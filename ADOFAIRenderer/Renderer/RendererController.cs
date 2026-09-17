using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ADOFAIRenderer.Patches;

namespace ADOFAIRenderer.Renderer
{
    public enum RenderState { Idle, Preparing, Rendering, Finishing, Completed, Failed, Cancelled }

    [DefaultExecutionOrder(32000)]
    public sealed class RendererController : MonoBehaviour
    {
        // Application.targetFrameRate = -1 lets Unity choose the platform's
        // default rate. On desktop that can follow the monitor refresh rate
        // (for example, exactly 200 Hz), which unintentionally caps offline
        // rendering even when vSync is disabled.
        private const int OfflineTargetFrameRate = 6000;
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
        public static RendererController Instance { get; private set; }
        public static bool ControlsTime => Instance != null && Instance.saved != null &&
            (Instance.State == RenderState.Preparing || Instance.State == RenderState.Rendering);
        internal static bool BgaModeActive => Instance != null && Instance.bgaModeForRun
            && (Instance.State == RenderState.Preparing || Instance.State == RenderState.Rendering
                || Instance.State == RenderState.Finishing);
        public RenderState State { get; private set; }
        public RenderClock Clock { get; private set; } = new RenderClock();
        public string Message { get; private set; } = "Open a Custom Level, then Render.";
        public string ToastText { get; private set; } = "Open a Custom Level, then press F6 to render.";
        public string ProgressText { get; private set; } = "";
        public string EtaText { get; private set; } = "";
        public string SpeedText { get; private set; } = "";
        public string OutputPath { get; private set; } = "";
        public string FFmpegPath = "";
        private readonly System.Diagnostics.Stopwatch renderTimer = new System.Diagnostics.Stopwatch();
        public double GenerationFps => renderTimer.Elapsed.TotalSeconds > 0 ? CapturedFrames / renderTimer.Elapsed.TotalSeconds : 0;
        public double ElapsedSeconds => renderTimer.Elapsed.TotalSeconds;
        public double EstimatedRemainingSeconds
        {
            get
            {
                if (TotalFrames <= 0 || CapturedFrames <= 0 || GenerationFps <= 0) return double.NaN;
                return Math.Max(0.0, (TotalFrames - CapturedFrames) / GenerationFps);
            }
        }
        public double RenderSpeedMultiplier => Clock != null && Clock.Fps > 0
            ? GenerationFps / Clock.Fps : 0;
        public double CaptureWaitSeconds { get; private set; }
        public double GameFrameSeconds => gameFrameTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        public double ReadbackWaitSeconds => capture != null ? capture.ReadbackWaitSeconds : 0;
        public double ReadbackCopySeconds => capture != null ? capture.ReadbackCopySeconds : 0;
        public double ReadbackLatencySeconds => capture != null ? capture.ReadbackLatencySeconds : 0;
        public int PendingReadbacks => capture != null ? capture.PendingReadbacks : 0;
        public int PeakPendingReadbacks => capture != null ? capture.PeakPendingReadbacks : 0;
        public double EncoderWriteSeconds => encoder != null ? encoder.WriteSeconds : 0;
        public int EncoderQueueDepth => encoder != null ? encoder.QueueDepth : 0;
        public int PeakEncoderQueueDepth => encoder != null ? encoder.PeakQueueDepth : 0;
        public long WrittenFrames => encoder != null ? encoder.WrittenFrames : 0;
        public double AudioCaptureSeconds => audio != null ? audio.CaptureSeconds : 0;
        public double FinalizationSeconds => finalizationTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        public long TotalFrames { get; private set; }
        public long CapturedFrames { get; private set; }
        public bool Busy => State == RenderState.Preparing || State == RenderState.Rendering || State == RenderState.Finishing
            || rpcLoadRoutine != null;
        private FrameCapture capture;
        private FFmpegEncoder encoder;
        private SavedState saved;
        private Coroutine routine;
        private scnGame level;
        private scnEditor editor;
        private bool cancellation;
        private string partialPath;
        private string audioPath, muxPath;
        private GameAudioCapture audio;
        private BgaRenderState bga;
        private bool bgaModeForRun;
        private double scheduledMusicStartDsp;
        private double scheduledMusicLengthSeconds;
        private float toastUntil;
        private bool captureAudioForRun;
        private bool openOutputFolderForRun;
        private RenderProfile profile;
        private float escapeHeldAt = -1f;
        private bool forceCancelTriggered;
        private bool processPriorityChanged;
        private System.Diagnostics.ProcessPriorityClass processPriorityBefore;
        private long gameFrameTicks;
        private long finalizationTicks;
        private readonly ConcurrentQueue<object> rpcCommands = new ConcurrentQueue<object>();
        private Coroutine rpcLoadRoutine;
        private RpcRenderJob activeRpcJob;
        private double nextProgressUpdateAt;

        private void Awake() { Instance = this; }
        public void StartRender()
        {
            if (Busy || !Main.Enabled) return;
            if (FfmpegInstaller.IsDownloading)
            {
                Message = FfmpegInstaller.StatusMessage;
                ShowToast(Message, 5f, false);
                return;
            }
            State = RenderState.Preparing;
            cancellation = false;
            CapturedFrames = TotalFrames = 0;
            OutputPath = ""; partialPath = null;
            audioPath = muxPath = null;
            renderTimer.Reset();
            nextProgressUpdateAt = 0;
            CaptureWaitSeconds = 0;
            ProgressText = EtaText = SpeedText = "";
            gameFrameTicks = 0;
            finalizationTicks = 0;
            ClearQueuedInput();
            escapeHeldAt = -1f;
            forceCancelTriggered = false;
            var settings = Main.Settings ?? new RendererSettings();
            var options = activeRpcJob != null ? activeRpcJob.Options : null;
            bgaModeForRun = options?.BgaMode ?? settings.BgaMode;
            profile = settings.ResolveProfile(options?.Preset, options?.Width, options?.Height,
                options?.Fps, options?.BitrateMbps, options?.EndDelaySeconds);
            Clock = new RenderClock(profile.Fps);
            Message = string.Format("Preparing {0}x{1} @ {2} fps ({3} Mbps, {4})...",
                profile.Width, profile.Height, profile.Fps, profile.BitrateMbps, profile.FfmpegCodec);
            captureAudioForRun = activeRpcJob != null
                ? activeRpcJob.CaptureAudio
                : Main.Settings == null || Main.Settings.CaptureAudio;
            openOutputFolderForRun = settings.OpenOutputFolder;
            activeRpcJob?.SetState(RpcJobState.Preparing);
            ShowToast(Message, 4f);
            routine = StartCoroutine(GuardedRun());
        }
        public void Cancel() { if (Busy) cancellation = true; }

        internal void EnqueueRpcRender(RpcRenderRequest request)
        {
            if (request != null && request.Job != null) rpcCommands.Enqueue(request);
        }

        internal void EnqueueRpcCancel(RpcCancelRequest request)
        {
            if (request != null && !string.IsNullOrEmpty(request.JobId)) rpcCommands.Enqueue(request);
        }

        internal bool ToastVisible => Time.unscaledTime <= toastUntil;

        internal void ShowToast(string text, float seconds, bool useGameNotification = true)
        {
            ToastText = text ?? string.Empty;
            toastUntil = Time.unscaledTime + Mathf.Max(0.5f, seconds);
            if (!useGameNotification || ADOBase.editor == null) return;
            try
            {
                // This is ADOFAI's own editor notification bar. The fallback
                // OnGUI toast below remains visible while the render canvas is
                // temporarily hidden from the captured camera.
                ADOBase.editor.ShowNotification(ToastText, null, seconds);
            }
            catch (Exception ex) { Main.Entry.Logger.Log("Game notification unavailable: " + ex.Message); }
        }

        private void ShowProgressToast()
        {
            if (TotalFrames <= 0) return;
            var progress = 100.0 * CapturedFrames / TotalFrames;
            ProgressText = string.Format("{0:F1}%   {1} / {2} frames   {3:F1} fps",
                progress, CapturedFrames, TotalFrames, GenerationFps);
            EtaText = string.Format("ETA {0}   •   finishes around {1}",
                FormatDuration(EstimatedRemainingSeconds), FormatFinishTime(EstimatedRemainingSeconds));
            SpeedText = string.Format("{0:F2}x realtime   •   elapsed {1}",
                RenderSpeedMultiplier, FormatDuration(ElapsedSeconds));
            ToastText = string.Format("Rendering  {0:F1}%  |  {1} / {2} frames  |  {3:F1} fps  |  ETA {4}",
                progress, CapturedFrames, TotalFrames, GenerationFps, FormatDuration(EstimatedRemainingSeconds));
            toastUntil = Time.unscaledTime + 1.0f;
        }
        private IEnumerator GuardedRun()
        {
            var run = Run();
            try
            {
                while (!cancellation)
                {
                    object next;
                    try { if (!run.MoveNext()) break; next = run.Current; }
                    catch (Exception ex) { Fail(ex); break; }
                    yield return next;
                }
                if (cancellation) { State = RenderState.Cancelled; Message = "Render cancelled."; ShowToast(Message, 5f); }
            }
            finally { (run as IDisposable)?.Dispose(); Cleanup(); routine = null; }
        }
        private IEnumerator Run()
        {
            // Start at a frame boundary; OnGUI can run several times per frame.
            yield return EndOfFrame;
            editor = ADOBase.editor;
            level = editor != null ? editor.customLevel : ADOBase.customLevel;
            ValidateLoadedLevel();
            if (GCS.d_oldConductor || GCS.d_webglConductor)
                throw new InvalidOperationException("The installed conductor must use its standard DSP timing mode.");
            var settings = Main.Settings ?? new RendererSettings();
            var directory = settings.ResolveOutputDirectory();
            Directory.CreateDirectory(directory);
            var name = SanitizeName(ADOBase.controller.levelName);
            OutputPath = Path.Combine(directory, name + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
            partialPath = Path.ChangeExtension(OutputPath, ".partial.mp4");
            audioPath = Path.ChangeExtension(OutputPath, ".partial.wav");
            muxPath = Path.ChangeExtension(OutputPath, ".mux.mp4");
            saved = new SavedState();
            MaximizeRenderPerformance();
            FFmpegPath = ResolveFfmpegExecutable(settings);
            encoder = new FFmpegEncoder(FFmpegPath, partialPath, profile.Width, profile.Height,
                profile.Fps, profile.BitrateMbps, profile.FfmpegPreset, !captureAudioForRun,
                profile.FfmpegCodec);
            if (editor != null)
            {
                Main.Entry.Logger.Log("Preparing editor render: playMode=" + editor.playMode
                    + ", strictlyEditing=" + editor.inStrictlyEditingMode + ", tiles=" + editor.floors.Count);
                // playMode includes paused playback. The editor's initial setup
                // does not initialize inStrictlyEditingMode, so that flag cannot
                // tell whether a freshly opened editor is ready to render.
                if (editor.playMode) editor.SwitchToEditMode();
            }
            Time.captureFramerate = profile.Fps;
            Time.timeScale = 1;
            DG.Tweening.DOTween.useSmoothDeltaTime = false;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = OfflineTargetFrameRate;
            Application.runInBackground = true;
            if (!captureAudioForRun) AudioListener.volume = 0;
            AudioListener.pause = false;
            Persistence.skipIntroBehavior = SkipIntroBehavior.Off;
            GCS.checkpointNum = 0;
            RDC.auto = false; // Preserve the normal countdown, avoiding the editor's fast-takeoff shortcut.
            RDC.noHud = true;
            RDC.noAutoHud = true;
            yield return null;
            if (editor != null)
            {
                editor.SelectFloor(editor.floors[0], cameraJump: false);
                editor.Play();
            }
            else
            {
                level.ResetScene();
                if (!level.Play(0)) throw new InvalidOperationException("Custom Level playback could not start.");
                // The official preparation coroutine warms filters over two frames.
                int preparationFrames = 0;
                while (level.isLoading)
                {
                    if (++preparationFrames > 600) throw new TimeoutException("Custom Level preparation did not finish.");
                    yield return null;
                }
                AbortStartPrompt();
                ADOBase.conductor.Start();
                level.FinishCustomLevelLoading(0);
                ADOBase.controller.Start_Rewind(0);
            }
            // editor.Play() leaves one frame of camera setup pending. Let that
            // setup run before taking ownership of the gameplay cameras, then
            // explicitly restore the normal gameplay framing below.
            yield return null;
            RDC.auto = true;
            ADOBase.controller.noFail = true;
            ADOBase.controller.paused = false;
            ADOBase.controller.enabled = true;
            Time.timeScale = 1;
            ADOBase.conductor.dspTime = Clock.DspTime;
            ADOBase.conductor.songposition_minusi = Clock.SongPosition(ADOBase.conductor.dspTimeSong,
                ADOBase.conductor.song.pitch, 0.0, scrConductor.calibration_i);
            capture = new FrameCapture(encoder, profile.Width, profile.Height);
            PrepareRenderCamera();
            if (bgaModeForRun)
            {
                bga = BgaRenderState.Capture();
                Main.Entry.Logger.Log("BGA mode enabled: hidden renderers=" + bga.HiddenRendererCount);
            }
            State = RenderState.Rendering;
            renderTimer.Start();
            ApplyFramePacing();
            Message = string.Format("Rendering {0}x{1} @ {2} fps (hold Escape 1s to force-cancel)",
                profile.Width, profile.Height, profile.Fps);
            ShowToast(Message, 2f, false);
            // Every output frame follows one complete game Update/LateUpdate/render.
            while (true)
            {
                var gameFrameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                yield return null;
                yield return EndOfFrame;
                if (level == null || (editor != null ? editor.customLevel : ADOBase.customLevel) != level || ADOBase.controller == null || ADOBase.conductor == null)
                    throw new InvalidOperationException("The level was unloaded during rendering.");
                encoder.Check();
                gameFrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gameFrameStart;
                capture.Capture(Clock.FrameIndex);
                CaptureWaitSeconds = capture.BackpressureSeconds;
                if (captureAudioForRun)
                {
                    if (audio == null) throw new InvalidOperationException("The game did not initialize game audio before frame zero.");
                    audio.CaptureFrame();
                }
                CapturedFrames++;
                // Throttle presentation work by wall time. At high offline
                // generation rates, updating this every six output frames can
                // format and rebuild the IMGUI text dozens of times per second.
                var elapsed = renderTimer.Elapsed.TotalSeconds;
                if (elapsed >= nextProgressUpdateAt)
                {
                    ShowProgressToast();
                    nextProgressUpdateAt = elapsed + 0.25;
                }
                if (TotalFrames > 0 && CapturedFrames >= TotalFrames)
                {
                    var player = ADOBase.controller.playerOne;
                    var floors = ADOBase.lm.listFloors;
                    if (player == null || player.currFloor == null || player.currFloor.seqID < floors.Count - 1)
                        throw new InvalidOperationException("Autoplay did not reach the last tile at the expected end time.");
                    break;
                }
                if (TotalFrames == 0 && Clock.Time > 10)
                    throw new InvalidOperationException("The game did not schedule level playback.");
                Clock.Advance();
            }
            State = RenderState.Finishing;
            renderTimer.Stop();
            Message = "Finalizing MP4...";
            ShowToast(Message, 8f, false);
            var finalizationStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                capture.Drain(true);
                // No Unity yields during finalization; gameplay must not progress further.
                encoder.Finish(CapturedFrames);
                if (audio != null)
                {
                    audio.Complete(CapturedFrames, Clock.Fps);
                    audio.Dispose();
                    FFmpegEncoder.MuxAudio(FFmpegPath, partialPath, audioPath, muxPath);
                    File.Move(muxPath, OutputPath);
                    File.Delete(partialPath); File.Delete(audioPath);
                }
                else File.Move(partialPath, OutputPath);
            }
            finally { finalizationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - finalizationStart; }
            State = RenderState.Completed;
            Message = "Completed: " + CapturedFrames + " frames."
                + (audio != null && audio.Peak < 0.000001f ? " Audio mix was silent; check game sound settings." : "");
            ShowToast(Message, 8f);
            Main.Entry.Logger.Log(string.Format("Completed: {0} frames in {1:F2}s, {2:F1} frames/s ({3:F2}x target). Video={4}x{5}@{6}fps {7}Mbps {8}/{9}. Audio={10}. Capture/encoder wait={11:F2}s. Metrics: game={12:F2}s, readbackWait={13:F2}s, readbackLatency={14:F2}s, readbackCopy={15:F2}s, pendingPeak={16}, encoderWrite={17:F2}s, encoderQueuePeak={18}, written={19}, audioCapture={20:F2}s, finalization={21:F2}s. Output={22}",
                CapturedFrames, ElapsedSeconds, GenerationFps, GenerationFps / Clock.Fps,
                profile.Width, profile.Height, profile.Fps, profile.BitrateMbps, profile.FfmpegCodec, profile.FfmpegPreset,
                audio != null, CaptureWaitSeconds,
                GameFrameSeconds, ReadbackWaitSeconds, ReadbackLatencySeconds, ReadbackCopySeconds,
                PeakPendingReadbacks, EncoderWriteSeconds, PeakEncoderQueueDepth, WrittenFrames,
                AudioCaptureSeconds, FinalizationSeconds, OutputPath));
            if (openOutputFolderForRun) OpenOutputFolder();
        }

        private void OpenOutputFolder()
        {
            var directory = Path.GetDirectoryName(OutputPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                Main.Entry.Logger.Log("Could not open render output folder: directory is unavailable.");
                return;
            }

            try
            {
                var windows = Application.platform == RuntimePlatform.WindowsPlayer
                    || Application.platform == RuntimePlatform.WindowsEditor;
                var mac = Application.platform == RuntimePlatform.OSXPlayer
                    || Application.platform == RuntimePlatform.OSXEditor;
                var fileName = windows ? "explorer.exe" : mac ? "open" : "xdg-open";
                var arguments = windows
                    ? "/select,\"" + OutputPath + "\""
                    : mac
                        ? "-R " + QuoteProcessArgument(OutputPath)
                        : QuoteProcessArgument(directory);

                using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null) throw new InvalidOperationException("The file manager process did not start.");
                }
            }
            catch (Exception ex)
            {
                // Opening a folder is a convenience and must not turn a
                // successfully completed render into a failed one.
                Main.Entry.Logger.Error("Could not open render output folder: " + ex.Message);
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        internal void ScheduleAudio(scrConductor conductor)
        {
            double pitch = conductor.song.pitch;
            double countdown = conductor.separateCountdownTime
                ? conductor.crotchetAtStart * conductor.adjustedCountdownTicks / pitch : 0.0;
            double songStart = conductor.dspTimeSong + countdown;
            scheduledMusicStartDsp = songStart;
            scheduledMusicLengthSeconds = LongestClipLength(conductor, pitch);
            if (!captureAudioForRun) return;

            audio = new GameAudioCapture();
            audio.Begin(audioPath);
            // AudioRenderer controls the DSP timeline. Anchor once, then keep
            // advancing video strictly by frame index; never read wall time to seek.
            Clock.AnchorDsp(AudioSettings.dspTime);
            conductor.dspTime = Clock.DspTime;
            conductor.dspTimeSong = conductor.dspTime + 1.0;
            countdown = conductor.separateCountdownTime
                ? conductor.crotchetAtStart * conductor.adjustedCountdownTicks / pitch : 0.0;
            songStart = conductor.dspTimeSong + countdown;
            scheduledMusicStartDsp = songStart;
            scheduledMusicLengthSeconds = LongestClipLength(conductor, pitch);
            foreach (var source in new[] { conductor.song, conductor.song2, conductor.song3 })
            {
                if (source == null || source.clip == null) continue;
                source.Stop(); source.time = 0; source.PlayScheduled(songStart);
            }
            conductor.PlayHitTimes();
            if (audio != null)
                Main.Entry.Logger.Log("Game audio started: " + audio.SampleRate + " Hz, " + audio.Channels + " channels.");
        }
        internal void MusicScheduled()
        {
            var conductor = ADOBase.conductor;
            double pitch = conductor.song.pitch;
            if (pitch <= 0 || double.IsNaN(pitch) || double.IsInfinity(pitch))
                throw new InvalidOperationException("Invalid song pitch.");
            var floors = ADOBase.lm.listFloors;
            double last = floors[floors.Count - 1].entryTime;
            double endDelay = profile != null ? profile.EndDelaySeconds : 2.0;
            if (double.IsNaN(endDelay) || double.IsInfinity(endDelay) || endDelay < 0) endDelay = 2.0;
            double chartEnd = conductor.dspTimeSong - Clock.DspOrigin
                + scrConductor.calibration_i + last / pitch;
            double musicEnd = scheduledMusicStartDsp - Clock.DspOrigin + scheduledMusicLengthSeconds;
            double end = Math.Max(chartEnd, musicEnd) + endDelay;
            if (double.IsNaN(end) || double.IsInfinity(end) || end <= 0)
                throw new InvalidOperationException("Invalid final tile time.");
            TotalFrames = checked((long)Math.Ceiling(end * Clock.Fps) + 1);
            Main.Entry.Logger.Log(string.Format("Render end: chart={0:F2}s, music={1:F2}s, delay={2:F2}s, total frames={3}",
                chartEnd, musicEnd, endDelay, TotalFrames));
        }

        private static double LongestClipLength(scrConductor conductor, double pitch)
        {
            double longest = 0.0;
            foreach (var source in new[] { conductor.song, conductor.song2, conductor.song3 })
            {
                if (source == null || source.clip == null) continue;
                longest = Math.Max(longest, source.clip.length / pitch);
            }
            return longest;
        }
        private void PrepareRenderCamera()
        {
            var camera = scrCamera.instance;
            if (camera == null) return;
            // Do not call MoveCameraToPlayer or Refocus here. Those methods
            // overwrite the camera state/tweens created by the level's Move
            // Camera events. The game has already applied its normal camera
            // update during the preparation frames; FrameCapture only redirects
            // the existing cameras to the render target.
            var orthoSize = camera.camobj != null && camera.camobj.orthographic
                ? camera.camobj.orthographicSize : float.NaN;
            Main.Entry.Logger.Log(string.Format("Prepared render camera: zoom={0:F3}, ortho={1:F3}, player={2}",
                camera.zoomSize, orthoSize, ADOBase.controller != null && ADOBase.controller.playerOne != null));
        }
        private void ValidateLoadedLevel()
        {
            if (level == null)
                throw new InvalidOperationException("No Custom Level instance is available. Open a level in the editor or Custom Level player.");
            if (level.levelData == null)
                throw new InvalidOperationException("The Custom Level has no loaded chart data.");
            if (editor != null)
            {
                // scnEditor owns loading and the level maker while editing.
                // scnGame.isLoading is cleared by the gameplay start coroutine;
                // it can remain true for a fully loaded editor chart.
                if (editor.isLoading)
                    throw new InvalidOperationException("The editor is still loading the chart.");
                if (level.levelMaker == null || editor.floors == null || editor.floors.Count < 2)
                    throw new InvalidOperationException("The editor chart needs at least two tiles.");
            }
            else
            {
                if (level.isLoading)
                    throw new InvalidOperationException("Custom Level gameplay is still loading. Wait for the start prompt.");
                if (ADOBase.lm == null || ADOBase.lm.listFloors == null || ADOBase.lm.listFloors.Count < 2)
                    throw new InvalidOperationException("Custom Level gameplay has no playable tile path.");
            }
            if (ADOBase.controller == null || ADOBase.conductor == null)
                throw new InvalidOperationException("The gameplay controller or conductor is not ready.");
        }
        private static void AbortStartPrompt()
        {
            var controller = ADOBase.controller;
            var field = AccessTools.Field(typeof(scrController), "waitForStartCoCallCount");
            field.SetValue(controller, (int)field.GetValue(controller) + 1);
            scrUIController.instance.txtPressToStart.GetComponent<scrPressToStart>().HideText();
        }
        private void LateUpdate()
        {
            if (State != RenderState.Rendering) return;
            try { bga?.Apply(); FlashLayerPatch.Apply(); ApplyFramePacing(); capture.Bind(); }
            catch (Exception ex) { Fail(ex); StopAndClean(); }
        }
        private void Update()
        {
            ProcessRpcCommands();
            UpdateRpcJob();
            if (Busy)
            {
                UpdateForceCancelKey();
                return;
            }
            escapeHeldAt = -1f;
            forceCancelTriggered = false;
            if (Input.GetKeyDown(KeyCode.F6) && Main.Enabled && ADOBase.editor != null)
                StartRender();
        }

        private void UpdateForceCancelKey()
        {
            if (!Input.GetKey(KeyCode.Escape))
            {
                escapeHeldAt = -1f;
                forceCancelTriggered = false;
                return;
            }
            if (escapeHeldAt < 0f) escapeHeldAt = Time.unscaledTime;
            if (!forceCancelTriggered && Time.unscaledTime - escapeHeldAt >= 1f)
            {
                forceCancelTriggered = true;
                ForceCancel();
            }
        }

        private void ForceCancel()
        {
            cancellation = true;
            if (rpcLoadRoutine != null) { StopCoroutine(rpcLoadRoutine); rpcLoadRoutine = null; }
            if (routine != null) { StopCoroutine(routine); routine = null; }
            State = RenderState.Cancelled;
            Message = "Render force-cancelled.";
            ShowToast(Message, 5f);
            activeRpcJob?.Cancel();
            Cleanup();
            activeRpcJob = null;
        }
        private void OnGUI()
        {
            if (!Main.Enabled) return;
            // Rendering temporarily owns the gameplay cameras and editor
            // overlays. Cover the presentation surface so a camera or canvas
            // target change can never flash through to the player window.
            if (Busy && State != RenderState.Rendering && Event.current.type == EventType.Repaint)
                ADOFAIRenderer.UI.RendererWindow.DrawBackdrop();
            if (!ToastVisible) return;
            ADOFAIRenderer.UI.RendererWindow.DrawToast(this);
        }
        private void MaximizeRenderPerformance()
        {
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                processPriorityBefore = process.PriorityClass;
                if (processPriorityBefore != System.Diagnostics.ProcessPriorityClass.High)
                {
                    process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
                    processPriorityChanged = true;
                }
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Log("Could not raise renderer process priority: " + ex.Message);
            }
        }

        private void RestoreRenderPerformance()
        {
            if (!processPriorityChanged) return;
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = processPriorityBefore; }
            catch (Exception ex) { Main.Entry.Logger.Log("Could not restore renderer process priority: " + ex.Message); }
            finally { processPriorityChanged = false; }
        }

        internal static string ResolveFfmpegExecutable(RendererSettings settings)
        {
            var configured = settings.ResolveFfmpegExecutable(Main.Entry.Path);
            if (!string.IsNullOrEmpty(configured)) return configured;

            var bundled = FfmpegInstaller.GetBundledExecutable(Main.Entry.Path);
            if (!string.IsNullOrEmpty(bundled) && File.Exists(bundled)) return bundled;

            var windows = Application.platform == RuntimePlatform.WindowsPlayer
                || Application.platform == RuntimePlatform.WindowsEditor;
            var names = windows ? new[] { "ffmpeg.exe", "ffmpeg" } : new[] { "ffmpeg", "ffmpeg.exe" };
            var candidates = new[]
            {
                Path.Combine(Main.Entry.Path, names[0]),
                Path.Combine(Main.Entry.Path, names[1])
            };
            foreach (var local in candidates)
            {
                if (File.Exists(local)) return local;
            }

            // Let the operating system resolve a system-installed FFmpeg from
            // PATH. This is the normal setup on macOS and Linux.
            return names[0];
        }

        internal static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "calculating...";
            var span = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            if (span.TotalHours >= 1) return span.ToString(@"h\:mm\:ss");
            return span.ToString(@"mm\:ss");
        }

        internal static string FormatFinishTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--:--";
            return DateTime.Now.AddSeconds(seconds).ToString("HH:mm:ss");
        }

        private void ProcessRpcCommands()
        {
            while (rpcCommands.TryDequeue(out var command))
            {
                var render = command as RpcRenderRequest;
                if (render != null)
                {
                    if (activeRpcJob != null || Busy)
                    {
                        render.Job.Fail("A render is already in progress.");
                        continue;
                    }
                    activeRpcJob = render.Job;
                    cancellation = false;
                    activeRpcJob.SetState(RpcJobState.Loading);
                    rpcLoadRoutine = StartCoroutine(LoadRpcLevelAndStart(render.Job));
                    continue;
                }

                var cancel = command as RpcCancelRequest;
                if (cancel != null && activeRpcJob != null &&
                    string.Equals(activeRpcJob.Id, cancel.JobId, StringComparison.OrdinalIgnoreCase))
                {
                    Cancel();
                }
            }
        }

        private IEnumerator LoadRpcLevelAndStart(RpcRenderJob job)
        {
            var core = LoadRpcLevelAndStartCore(job);
            while (true)
            {
                object next = null;
                bool hasNext;
                Exception failure = null;
                try
                {
                    hasNext = core.MoveNext();
                    if (hasNext) next = core.Current;
                }
                catch (Exception ex)
                {
                    hasNext = false;
                    failure = ex;
                }
                if (failure != null)
                {
                    HandleRpcPreparationFailure(job, failure);
                    yield break;
                }
                if (!hasNext) yield break;
                yield return next;
            }
        }

        private IEnumerator LoadRpcLevelAndStartCore(RpcRenderJob job)
        {
            while (FfmpegInstaller.IsDownloading)
            {
                if (cancellation) throw new OperationCanceledException();
                yield return null;
            }
            if (!File.Exists(job.LevelPath))
                throw new FileNotFoundException("Level file does not exist.", job.LevelPath);

            var waitFrames = 0;
            var editorDeadline = Time.realtimeSinceStartup + 60f;
            var sceneRequested = false;
            Main.Entry.Logger.Log("RPC preparing level: " + job.LevelPath + ", loader=" + (scrLoader.instance != null));
            while (ADOBase.editor == null || (sceneRequested && !ADOBase.isLevelEditor))
            {
                if (cancellation) throw new OperationCanceledException();
                if (!sceneRequested && (waitFrames == 0 || waitFrames % 30 == 0)
                    && (scrLoader.instance != null || ADOBase.loader != null || ADOBase.controller != null || ADOBase.customLevel != null))
                {
                    try
                    {
                        OpenLevelEditorScene();
                        sceneRequested = true;
                        Main.Entry.Logger.Log("RPC requested scnEditor scene.");
                    }
                    catch (Exception ex)
                    {
                        Main.Entry.Logger.Log("Waiting for the game loader before opening the editor: " + ex.Message);
                    }
                }
                waitFrames++;
                if (waitFrames > 36000 || Time.realtimeSinceStartup > editorDeadline)
                    throw new TimeoutException("The level editor did not become available within 60 seconds.");
                yield return null;
            }

            var targetEditor = ADOBase.editor;
            if (targetEditor.playMode) targetEditor.SwitchToEditMode();
            var previousLevel = targetEditor.customLevel;
            targetEditor.OpenLevel(job.LevelPath);
            Main.Entry.Logger.Log("RPC dispatched editor.OpenLevel: customLevel=" + (previousLevel != null)
                + ", isLoading=" + targetEditor.isLoading);

            var sawLoading = false;
            var loaded = false;
            var loadDeadline = Time.realtimeSinceStartup + 120f;
            for (var frame = 0; frame < 72000 && Time.realtimeSinceStartup <= loadDeadline; frame++)
            {
                if (cancellation) throw new OperationCanceledException();
                yield return null;
                targetEditor = ADOBase.editor;
                if (targetEditor == null) continue;
                if (targetEditor.isLoading) sawLoading = true;
                var loadedLevel = targetEditor.customLevel;
                if (!targetEditor.isLoading && loadedLevel != null && loadedLevel.levelData != null
                    && targetEditor.floors != null && targetEditor.floors.Count > 1
                    && (sawLoading || loadedLevel != previousLevel
                        || (frame >= 5 && PathsEqual(loadedLevel.levelPath, job.LevelPath))))
                {
                    loaded = true;
                    break;
                }
            }
            if (!loaded)
            {
                var finalEditor = ADOBase.editor;
                var finalLevel = finalEditor != null ? finalEditor.customLevel : null;
                Main.Entry.Logger.Log("RPC level load state: editor=" + (finalEditor != null)
                    + ", isLoading=" + (finalEditor != null && finalEditor.isLoading)
                    + ", levelData=" + (finalLevel != null && finalLevel.levelData != null)
                    + ", floors=" + (finalEditor != null && finalEditor.floors != null ? finalEditor.floors.Count.ToString() : "null")
                    + ", previousSame=" + (finalLevel == previousLevel)
                    + ", path=" + (finalLevel != null ? finalLevel.levelPath : "null"));
                throw new TimeoutException("The requested level did not finish loading in the editor.");
            }
            if (cancellation) throw new OperationCanceledException();

            job.SetState(RpcJobState.Preparing);
            rpcLoadRoutine = null;
            StartRender();
        }

        private static void OpenLevelEditorScene()
        {
            if (scrLoader.instance != null) scrLoader.instance.GoToLevelEditor();
            else if (ADOBase.loader != null) ADOBase.loader.GoToLevelEditor();
            else if (ADOBase.controller != null) ADOBase.controller.GoToLevelEditor();
            else if (ADOBase.customLevel != null) ADOBase.customLevel.GoToLevelEditor();
            else throw new InvalidOperationException("The game loader is not ready.");
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        private void HandleRpcPreparationFailure(RpcRenderJob job, Exception ex)
        {
            rpcLoadRoutine = null;
            if (ex is OperationCanceledException)
            {
                State = RenderState.Cancelled;
                Message = "Render cancelled.";
                job.Cancel();
            }
            else
            {
                State = RenderState.Failed;
                Message = ex.Message;
                job.Fail(ex.Message);
                ShowToast("Render failed: " + Message, 10f);
                Main.Entry.Logger.Error("RPC render preparation: " + ex);
            }
            activeRpcJob = null;
        }

        private void UpdateRpcJob()
        {
            var job = activeRpcJob;
            if (job == null) return;
            job.SetProgress(CapturedFrames, TotalFrames, OutputPath);
            if (rpcLoadRoutine != null) job.SetState(RpcJobState.Loading);
            else if (State == RenderState.Preparing) job.SetState(RpcJobState.Preparing);
            else if (State == RenderState.Rendering) job.SetState(RpcJobState.Rendering);
            else if (State == RenderState.Finishing) job.SetState(RpcJobState.Finishing);
            else if (State == RenderState.Completed)
            {
                job.SetState(RpcJobState.Completed);
                job.SetProgress(CapturedFrames, TotalFrames, OutputPath);
            }
            else if (State == RenderState.Cancelled) job.Cancel();
            else if (State == RenderState.Failed) job.Fail(Message);

            if (rpcLoadRoutine == null && routine == null && !Busy &&
                (State == RenderState.Completed || State == RenderState.Cancelled || State == RenderState.Failed))
                activeRpcJob = null;
        }

        private static void ApplyFramePacing()
        {
            // Game settings or other mods may restore a cap after editor.Play.
            // Do not use -1 here: Unity may resolve it to the display refresh
            // rate, making a 200 Hz monitor a hard offline-render cap.
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != OfflineTargetFrameRate)
                Application.targetFrameRate = OfflineTargetFrameRate;
            if (UnityEngine.Rendering.OnDemandRendering.renderFrameInterval != 1)
                UnityEngine.Rendering.OnDemandRendering.renderFrameInterval = 1;
        }
        private void Fail(Exception ex)
        {
            State = RenderState.Failed;
            Message = ex.Message;
            ShowToast("Render failed: " + Message, 10f);
            Main.Entry.Logger.Error(ex.ToString());
        }
        internal void AbortWithError(Exception ex) { Fail(ex); StopAndClean(); }
        public void StopAndClean()
        {
            if (rpcLoadRoutine != null) { StopCoroutine(rpcLoadRoutine); rpcLoadRoutine = null; }
            if (routine != null) { StopCoroutine(routine); routine = null; }
            if (Busy) { State = RenderState.Cancelled; Message = "Render cancelled."; }
            Cleanup();
        }
        private void Cleanup()
        {
            // Clear patch ownership before calling any normal game reset methods.
            var restore = saved;
            saved = null;
            FlashLayerPatch.Restore();
            renderTimer.Stop();
            TryCleanup(() => capture?.Dispose()); capture = null;
            TryCleanup(() => encoder?.Dispose()); encoder = null;
            TryCleanup(() => audio?.Dispose()); audio = null;
            TryCleanup(() => bga?.Dispose()); bga = null;
            TryCleanup(RestoreRenderPerformance);
            if (restore != null)
            {
                // Reset playback with the user's autoplay setting, otherwise Play
                // would retain renderer fast-takeoff flags in the restored session.
                TryCleanup(restore.RestoreTiming);
                TryCleanup(() => {
                    var conductor = ADOBase.conductor;
                    if (conductor != null) {
                        var handle = AccessTools.Field(typeof(scrConductor), "startMusicCoroutine").GetValue(conductor) as Coroutine;
                        if (handle != null) conductor.StopCoroutine(handle);
                        conductor.Rewind();
                        conductor.song?.Stop(); conductor.song2?.Stop(); conductor.song3?.Stop();
                    }
                    if (editor != null) editor.SwitchToEditMode();
                    else if (level != null && ADOBase.customLevel == level && ADOBase.controller != null) {
                        level.ResetScene();
                        level.Play(0); // Return to the game's normal press-to-start preparation.
                    }
                });
                TryCleanup(restore.Restore);
            }
            if (State != RenderState.Completed && !string.IsNullOrEmpty(partialPath))
                TryCleanup(() => { if (File.Exists(partialPath)) File.Delete(partialPath); });
            foreach (var temporary in new[] { audioPath, muxPath })
                if (!string.IsNullOrEmpty(temporary)) TryCleanup(() => { if (File.Exists(temporary)) File.Delete(temporary); });
            ClearQueuedInput();
        }
        private static void ClearQueuedInput()
        {
            try { AsyncInputManager.ClearKeys(); } catch { }
        }
        private void TryCleanup(Action action)
        {
            try { action(); }
            catch (Exception ex) { Main.Entry.Logger.Error("Cleanup: " + ex); State = RenderState.Failed; Message = "Cleanup failed: " + ex.Message; }
        }
        private void OnDestroy() { StopAndClean(); if (Instance == this) Instance = null; }
        private void OnApplicationQuit() { StopAndClean(); }
        internal static string SanitizeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string((value ?? "Level").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
            return "Render_" + (string.IsNullOrEmpty(name) ? "Level" : name.Substring(0, Math.Min(80, name.Length)));
        }
        private sealed class SavedState
        {
            private readonly int captureRate = Time.captureFramerate, targetRate = Application.targetFrameRate, vsync = QualitySettings.vSyncCount, checkpoint = GCS.checkpointNum;
            private readonly float timeScale = Time.timeScale, volume = AudioListener.volume;
            private readonly bool auto = RDC.auto, noFail = ADOBase.controller.noFail, pauseAudio = AudioListener.pause, background = Application.runInBackground;
            private readonly bool smoothTweens = DG.Tweening.DOTween.useSmoothDeltaTime;
            private readonly bool noHud = RDC.noHud, noAutoHud = RDC.noAutoHud;
            private readonly int renderInterval = UnityEngine.Rendering.OnDemandRendering.renderFrameInterval;
            private readonly bool wasPaused = ADOBase.controller.paused, controllerEnabled = ADOBase.controller.enabled;
            private readonly SkipIntroBehavior intro = Persistence.skipIntroBehavior;
            private readonly int[] selection = ADOBase.editor != null ? ADOBase.editor.selectedFloors.Select(f => f.seqID).ToArray() : new int[0];
            public void Restore()
            {
                RestoreTiming();
                // Rendering always returns to editing, even if it was requested
                // during playback. Restoring the old unpaused flag here would
                // incorrectly turn editor.playMode back on with its conductor off.
                if (ADOBase.controller != null && ADOBase.editor == null)
                {
                    ADOBase.controller.paused = wasPaused;
                    ADOBase.controller.enabled = controllerEnabled;
                }
                var editor = ADOBase.editor;
                if (editor != null && selection.Length > 0 && editor.floors.Count > selection.Max())
                {
                    if (selection.Length == 1) editor.SelectFloor(editor.floors[selection[0]], cameraJump: false);
                    else editor.MultiSelectFloors(editor.floors[selection.Min()], editor.floors[selection.Max()], setSelectPoint: true);
                }
            }
            public void RestoreTiming()
            {
                Time.captureFramerate = captureRate; Time.timeScale = timeScale;
                DG.Tweening.DOTween.useSmoothDeltaTime = smoothTweens;
                UnityEngine.Rendering.OnDemandRendering.renderFrameInterval = renderInterval;
                Application.targetFrameRate = targetRate; QualitySettings.vSyncCount = vsync;
                Application.runInBackground = background;
                AudioListener.volume = volume; AudioListener.pause = pauseAudio;
                RDC.auto = auto; GCS.checkpointNum = checkpoint; Persistence.skipIntroBehavior = intro;
                RDC.noHud = noHud; RDC.noAutoHud = noAutoHud;
                if (ADOBase.controller != null) ADOBase.controller.noFail = noFail;
            }
        }
    }
}
