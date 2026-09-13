using System;
using System.IO;
using UnityModManagerNet;

namespace ADOFAIRenderer
{
    public enum RendererPreset
    {
        Custom,
        Preview,
        FullHD,
        QHD,
        UHD4K
    }

    public enum EncoderSpeed
    {
        Maximum,
        Balanced,
        Quality
    }

    public enum VideoEncoder
    {
        Auto,
        NvidiaNvenc,
        Software
    }

    internal sealed class RenderProfile
    {
        public RenderProfile(int width, int height, int fps, int bitrateMbps, string ffmpegPreset,
            float endDelaySeconds = 2f, string ffmpegCodec = "libx264")
        {
            Width = width;
            Height = height;
            Fps = fps;
            BitrateMbps = bitrateMbps;
            FfmpegPreset = ffmpegPreset;
            EndDelaySeconds = endDelaySeconds;
            FfmpegCodec = ffmpegCodec;
        }

        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public int BitrateMbps { get; }
        public string FfmpegPreset { get; }
        public float EndDelaySeconds { get; }
        public string FfmpegCodec { get; }
    }

    public sealed class RendererSettings : UnityModManager.ModSettings, IDrawable
    {
        private const int MinWidth = 320;
        private const int MaxWidth = 3840;
        private const int MinHeight = 180;
        private const int MaxHeight = 2160;
        private const int MinFps = 15;
        private const int MaxFps = 240;
        private const int MinBitrate = 1;
        private const int MaxBitrate = 200;

        [Draw("Preset", DrawType.PopupList)]
        public RendererPreset Preset = RendererPreset.FullHD;

        [Draw("Width", DrawType.Field, Min = MinWidth, Max = MaxWidth, VisibleOn = "Preset|Custom")]
        public int Width = 1920;

        [Draw("Height", DrawType.Field, Min = MinHeight, Max = MaxHeight, VisibleOn = "Preset|Custom")]
        public int Height = 1080;

        [Draw("Target FPS", DrawType.Field, Min = MinFps, Max = MaxFps, VisibleOn = "Preset|Custom")]
        public int Fps = 60;

        [Draw("Video bitrate (Mbps)", DrawType.Field, Min = MinBitrate, Max = MaxBitrate, VisibleOn = "Preset|Custom")]
        public int BitrateMbps = 18;

        [Draw("End delay (seconds)", DrawType.Field, Min = 0, Max = 30, Precision = 2)]
        public float EndDelaySeconds = 2f;

        [Draw("Capture audio", DrawType.Toggle)]
        public bool CaptureAudio = true;

        [Draw("BGA mode (hide tiles, planets & hit sounds)", DrawType.Toggle)]
        public bool BgaMode = false;

        [Draw("Encoding speed", DrawType.PopupList)]
        public EncoderSpeed Encoding = EncoderSpeed.Quality;

        [Draw("Video encoder", DrawType.PopupList)]
        public VideoEncoder Encoder = VideoEncoder.Auto;

        [Draw("Output folder", DrawType.Field)]
        public string OutputDirectory = "";

        [Draw("FFmpeg executable", DrawType.Field)]
        public string FfmpegExecutable = "";

        public void OnChange()
        {
            // Selecting a built-in preset also copies its values into the
            // fields, so switching to Custom starts from a useful baseline.
            if (Preset != RendererPreset.Custom)
            {
                var profile = GetPresetProfile(Preset);
                Width = profile.Width;
                Height = profile.Height;
                Fps = profile.Fps;
                BitrateMbps = profile.BitrateMbps;
            }
            Width = EvenClamp(Width, MinWidth, MaxWidth);
            Height = EvenClamp(Height, MinHeight, MaxHeight);
            Fps = Clamp(Fps, MinFps, MaxFps);
            BitrateMbps = Clamp(BitrateMbps, MinBitrate, MaxBitrate);
            if (float.IsNaN(EndDelaySeconds) || float.IsInfinity(EndDelaySeconds)) EndDelaySeconds = 2f;
            EndDelaySeconds = Math.Max(0f, Math.Min(30f, EndDelaySeconds));
        }

        internal RenderProfile ResolveProfile()
        {
            return ResolveProfile(null, null, null, null, null, null);
        }

        internal RenderProfile ResolveProfile(RendererPreset? presetOverride, int? widthOverride,
            int? heightOverride, int? fpsOverride, int? bitrateOverride, float? endDelayOverride)
        {
            var hasVideoOverride = presetOverride.HasValue || widthOverride.HasValue || heightOverride.HasValue
                || fpsOverride.HasValue || bitrateOverride.HasValue;
            var preset = presetOverride ?? (hasVideoOverride ? RendererPreset.Custom : Preset);
            var baseProfile = preset == RendererPreset.Custom
                ? new RenderProfile(
                    EvenClamp(Width, MinWidth, MaxWidth),
                    EvenClamp(Height, MinHeight, MaxHeight),
                    Clamp(Fps, MinFps, MaxFps),
                    Clamp(BitrateMbps, MinBitrate, MaxBitrate),
                    "fast", EndDelaySeconds)
                : GetPresetProfile(preset);
            var endDelay = endDelayOverride.HasValue
                ? Clamp(endDelayOverride.Value, 0f, 30f)
                : baseProfile.EndDelaySeconds;
            return new RenderProfile(
                widthOverride.HasValue ? EvenClamp(widthOverride.Value, MinWidth, MaxWidth) : baseProfile.Width,
                heightOverride.HasValue ? EvenClamp(heightOverride.Value, MinHeight, MaxHeight) : baseProfile.Height,
                fpsOverride.HasValue ? Clamp(fpsOverride.Value, MinFps, MaxFps) : baseProfile.Fps,
                bitrateOverride.HasValue ? Clamp(bitrateOverride.Value, MinBitrate, MaxBitrate) : baseProfile.BitrateMbps,
                GetEncoderPreset(), endDelay, GetEncoderCodec());
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public static RendererSettings Load(UnityModManager.ModEntry modEntry)
        {
            return UnityModManager.ModSettings.Load<RendererSettings>(modEntry) ?? new RendererSettings();
        }

        internal string ResolveOutputDirectory()
        {
            var dataPath = Path.GetFullPath(UnityEngine.Application.dataPath);
            var dataDirectory = new DirectoryInfo(dataPath);
            // A macOS Unity player reports its Contents directory as
            // Application.dataPath, while Windows/Linux report <game>_Data.
            var gameRoot = (UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXPlayer
                || UnityEngine.Application.platform == UnityEngine.RuntimePlatform.OSXEditor)
                && string.Equals(dataDirectory.Name, "Contents", StringComparison.OrdinalIgnoreCase)
                ? dataDirectory.FullName
                : dataDirectory.Parent.FullName;
            var configured = Environment.ExpandEnvironmentVariables((OutputDirectory ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(configured)) return Path.Combine(gameRoot, "Renders");
            if (!Path.IsPathRooted(configured)) configured = Path.Combine(gameRoot, configured);
            return Path.GetFullPath(configured);
        }

        internal string ResolveFfmpegExecutable(string modDirectory)
        {
            var configured = Environment.ExpandEnvironmentVariables((FfmpegExecutable ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(configured)) return string.Empty;
            if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);

            // A relative path is resolved beside the mod. A bare command name
            // is returned as-is so Process.Start can resolve it through PATH.
            var local = Path.Combine(modDirectory, configured);
            return File.Exists(local) ? Path.GetFullPath(local) : configured;
        }

        private static RenderProfile GetPresetProfile(RendererPreset preset)
        {
            switch (preset)
            {
                case RendererPreset.Preview: return new RenderProfile(1280, 720, 30, 8, "veryfast");
                case RendererPreset.QHD: return new RenderProfile(2560, 1440, 60, 30, "veryfast");
                case RendererPreset.UHD4K: return new RenderProfile(3840, 2160, 60, 50, "fast");
                case RendererPreset.FullHD:
                default: return new RenderProfile(1920, 1080, 60, 18, "veryfast");
            }
        }

        private string GetEncoderPreset()
        {
            switch (Encoding)
            {
                case EncoderSpeed.Balanced: return "veryfast";
                case EncoderSpeed.Quality: return "fast";
                case EncoderSpeed.Maximum:
                default: return "ultrafast";
            }
        }

        private string GetEncoderCodec()
        {
            switch (Encoder)
            {
                case VideoEncoder.NvidiaNvenc: return "h264_nvenc";
                case VideoEncoder.Software: return "libx264";
                case VideoEncoder.Auto:
                default:
                    var gpu = UnityEngine.SystemInfo.graphicsDeviceName ?? string.Empty;
                    return gpu.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "h264_nvenc" : "libx264";
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static int EvenClamp(int value, int min, int max)
        {
            var result = Clamp(value, min, max);
            return (result & 1) == 0 ? result : result == max ? result - 1 : result + 1;
        }
    }
}
