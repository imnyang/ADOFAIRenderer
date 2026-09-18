using UnityEngine;

namespace OrbitRender.UI
{
    // Shared controls for the UMM settings page and the in-game export dialog.
    // Keeping these in one place prevents the two entry points from slowly
    // drifting into different layouts and interaction patterns.
    internal static class SettingsUi
    {
        internal static string LabeledField(string label, string value, float width)
        {
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            return GUILayout.TextField(value ?? string.Empty, GUILayout.Width(width));
        }

        internal static bool DrawSectionHeader(string title, ref bool expanded)
        {
            var marker = expanded ? "▼ " : "▶ ";
            if (GUILayout.Button(marker + title, GUI.skin.button, GUILayout.ExpandWidth(true)))
                expanded = !expanded;
            return expanded;
        }

        internal static RendererPreset DrawPreset(RendererPreset value)
        {
            return (RendererPreset)GUILayout.Toolbar((int)value, new[] {
                Localization.Text("Custom", "사용자 지정"),
                Localization.Text("Preview", "미리보기"),
                "FullHD", "QHD", "UHD 4K"
            });
        }

        internal static EncoderSpeed DrawEncoding(EncoderSpeed value)
        {
            GUILayout.Label(Localization.Text("Encoding speed", "인코딩 속도"));
            return (EncoderSpeed)GUILayout.Toolbar((int)value, new[] {
                Localization.Text("Maximum", "최대 속도"),
                Localization.Text("Balanced", "균형"),
                Localization.Text("Quality", "품질")
            });
        }

        internal static VideoEncoder DrawEncoder(VideoEncoder value)
        {
            GUILayout.Label(Localization.Text("Video encoder", "비디오 인코더"));
            var selected = value == VideoEncoder.Auto ? 0
                : value == VideoEncoder.NvidiaNvenc ? 1
                : value == VideoEncoder.IntelQsv ? 2
                : value == VideoEncoder.AmdAmf ? 3 : 4;
            selected = GUILayout.Toolbar(selected, new[] {
                Localization.Text("Auto", "자동"),
                "NVIDIA NVENC", "Intel QSV", "AMD AMF",
                Localization.Text("Software", "소프트웨어")
            });
            switch (selected)
            {
                case 1: return VideoEncoder.NvidiaNvenc;
                case 2: return VideoEncoder.IntelQsv;
                case 3: return VideoEncoder.AmdAmf;
                case 4: return VideoEncoder.Software;
                default: return VideoEncoder.Auto;
            }
        }

        internal static VideoCodec DrawCodec(VideoCodec value)
        {
            GUILayout.Label(Localization.Text("Video codec", "비디오 코덱"));
            return (VideoCodec)GUILayout.Toolbar((int)value,
                new[] { "H.264", "H.265", "VP9", "AV1" });
        }

        internal static VideoBitDepth DrawBitDepth(VideoBitDepth value)
        {
            GUILayout.Label(Localization.Text("Video bit depth", "비트 깊이"));
            return (VideoBitDepth)GUILayout.Toolbar((int)value,
                new[] { "8-bit", "10-bit" });
        }
    }
}
