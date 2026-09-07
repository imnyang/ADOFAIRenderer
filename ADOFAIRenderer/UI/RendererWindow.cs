using UnityEngine;
using ADOFAIRenderer.Renderer;

namespace ADOFAIRenderer.UI
{
    internal static class RendererWindow
    {
        private static bool initialized;
        private static GUIStyle panel, title, message, detail;
        private static Texture2D background;

        internal static void DrawToast(RendererController renderer)
        {
            EnsureStyles();
            float width = Mathf.Min(620f, Screen.width - 40f);
            float left = (Screen.width - width) * 0.5f;
            GUILayout.BeginArea(new Rect(left, 26f, width, 86f), panel);
            GUILayout.BeginVertical();
            GUILayout.Label("ADOFAI RENDERER", title);
            GUILayout.Label(renderer.ToastText ?? renderer.Message ?? string.Empty, message);
            if (renderer.TotalFrames > 0)
            {
                float progress = Mathf.Clamp01((float)renderer.CapturedFrames / renderer.TotalFrames);
                GUILayout.Label(string.Format("{0:F1}%   {1} / {2} frames   {3:F1} fps",
                    progress * 100f, renderer.CapturedFrames, renderer.TotalFrames, renderer.GenerationFps), detail);
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static void EnsureStyles()
        {
            if (initialized) return;
            initialized = true;
            background = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            background.SetPixel(0, 0, new Color(0.035f, 0.045f, 0.07f, 0.96f));
            background.Apply();
            panel = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(18, 18, 12, 12),
                normal = { background = background }
            };
            title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.82f, 0.93f, 1f) }
            };
            message = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            detail = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.55f, 0.80f, 0.90f) }
            };
        }
    }
}
