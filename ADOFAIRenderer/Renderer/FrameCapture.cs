using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ADOFAIRenderer.Renderer
{
    internal sealed class FrameCapture : IDisposable
    {
        private sealed class CameraState
        {
            public Camera Camera;
            public RenderTexture Target;
            public float Aspect;
            public bool Enabled;
            public bool Orthographic;
            public float OrthographicSize;
            public Vector3 Position;
            public Quaternion Rotation;
        }
        private sealed class CanvasState
        {
            public Canvas Canvas;
            public bool Enabled;
        }
        private sealed class Pending
        {
            public FFmpegEncoder.Frame Frame;
            public AsyncGPUReadbackRequest Request;
            public bool Ready;
            public Exception Error;
            public long SubmittedAt;
            public long CompletedAt;
            public long CopyTicks;

            public void Reset(FFmpegEncoder.Frame frame, long submittedAt)
            {
                Frame = frame;
                Request = default(AsyncGPUReadbackRequest);
                Ready = false;
                Error = null;
                SubmittedAt = submittedAt;
                CompletedAt = 0;
                CopyTicks = 0;
            }

            public void Complete(AsyncGPUReadbackRequest request)
            {
                if (Ready) return;
                var copyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (request.hasError) throw new InvalidOperationException("GPU readback failed at frame " + Frame.Index);
                    var data = request.GetData<byte>();
                    if (data.Length != Frame.Bytes.Length) throw new InvalidOperationException("Unexpected GPU frame size.");
                    data.CopyTo(Frame.Bytes);
                }
                catch (Exception ex) { Error = ex; }
                finally
                {
                    CopyTicks = System.Diagnostics.Stopwatch.GetTimestamp() - copyStart;
                    CompletedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    Ready = true;
                }
            }
        }
        private readonly List<CameraState> cameras = new List<CameraState>();
        private readonly List<CanvasState> canvases = new List<CanvasState>();
        private readonly Queue<Pending> pending = new Queue<Pending>();
        private readonly Stack<Pending> reusable = new Stack<Pending>();
        private readonly FFmpegEncoder encoder;
        private readonly RenderTexture target;
        private readonly int width;
        private readonly int height;
        private readonly scrCamera gameCamera;
        private readonly float originalZoomSize;
        private readonly Vector2 originalOffset;
        private readonly int originalPositionStateInt;
        private readonly PositionState originalPositionState;
        private readonly bool overlayActive, quadActive;
        private readonly int mainMask;
        private Texture2D fallback;
        private bool disposed;
        private long readbackWaitTicks;
        private long readbackCopyTicks;
        private long readbackLatencyTicks;
        private int peakPending;
        public double BackpressureSeconds { get; private set; }
        public double ReadbackWaitSeconds => readbackWaitTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        public double ReadbackCopySeconds => readbackCopyTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        public double ReadbackLatencySeconds => readbackLatencyTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        public int PendingReadbacks => pending.Count;
        public int PeakPendingReadbacks => peakPending;

        public FrameCapture(FFmpegEncoder encoder, int width, int height)
        {
            this.encoder = encoder;
            this.width = width;
            this.height = height;
            gameCamera = scrCamera.instance;
            if (gameCamera == null || gameCamera.Bgcamstatic == null || gameCamera.BGcam == null || gameCamera.camobj == null)
                throw new InvalidOperationException("ADOFAI camera chain is not available.");
            originalZoomSize = gameCamera.zoomSize;
            originalOffset = gameCamera.offset;
            originalPositionStateInt = gameCamera.positionStateInt;
            originalPositionState = gameCamera.positionState;
            overlayActive = gameCamera.Overlaycam != null && gameCamera.Overlaycam.gameObject.activeSelf;
            quadActive = gameCamera.quad != null && gameCamera.quad.activeSelf;
            mainMask = gameCamera.camobj.cullingMask;
            target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                name = "ADOFAI Frame", antiAliasing = 1, useMipMap = false, autoGenerateMips = false
            };
            try
            {
                if (!target.Create()) throw new InvalidOperationException("Cannot allocate render target.");
                var previous = RenderTexture.active;
                try { RenderTexture.active = target; GL.Clear(true, true, Color.black); }
                finally { RenderTexture.active = previous; }
                Add(gameCamera.Bgcamstatic); Add(gameCamera.BGcam); Add(gameCamera.camobj);
                // Overlaycam presents the already composited RT on a quad. Capturing
                // it again would feed our own output back into itself.
                if (gameCamera.Overlaycam != null) gameCamera.Overlaycam.gameObject.SetActive(false);
                if (gameCamera.quad != null) gameCamera.quad.SetActive(false);
                foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                {
                    // Keep world-space level decorations; exclude editor/game HUD
                    // and third-party screen-space overlays from the three cameras.
                    if (!canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace) continue;
                    canvases.Add(new CanvasState { Canvas = canvas, Enabled = canvas.enabled });
                    canvas.enabled = false;
                }
                if (!SystemInfo.supportsAsyncGPUReadback)
                    fallback = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Bind();
            }
            catch { Dispose(); throw; }
        }
        private void Add(Camera camera)
        {
            if (cameras.Exists(s => s.Camera == camera)) return;
            cameras.Add(new CameraState {
                Camera = camera,
                Target = camera.targetTexture,
                Aspect = camera.aspect,
                Enabled = camera.enabled,
                Orthographic = camera.orthographic,
                OrthographicSize = camera.orthographicSize,
                Position = camera.transform.position,
                Rotation = camera.transform.rotation
            });
        }
        public void Bind()
        {
            if (gameCamera.Overlaycam != null) gameCamera.Overlaycam.gameObject.SetActive(false);
            if (gameCamera.quad != null) gameCamera.quad.SetActive(false);
            foreach (var state in canvases) if (state.Canvas != null) state.Canvas.enabled = false;
            foreach (var state in cameras)
            {
                if (state.Camera == null) throw new InvalidOperationException("A render camera was destroyed.");
                // Own the camera output for the duration of the render. The old
                // path let Unity draw these cameras to the game window and then
                // called Camera.Render again into this texture, doubling the
                // scene-rendering work for every encoded frame. RendererController
                // calls Bind from its last LateUpdate, immediately before Unity's
                // normal camera pass, so that pass can be captured directly.
                state.Camera.targetTexture = target;
                state.Camera.aspect = width / (float)height;
                // Camera.Render ignored the component's enabled flag on the old
                // manual path; keep that behavior while using the automatic pass.
                state.Camera.enabled = true;
            }
        }
        public void Capture(long index)
        {
            Drain(false);
            if (!encoder.TryRent(out var buffer))
            {
                long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                // Never yield a Unity frame under backpressure: that would advance
                // tweens/particles while the song clock and output frame stand still.
                Drain(true);
                buffer = encoder.Rent();
                BackpressureSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - waitStart)
                    / (double)System.Diagnostics.Stopwatch.Frequency;
            }
            buffer.Index = index;
            if (fallback != null)
            {
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    fallback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    fallback.GetRawTextureData<byte>().CopyTo(buffer.Bytes);
                }
                finally { RenderTexture.active = previous; }
                encoder.Submit(buffer);
                return;
            }
            var frame = reusable.Count > 0 ? reusable.Pop() : new Pending();
            frame.Reset(buffer, System.Diagnostics.Stopwatch.GetTimestamp());
            // Copy in the callback; Unity request data is only valid for one frame.
            frame.Request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, frame.Complete);
            pending.Enqueue(frame);
            if (pending.Count > peakPending) peakPending = pending.Count;
        }
        public void Drain(bool wait)
        {
            while (pending.Count > 0)
            {
                var frame = pending.Peek();
                if (!frame.Ready && wait)
                {
                    var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    frame.Request.WaitForCompletion();
                    frame.Complete(frame.Request);
                    readbackWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - waitStart;
                }
                if (!frame.Ready) break;
                if (frame.Error != null) throw new InvalidOperationException("Capture failed.", frame.Error);
                readbackCopyTicks += frame.CopyTicks;
                readbackLatencyTicks += frame.CompletedAt - frame.SubmittedAt;
                encoder.Submit(frame.Frame);
                pending.Dequeue();
                reusable.Push(frame);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Readbacks must release the texture before it can be destroyed, even
            // when encoder failure/cancellation means their frames are discarded.
            foreach (var frame in pending) if (!frame.Ready) frame.Request.WaitForCompletion();
            pending.Clear();
            foreach (var state in cameras) if (state.Camera != null) {
                state.Camera.targetTexture = state.Target;
                state.Camera.aspect = state.Aspect;
                state.Camera.enabled = state.Enabled;
                state.Camera.orthographic = state.Orthographic;
                state.Camera.orthographicSize = state.OrthographicSize;
                state.Camera.transform.SetPositionAndRotation(state.Position, state.Rotation);
            }
            // PrepareRenderCamera changes scrCamera's source state as well as
            // the Camera components. Restore both so the editor resumes with
            // exactly the same view after rendering.
            if (gameCamera != null) {
                gameCamera.zoomSize = originalZoomSize;
                gameCamera.offset = originalOffset;
                gameCamera.positionStateInt = originalPositionStateInt;
                gameCamera.positionState = originalPositionState;
            }
            foreach (var state in canvases) if (state.Canvas != null) {
                state.Canvas.enabled = state.Enabled;
            }
            if (gameCamera != null) {
                if (gameCamera.camobj != null) gameCamera.camobj.cullingMask = mainMask;
                if (gameCamera.Overlaycam != null) gameCamera.Overlaycam.gameObject.SetActive(overlayActive);
                if (gameCamera.quad != null) gameCamera.quad.SetActive(quadActive);
            }
            if (fallback != null) UnityEngine.Object.Destroy(fallback);
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
        }
    }
}
