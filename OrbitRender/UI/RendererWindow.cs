using UnityEngine;
using OrbitRender.Renderer;

namespace OrbitRender.UI
{
    internal static class RendererWindow
    {
        private static bool initialized;
        private static GUIStyle title, message, detail;
        private static Texture2D backdrop, border, background, progressTrack, progressFill;

        // Renderer UI palette supplied by the user.
        private static readonly Color DarkBackground = Hsl(315f, 21f, 8f);
        private static readonly Color Foreground = Hsl(0f, 0f, 98f);
        private static readonly Color DarkMuted = Hsl(296f, 18f, 15f);
        private static readonly Color MutedForeground = Hsl(240f, 5f, 68f);
        private static readonly Color DarkBorder = Hsl(296f, 18f, 15f);
        private static readonly Color Ring = Hsl(240f, 4.9f, 83.9f);

        internal static void DrawBackdrop()
        {
            EnsureStyles();
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height),
                backdrop, ScaleMode.StretchToFill, false);
        }

        internal static void DrawToast(RendererController renderer)
        {
            EnsureStyles();
            float width = Mathf.Min(680f, Screen.width - 40f);
            float height = renderer.TotalFrames > 0 ? 190f : 132f;
            float left = (Screen.width - width) * 0.5f;
            float top = Mathf.Max(20f, (Screen.height - height) * 0.5f);
            var rect = new Rect(left, top, width, height);

            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(rect.x - 1f, rect.y - 1f, rect.width + 2f, rect.height + 2f),
                    border, ScaleMode.StretchToFill, false);
                GUI.DrawTexture(rect, background, ScaleMode.StretchToFill, false);
            }

            const float padding = 22f;
            var content = new Rect(rect.x + padding, rect.y + 16f, rect.width - padding * 2f, rect.height - 32f);
            GUI.Label(new Rect(content.x, content.y, content.width, 20f), "ORBITRENDER", title);
            GUI.Label(new Rect(content.x, content.y + 24f, content.width, 32f),
                renderer.ToastText ?? renderer.Message ?? string.Empty, message);
            if (renderer.TotalFrames > 0)
            {
                float progress = Mathf.Clamp01((float)renderer.CapturedFrames / renderer.TotalFrames);
                var progressRect = new Rect(content.x, content.y + 62f, content.width, 9f);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.DrawTexture(progressRect, progressTrack, ScaleMode.StretchToFill, false);
                    if (progress > 0f)
                        GUI.DrawTexture(new Rect(progressRect.x, progressRect.y,
                            progressRect.width * progress, progressRect.height), progressFill,
                            ScaleMode.StretchToFill, false);
                }

                GUI.Label(new Rect(content.x, content.y + 76f, content.width, 18f), renderer.ProgressText, detail);
                GUI.Label(new Rect(content.x, content.y + 94f, content.width, 18f), renderer.EtaText, detail);
                GUI.Label(new Rect(content.x, content.y + 112f, content.width, 18f), renderer.SpeedText, detail);
            }
        }

        internal static void DrawEncoderFallbackPrompt(RendererController renderer)
        {
            EnsureStyles();
            float width = Mathf.Min(760f, Screen.width - 40f);
            float height = 230f;
            float left = (Screen.width - width) * 0.5f;
            float top = Mathf.Max(24f, (Screen.height - height) * 0.5f);
            var rect = new Rect(left, top, width, height);
            GUI.Box(rect, string.Empty);
            var content = new Rect(rect.x + 24f, rect.y + 18f, rect.width - 48f, rect.height - 36f);
            GUI.Label(new Rect(content.x, content.y, content.width, 24f), "ENCODER CONFIRMATION", title);
            GUI.Label(new Rect(content.x, content.y + 34f, content.width, 38f),
                "The selected hardware encoder could not be initialized.", message);
            GUI.Label(new Rect(content.x, content.y + 76f, content.width, 54f),
                renderer.EncoderFallbackReason ?? string.Empty, detail);
            GUI.Label(new Rect(content.x, content.y + 132f, content.width, 28f),
                "Use Software encoder for this render? Your saved encoder setting will not be changed.", detail);
            if (GUI.Button(new Rect(content.x, content.y + 170f, 310f, 34f), "Use Software and continue"))
                renderer.ConfirmEncoderFallback();
            if (GUI.Button(new Rect(content.x + 326f, content.y + 170f, 160f, 34f), "Cancel render"))
                renderer.RejectEncoderFallback();
        }

        internal static void DrawFfmpegInstallPrompt()
        {
            EnsureStyles();
            float width = Mathf.Min(760f, Screen.width - 40f);
            float height = 238f;
            float left = (Screen.width - width) * 0.5f;
            float top = Mathf.Max(24f, (Screen.height - height) * 0.5f);
            var rect = new Rect(left, top, width, height);
            GUI.Box(rect, string.Empty);
            var content = new Rect(rect.x + 24f, rect.y + 18f, rect.width - 48f, rect.height - 36f);

            var waiting = FfmpegInstaller.IsAwaitingConsent;
            GUI.Label(new Rect(content.x, content.y, content.width, 24f),
                waiting ? "FFMPEG INSTALLATION" : "INSTALLING FFMPEG", title);
            GUI.Label(new Rect(content.x, content.y + 34f, content.width, 40f),
                waiting
                    ? "OrbitRender needs FFmpeg to export videos. Download the platform-compatible binary now?"
                    : "Downloading FFmpeg for this platform. The renderer will be ready when the download finishes.",
                message);
            GUI.Label(new Rect(content.x, content.y + 82f, content.width, 52f),
                waiting
                    ? "The download comes from the FFmpeg build provider and is saved inside the mod folder. An internet connection is required."
                    : FfmpegInstaller.StatusMessage,
                detail);

            if (waiting)
            {
                if (GUI.Button(new Rect(content.x, content.y + 158f, 310f, 34f), "Install FFmpeg"))
                    FfmpegInstaller.ConfirmInstall();
                if (GUI.Button(new Rect(content.x + 326f, content.y + 158f, 160f, 34f), "Not now"))
                    FfmpegInstaller.DeclineInstall();
            }
        }

        private static void EnsureStyles()
        {
            if (initialized) return;
            initialized = true;
            backdrop = Solid(DarkBackground);
            border = Solid(DarkBorder);
            background = Solid(DarkMuted);
            progressTrack = Solid(DarkBackground);
            progressFill = Solid(Ring);
            title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Ring }
            };
            message = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Foreground }
            };
            detail = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = MutedForeground }
            };
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "OrbitRender UI" };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static Color Hsl(float hue, float saturationPercent, float lightnessPercent)
        {
            float h = Mathf.Repeat(hue, 360f) / 360f;
            float s = saturationPercent / 100f;
            float l = lightnessPercent / 100f;
            float chroma = (1f - Mathf.Abs(2f * l - 1f)) * s;
            float x = chroma * (1f - Mathf.Abs((h * 6f) % 2f - 1f));
            float r = 0f, g = 0f, b = 0f;
            if (h < 1f / 6f) { r = chroma; g = x; }
            else if (h < 2f / 6f) { r = x; g = chroma; }
            else if (h < 3f / 6f) { g = chroma; b = x; }
            else if (h < 4f / 6f) { g = x; b = chroma; }
            else if (h < 5f / 6f) { r = x; b = chroma; }
            else { r = chroma; b = x; }
            float match = l - chroma / 2f;
            return new Color(r + match, g + match, b + match, 1f);
        }
    }
}
