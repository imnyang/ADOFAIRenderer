using System;
using System.IO;
using UnityModManagerNet;

namespace OrbitRender
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

    internal sealed class RenderProfile
    {
        public RenderProfile(int width, int height, int fps, int bitrateMbps, string ffmpegPreset,
            float endDelaySeconds = 2f, string ffmpegCodec = "libx264", VideoCodec videoCodec = VideoCodec.H264,
            VideoBitDepth bitDepth = VideoBitDepth.Eight)
        {
            Width = width;
            Height = height;
            Fps = fps;
            BitrateMbps = bitrateMbps;
            FfmpegPreset = ffmpegPreset;
            EndDelaySeconds = endDelaySeconds;
            FfmpegCodec = ffmpegCodec;
            VideoCodec = VideoCodecCatalog.Normalize(videoCodec);
            BitDepth = VideoCodecCatalog.Normalize(bitDepth);
        }

        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public int BitrateMbps { get; }
        public string FfmpegPreset { get; }
        public float EndDelaySeconds { get; }
        public string FfmpegCodec { get; }
        public VideoCodec VideoCodec { get; }
        public VideoBitDepth BitDepth { get; }
        public string PixelFormat => BitDepth == VideoBitDepth.Ten ? "yuv420p10le" : "yuv420p";
        public string ContainerExtension => VideoCodecCatalog.Get(VideoCodec).ContainerExtension;
        public string ContainerMimeType => VideoCodecCatalog.Get(VideoCodec).MimeType;
        public string AudioEncoder => VideoCodecCatalog.Get(VideoCodec).AudioEncoder;
        public string AudioBitrate => VideoCodecCatalog.Get(VideoCodec).AudioBitrate;
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

        // Do not pass Min/Max to UMM for a text field. UMM clamps each parsed
        // keystroke, so typing a value such as 120 would turn the first "1"
        // into 15 before the remaining digits can be entered.
        [Draw("Target FPS", DrawType.Field, VisibleOn = "Preset|Custom")]
        public int Fps = 60;

        [Draw("Video bitrate (Mbps)", DrawType.Field, Min = MinBitrate, Max = MaxBitrate, VisibleOn = "Preset|Custom")]
        public int BitrateMbps = 18;

        [Draw("End delay (seconds)", DrawType.Field, Min = 0, Max = 30, Precision = 2)]
        public float EndDelaySeconds = 2f;

        [Draw("Capture audio", DrawType.Toggle)]
        public bool CaptureAudio = true;

        [Draw("BGA mode (hide tiles, planets & hit sounds)", DrawType.Toggle)]
        public bool BgaMode = false;

        [Draw("Show planet rings", DrawType.Toggle)]
        public bool ShowPlanetRings = true;

        [Draw("Show song title", DrawType.Toggle)]
        public bool ShowSongTitle = true;

        [Draw("Show countdown", DrawType.Toggle)]
        public bool ShowCountdown = true;

        [Draw("Show result text (hit judgments stay hidden)", DrawType.Toggle)]
        public bool ShowResultText = true;

        [Draw("Encoding speed", DrawType.PopupList)]
        public EncoderSpeed Encoding = EncoderSpeed.Quality;

        [Draw("Video encoder", DrawType.PopupList)]
        public VideoEncoder Encoder = VideoEncoder.Auto;

        [Draw("Video codec", DrawType.PopupList)]
        public VideoCodec Codec = VideoCodec.H264;

        [Draw("Video bit depth", DrawType.PopupList)]
        public VideoBitDepth BitDepth = VideoBitDepth.Eight;

        // These paths are rendered by Main.OnGUI so a Browse button can sit
        // next to each field. UMM's stock Field drawer cannot add a button
        // to the same row.
        [Draw(DrawType.Ignore)]
        public string OutputDirectory = "";

        [Draw("Open output folder after render", DrawType.Toggle)]
        public bool OpenOutputFolder = true;

        [Draw(DrawType.Ignore)]
        public string FfmpegExecutable = "";

        // FFmpeg is downloaded only after the user explicitly approves it on
        // the first launch. This is intentionally hidden from the UMM form;
        // the install prompt is shown in the game UI instead.
        [Draw(DrawType.Ignore)]
        public bool FfmpegInstallPrompted = false;

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
            BitrateMbps = Clamp(BitrateMbps, MinBitrate, MaxBitrate);
            if (float.IsNaN(EndDelaySeconds) || float.IsInfinity(EndDelaySeconds)) EndDelaySeconds = 2f;
            EndDelaySeconds = Math.Max(0f, Math.Min(30f, EndDelaySeconds));
        }

        internal RenderProfile ResolveProfile()
        {
            return ResolveProfile(null, null, null, null, null, null, null, null);
        }

        internal RenderProfile ResolveProfile(RendererPreset? presetOverride, int? widthOverride,
            int? heightOverride, int? fpsOverride, int? bitrateOverride, float? endDelayOverride,
            VideoCodec? codecOverride, VideoBitDepth? bitDepthOverride,
            EncoderSpeed? encodingOverride = null, VideoEncoder? encoderOverride = null)
        {
            var hasVideoOverride = presetOverride.HasValue || widthOverride.HasValue || heightOverride.HasValue
                || fpsOverride.HasValue || bitrateOverride.HasValue || codecOverride.HasValue
                || bitDepthOverride.HasValue;
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
            var encoding = encodingOverride ?? Encoding;
            var encoder = encoderOverride ?? Encoder;
            return new RenderProfile(
                widthOverride.HasValue ? EvenClamp(widthOverride.Value, MinWidth, MaxWidth) : baseProfile.Width,
                heightOverride.HasValue ? EvenClamp(heightOverride.Value, MinHeight, MaxHeight) : baseProfile.Height,
                fpsOverride.HasValue ? Clamp(fpsOverride.Value, MinFps, MaxFps) : baseProfile.Fps,
                bitrateOverride.HasValue ? Clamp(bitrateOverride.Value, MinBitrate, MaxBitrate) : baseProfile.BitrateMbps,
                GetEncoderPreset(encoding), endDelay, GetEncoderCodec(codecOverride ?? Codec, encoder), codecOverride ?? Codec,
                bitDepthOverride ?? BitDepth);
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Normalize();
            Save(this, modEntry);
        }

        public static RendererSettings Load(UnityModManager.ModEntry modEntry)
        {
            var settings = UnityModManager.ModSettings.Load<RendererSettings>(modEntry) ?? new RendererSettings();
            settings.Normalize();
            return settings;
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

        private string GetEncoderPreset(EncoderSpeed encoding)
        {
            switch (encoding)
            {
                case EncoderSpeed.Balanced: return "veryfast";
                case EncoderSpeed.Quality: return "fast";
                case EncoderSpeed.Maximum:
                default: return "ultrafast";
            }
        }

        private string GetEncoderCodec(VideoCodec codec, VideoEncoder encoder)
        {
            var gpu = (UnityEngine.SystemInfo.graphicsDeviceName ?? string.Empty) + " "
                + (UnityEngine.SystemInfo.graphicsDeviceVendor ?? string.Empty);
            var nvidia = ContainsGpuName(gpu, "NVIDIA");
            var intel = ContainsGpuName(gpu, "Intel");
            var amd = ContainsGpuName(gpu, "AMD") || ContainsGpuName(gpu, "ATI")
                || ContainsGpuName(gpu, "Radeon");
            return VideoCodecCatalog.Get(VideoCodecCatalog.Normalize(codec))
                .ResolveEncoder(encoder, nvidia, intel, amd);
        }

        private static bool ContainsGpuName(string value, string name)
        {
            return value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private void Normalize()
        {
            Width = EvenClamp(Width, MinWidth, MaxWidth);
            Height = EvenClamp(Height, MinHeight, MaxHeight);
            Fps = Clamp(Fps, MinFps, MaxFps);
            BitrateMbps = Clamp(BitrateMbps, MinBitrate, MaxBitrate);
            Codec = VideoCodecCatalog.Normalize(Codec);
            BitDepth = VideoCodecCatalog.Normalize(BitDepth);
            if (!Enum.IsDefined(typeof(VideoEncoder), Encoder)) Encoder = VideoEncoder.Auto;
            if (float.IsNaN(EndDelaySeconds) || float.IsInfinity(EndDelaySeconds)) EndDelaySeconds = 2f;
            EndDelaySeconds = Clamp(EndDelaySeconds, 0f, 30f);
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
