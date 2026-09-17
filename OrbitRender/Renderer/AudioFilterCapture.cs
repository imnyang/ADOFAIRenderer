using System;
using System.Threading;
using UnityEngine;

namespace OrbitRender.Renderer
{
    // AudioRenderer is not reliable on every Unity desktop audio backend.
    // This component sits beside the active AudioListener and copies the
    // listener mix from OnAudioFilterRead into a single-producer,
    // single-consumer ring buffer. The callback never allocates or calls back
    // into Unity, which is required because it runs on the audio thread.
    internal sealed class AudioFilterCapture : MonoBehaviour
    {
        private const int RingBufferSeconds = 8;
        private float[] ring;
        private int capacity;
        private int channels;
        private int accepting;
        private long readPosition;
        private long writePosition;
        private long droppedSamples;

        internal int AvailableSamples
        {
            get
            {
                var available = Interlocked.Read(ref writePosition) - Interlocked.Read(ref readPosition);
                if (available <= 0) return 0;
                return (int)Math.Min(available, capacity);
            }
        }

        internal long DroppedSamples => Interlocked.Read(ref droppedSamples);

        internal static AudioFilterCapture Attach()
        {
            AudioListener selected = null;
            foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener == null || listener.gameObject == null || !listener.gameObject.activeInHierarchy) continue;
                if (listener.enabled) { selected = listener; break; }
            }
            if (selected == null) return null;

            // Unity binds OnAudioFilterRead to either the AudioSource or the
            // AudioListener on a GameObject. Some ADOFAI versions put both on
            // SfxHandler(Clone), so adding this component there can silently
            // bind it to the source instead of the listener. Leave the game's
            // listener untouched in that case; GameAudioCapture uses the
            // static AudioListener.GetOutputData path instead.
            var sources = selected.gameObject.GetComponents<AudioSource>();
            var listeners = selected.gameObject.GetComponents<AudioListener>();
            if (sources.Length > 0 || listeners.Length > 1)
                return null;

            var capture = selected.gameObject.GetComponent<AudioFilterCapture>();
            return capture ?? selected.gameObject.AddComponent<AudioFilterCapture>();
        }

        internal void StartCapture(int sampleRate, int channelCount)
        {
            if (sampleRate <= 0 || channelCount <= 0) return;
            channels = channelCount;
            capacity = checked(Math.Max(65536, sampleRate * channelCount * RingBufferSeconds));
            ring = new float[capacity];
            Interlocked.Exchange(ref readPosition, 0);
            Interlocked.Exchange(ref writePosition, 0);
            Interlocked.Exchange(ref droppedSamples, 0);
            Interlocked.Exchange(ref accepting, 1);
        }

        internal int SkipSamples(int count)
        {
            if (count <= 0 || ring == null) return 0;
            var available = AvailableSamples;
            var skipped = Math.Min(count, available);
            Interlocked.Exchange(ref readPosition, Interlocked.Read(ref readPosition) + skipped);
            return skipped;
        }

        internal int ReadSamples(float[] destination, int count)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (count <= 0 || ring == null) return 0;
            count = Math.Min(count, destination.Length);
            count = Math.Min(count, AvailableSamples);
            if (count <= 0) return 0;

            var read = Interlocked.Read(ref readPosition);
            for (int i = 0; i < count; i++) destination[i] = ring[(read + i) % capacity];
            Thread.MemoryBarrier();
            Interlocked.Exchange(ref readPosition, read + count);
            return count;
        }

        internal void StopCapture()
        {
            Interlocked.Exchange(ref accepting, 0);
        }

        internal void DetachCapture()
        {
            StopCapture();
            UnityEngine.Object.Destroy(this);
        }

        private void OnAudioFilterRead(float[] data, int callbackChannels)
        {
            if (data == null || data.Length == 0 || ring == null ||
                Volatile.Read(ref accepting) == 0 || callbackChannels <= 0) return;

            var read = Interlocked.Read(ref readPosition);
            var write = Interlocked.Read(ref writePosition);
            var available = write - read;
            var callbackFrames = data.Length / callbackChannels;
            var outputSamples = checked(callbackFrames * channels);
            if (outputSamples <= 0) return;
            if (outputSamples > capacity - available)
            {
                Interlocked.Add(ref droppedSamples, outputSamples);
                return;
            }

            for (int frame = 0; frame < callbackFrames; frame++)
            {
                var sourceOffset = frame * callbackChannels;
                var outputOffset = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample;
                    if (channels == 1)
                    {
                        sample = 0f;
                        for (int source = 0; source < callbackChannels; source++)
                            sample += data[sourceOffset + source];
                        sample /= callbackChannels;
                    }
                    else sample = data[sourceOffset + Math.Min(channel, callbackChannels - 1)];
                    ring[(write + outputOffset + channel) % capacity] = sample;
                }
            }
            Thread.MemoryBarrier();
            Interlocked.Exchange(ref writePosition, write + outputSamples);
        }

        private void OnDestroy()
        {
            StopCapture();
        }
    }
}
