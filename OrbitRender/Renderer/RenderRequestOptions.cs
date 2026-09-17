namespace OrbitRender.Renderer
{
    // Per-render values selected from the editor's Export Video dialog.
    // Nullable fields deliberately fall back to the normal UMM settings.
    internal sealed class RenderRequestOptions
    {
        internal RendererPreset? Preset;
        internal int? Width;
        internal int? Height;
        internal int? Fps;
        internal int? BitrateMbps;
        internal float? EndDelaySeconds;
        internal bool? CaptureAudio;
        internal bool? BgaMode;
        internal EncoderSpeed? Encoding;
        internal VideoEncoder? Encoder;
        internal VideoCodec? VideoCodec;
        internal VideoBitDepth? BitDepth;
        internal bool? OpenOutputFolder;
    }
}
