using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace ADOFAIRenderer.Renderer
{
    internal sealed class GameAudioCapture : IDisposable
    {
        private FileStream stream;
        private NativeArray<float> samples;
        private float[] managed;
        private byte[] bytes;
        private bool started;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public long SampleFrames { get; private set; }
        public float Peak { get; private set; }

        public void Begin(string path)
        {
            SampleRate = AudioSettings.outputSampleRate;
            switch (AudioSettings.speakerMode)
            {
                case AudioSpeakerMode.Mono: Channels = 1; break;
                case AudioSpeakerMode.Stereo: case AudioSpeakerMode.Prologic: Channels = 2; break;
                case AudioSpeakerMode.Quad: Channels = 4; break;
                case AudioSpeakerMode.Surround: Channels = 5; break;
                case AudioSpeakerMode.Mode5point1: Channels = 6; break;
                case AudioSpeakerMode.Mode7point1: Channels = 8; break;
                default: throw new InvalidOperationException("Unsupported audio speaker layout.");
            }
            if (SampleRate <= 0) throw new InvalidOperationException("Audio device has no sample rate.");
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                WriteHeader(0);
                if (!AudioRenderer.Start()) throw new InvalidOperationException("Unity AudioRenderer could not start game audio capture.");
                started = true;
            }
            catch { Dispose(); throw; }
        }

        public void CaptureFrame()
        {
            if (!started) throw new InvalidOperationException("Audio was not initialized before capturing frame zero.");
            int count = AudioRenderer.GetSampleCountForCaptureFrame();
            if (count <= 0) throw new InvalidOperationException("Unity AudioRenderer returned no samples. Game audio capture is unavailable; disable Audio to render video only.");
            int length = checked(count * Channels);
            if (!samples.IsCreated || samples.Length != length)
            {
                if (samples.IsCreated) samples.Dispose();
                samples = new NativeArray<float>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                managed = new float[length]; bytes = new byte[length * sizeof(float)];
            }
            if (!AudioRenderer.Render(samples)) throw new InvalidOperationException("Unity AudioRenderer failed to render the audio frame.");
            samples.CopyTo(managed);
            for (int i = 0; i < managed.Length; i++) Peak = Math.Max(Peak, Math.Abs(managed[i]));
            Buffer.BlockCopy(managed, 0, bytes, 0, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
            SampleFrames += count;
            if (stream.Length > uint.MaxValue - 36L) throw new IOException("WAV exceeded its 4 GB size limit.");
        }

        public void Complete(long videoFrames, int fps)
        {
            long targetSamples = checked(videoFrames * (long)SampleRate / fps);
            // Unity's audio mixer can round the final block. Correct only that
            // boundary; reject drift instead of silently stretching the song.
            int tolerance = Math.Max(AudioSettings.GetConfiguration().dspBufferSize * 2, SampleRate / fps * 2);
            if (Math.Abs(SampleFrames - targetSamples) > tolerance)
                throw new InvalidOperationException("Audio drift: captured " + SampleFrames + " sample frames, expected " + targetSamples + ".");
            long dataLength = checked(targetSamples * Channels * sizeof(float));
            stream.SetLength(44 + dataLength);
            stream.Position = 0; WriteHeader(dataLength); stream.Flush();
            stream.Dispose(); stream = null;
        }
        private void WriteHeader(long size)
        {
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(checked((uint)(36 + size)));
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16u);
                writer.Write((ushort)3); writer.Write((ushort)Channels); writer.Write(SampleRate);
                writer.Write(SampleRate * Channels * 4); writer.Write((ushort)(Channels * 4)); writer.Write((ushort)32);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(checked((uint)size));
            }
        }
        public void Dispose()
        {
            try { if (started) AudioRenderer.Stop(); }
            finally
            {
                started = false;
                if (samples.IsCreated) samples.Dispose();
                stream?.Dispose(); stream = null;
            }
        }
    }
}
