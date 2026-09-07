using System;

namespace ADOFAIRenderer.Renderer
{
    public sealed class RenderClock
    {
        public int Fps { get; }
        public long FrameIndex { get; private set; }
        public double Time => FrameIndex / (double)Fps;
        // A fixed DSP origin also keeps scheduling independent of the audio device.
        public double DspOrigin { get; private set; } = 1000.0;
        public double DspTime => DspOrigin + Time;
        public void AnchorDsp(double origin)
        {
            if (FrameIndex != 0 || double.IsNaN(origin) || double.IsInfinity(origin))
                throw new InvalidOperationException("DSP origin must be anchored before frame zero.");
            DspOrigin = origin;
        }
        public RenderClock(int fps = 60)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
            Fps = fps;
        }
        public double SongPosition(double scheduledStart, double pitch, double offset, double calibration)
            => (DspTime - scheduledStart - calibration) * pitch - offset;
        public void Advance() { checked { FrameIndex++; } }
    }
}
