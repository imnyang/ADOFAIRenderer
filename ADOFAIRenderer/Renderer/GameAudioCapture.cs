using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace ADOFAIRenderer.Renderer
{
    internal sealed class GameAudioCapture : IDisposable
    {
        // AudioListener.GetOutputData is documented to require a power-of-two
        // buffer. Unity 6 macOS players can return an empty history for large
        // requests, so keep this at one 60 fps frame plus headroom.
        private const int ListenerHistorySamples = 1024;
        private FileStream stream;
        private NativeArray<float> samples;
        private float[] managed;
        private byte[] bytes;
        private float[] listenerLeft;
        private float[] listenerRight;
        private AudioFilterCapture filter;
        private bool started;
        private bool rendererUnavailable;
        private bool useFilterFallback;
        private bool useListenerOutputFallback;
        private bool listenerOutputAvailable;
        private long filterSamplesToDiscard;
        private int zeroSampleReads;
        private long captureTicks;
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public long SampleFrames { get; private set; }
        public float Peak { get; private set; }
        public int ZeroSampleReads => zeroSampleReads;
        public bool UsedSilenceFallback => rendererUnavailable || zeroSampleReads > 0;
        public bool UsedFilterFallback => useFilterFallback;
        public bool NeedsRealtimePacing => zeroSampleReads > 0;
        public double CaptureSeconds => System.Threading.Interlocked.Read(ref captureTicks)
            / (double)System.Diagnostics.Stopwatch.Frequency;

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
                filter = AudioFilterCapture.Attach();
                started = AudioRenderer.Start();
                if (!started)
                {
                    rendererUnavailable = true;
                    Main.Entry.Logger.Log("Unity AudioRenderer could not start; audio will be padded with silence.");
                }
                filter?.StartCapture(SampleRate, Channels);
                InitializeListenerOutputCapture();
                useFilterFallback = false;
                useListenerOutputFallback = !started && listenerOutputAvailable;
                Main.Entry.Logger.Log(filter == null
                    ? "AudioListener filter unavailable on the mixed listener; using master-output polling fallback."
                    : "AudioListener filter fallback attached and waiting for mixer samples.");
            }
            catch { Dispose(); throw; }
        }

        public void CaptureFrame(long videoFrameIndex, int fps)
        {
            var captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                long expectedSamples = checked((videoFrameIndex + 1) * (long)SampleRate / fps);
                if (useListenerOutputFallback)
                {
                    CaptureListenerOutput(expectedSamples - SampleFrames);
                    return;
                }
                if (useFilterFallback)
                {
                    CaptureFilterSamples(expectedSamples - SampleFrames);
                    return;
                }

                if (!started) return;
                int count = AudioRenderer.GetSampleCountForCaptureFrame();
                // Unity reports the samples available since the previous Render
                // call. A zero here is valid at the first capture boundary (and
                // on some audio backends while the mixer is warming up). Do not
                // call Render with an empty buffer and do not pad yet: the next
                // positive query may include the samples from both boundaries.
                if (count == 0)
                {
                    zeroSampleReads++;
                    if (TryUseFilterFallback()) CaptureFilterSamples(expectedSamples - SampleFrames);
                    else if (TryUseListenerOutputFallback()) CaptureListenerOutput(expectedSamples - SampleFrames);
                    return;
                }
                if (count < 0) throw new InvalidOperationException("Unity AudioRenderer returned an invalid sample count: " + count + ".");
                // If the filter has already received the same mix, prefer it
                // after the first unavailable AudioRenderer read. Discard the
                // frames already written through AudioRenderer before switching
                // so the two capture paths cannot duplicate the prefix.
                if (zeroSampleReads > 0 && TryUseFilterFallback())
                {
                    CaptureFilterSamples(expectedSamples - SampleFrames);
                    return;
                }
                int length = checked(count * Channels);
                if (!samples.IsCreated || samples.Length != length)
                {
                    if (samples.IsCreated) samples.Dispose();
                    samples = new NativeArray<float>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    managed = new float[length]; bytes = new byte[length * sizeof(float)];
                }
                if (!AudioRenderer.Render(samples))
                {
                    rendererUnavailable = true;
                    zeroSampleReads++;
                    if (TryUseFilterFallback()) CaptureFilterSamples(expectedSamples - SampleFrames);
                    else if (TryUseListenerOutputFallback()) CaptureListenerOutput(expectedSamples - SampleFrames);
                    return;
                }
                samples.CopyTo(managed);
                WriteManagedSamples(count * Channels);
                if (stream.Length > uint.MaxValue - 36L) throw new IOException("WAV exceeded its 4 GB size limit.");
            }
            finally
            {
                System.Threading.Interlocked.Add(ref captureTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - captureStart);
            }
        }

        public void Complete(long videoFrames, int fps)
        {
            long targetSamples = checked(videoFrames * (long)SampleRate / fps);
            if (useListenerOutputFallback)
            {
                CaptureListenerOutput(targetSamples - SampleFrames);
                Main.Entry.Logger.Log("Game audio capture used AudioListener.GetOutputData fallback. Peak: "
                    + Peak.ToString("F6") + ".");
            }
            if (useFilterFallback) CaptureFilterSamples(targetSamples - SampleFrames);
            if (useFilterFallback)
                Main.Entry.Logger.Log("Game audio capture used the AudioListener filter fallback. Dropped interleaved samples: "
                    + (filter != null ? filter.DroppedSamples.ToString() : "0") + ".");
            else if (filter != null && zeroSampleReads > 0)
                Main.Entry.Logger.Log("AudioListener filter fallback received no samples; unavailable Unity AudioRenderer reads will be padded with silence.");
            // Unity's audio mixer can round the final block. Correct only that
            // boundary; reject drift instead of silently stretching the song.
            int tolerance = Math.Max(AudioSettings.GetConfiguration().dspBufferSize * 2, SampleRate / fps * 2);
            // Missing frames are recoverable: AudioRenderer can return zero
            // until its first positive capture window, and the final shortfall
            // is padded below. Only reject audio that ran ahead materially.
            if (SampleFrames > targetSamples + tolerance)
                throw new InvalidOperationException("Audio drift: captured " + SampleFrames + " sample frames, expected " + targetSamples + ".");
            if (SampleFrames < targetSamples)
            {
                long missing = targetSamples - SampleFrames;
                AppendSilence(missing);
                Main.Entry.Logger.Log("Unity AudioRenderer audio was short by " + missing
                    + " sample frames (unavailable reads: " + zeroSampleReads
                    + "); padded the WAV with silence.");
            }
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

        private void AppendSilence(long sampleFrames)
        {
            if (sampleFrames <= 0) return;
            int chunkFrames = Math.Max(1, Math.Min(16384, SampleRate));
            int chunkBytes = checked(chunkFrames * Channels * sizeof(float));
            if (bytes == null || bytes.Length != chunkBytes) bytes = new byte[chunkBytes];
            else Array.Clear(bytes, 0, bytes.Length);
            while (sampleFrames > 0)
            {
                int frames = (int)Math.Min(chunkFrames, sampleFrames);
                stream.Write(bytes, 0, checked(frames * Channels * sizeof(float)));
                sampleFrames -= frames;
            }
        }

        private void InitializeListenerOutputCapture()
        {
            listenerLeft = new float[ListenerHistorySamples];
            listenerRight = Channels > 1 ? new float[ListenerHistorySamples] : null;
            try
            {
                AudioListener.GetOutputData(listenerLeft, 0);
                if (listenerRight != null) AudioListener.GetOutputData(listenerRight, 1);
                listenerOutputAvailable = true;
            }
            catch (Exception ex)
            {
                listenerOutputAvailable = false;
                Main.Entry.Logger.Log("AudioListener.GetOutputData fallback unavailable: " + ex.Message);
            }
        }

        private bool TryUseListenerOutputFallback()
        {
            if (!listenerOutputAvailable) return false;
            useListenerOutputFallback = true;
            return true;
        }

        private void CaptureListenerOutput(long sampleFrames)
        {
            if (!listenerOutputAvailable || sampleFrames <= 0) return;
            int frames = (int)Math.Min(sampleFrames, ListenerHistorySamples);
            try
            {
                AudioListener.GetOutputData(listenerLeft, 0);
                if (listenerRight != null) AudioListener.GetOutputData(listenerRight, 1);
                EnsureManagedBuffers(checked(frames * Channels));
                int start = ListenerHistorySamples - frames;
                for (int frame = 0; frame < frames; frame++)
                {
                    int source = start + frame;
                    int destination = frame * Channels;
                    managed[destination] = listenerLeft[source];
                    for (int channel = 1; channel < Channels; channel++)
                        managed[destination + channel] = listenerRight != null ? listenerRight[source] : listenerLeft[source];
                }
                WriteManagedSamples(checked(frames * Channels));
            }
            catch (Exception ex)
            {
                listenerOutputAvailable = false;
                Main.Entry.Logger.Log("AudioListener.GetOutputData failed during capture: " + ex.Message);
            }
        }

        private bool TryUseFilterFallback()
        {
            if (filter == null || filter.AvailableSamples < Channels) return false;
            useFilterFallback = true;
            filterSamplesToDiscard = checked(SampleFrames * Channels);
            return true;
        }

        private void CaptureFilterSamples(long sampleFrames)
        {
            if (filter == null || sampleFrames <= 0) return;
            while (sampleFrames > 0)
            {
                if (filterSamplesToDiscard > 0)
                {
                    int skip = (int)Math.Min(filterSamplesToDiscard, Math.Min(int.MaxValue, filter.AvailableSamples));
                    if (skip <= 0) return;
                    filter.SkipSamples(skip);
                    filterSamplesToDiscard -= skip;
                    continue;
                }

                int available = filter.AvailableSamples;
                int values = (int)Math.Min((long)available, checked(sampleFrames * Channels));
                values -= values % Channels;
                if (values <= 0) return;
                EnsureManagedBuffers(values);
                int read = filter.ReadSamples(managed, values);
                if (read <= 0) return;
                WriteManagedSamples(read);
                sampleFrames -= read / Channels;
            }
        }

        private void EnsureManagedBuffers(int length)
        {
            if (managed == null || managed.Length != length) managed = new float[length];
            if (bytes == null || bytes.Length != length * sizeof(float)) bytes = new byte[length * sizeof(float)];
        }

        private void WriteManagedSamples(int sampleValues)
        {
            for (int i = 0; i < sampleValues; i++) Peak = Math.Max(Peak, Math.Abs(managed[i]));
            Buffer.BlockCopy(managed, 0, bytes, 0, checked(sampleValues * sizeof(float)));
            stream.Write(bytes, 0, checked(sampleValues * sizeof(float)));
            SampleFrames += sampleValues / Channels;
        }

        public void Dispose()
        {
            try
            {
                filter?.StopCapture();
                if (started) AudioRenderer.Stop();
                filter?.DetachCapture();
            }
            finally
            {
                filter = null;
                started = false;
                rendererUnavailable = false;
                useFilterFallback = false;
                useListenerOutputFallback = false;
                listenerOutputAvailable = false;
                filterSamplesToDiscard = 0;
                zeroSampleReads = 0;
                listenerLeft = null;
                listenerRight = null;
                if (samples.IsCreated) samples.Dispose();
                stream?.Dispose(); stream = null;
            }
        }
    }
}
