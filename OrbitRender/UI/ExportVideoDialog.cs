using System;
using System.Globalization;
using UnityEngine;
using OrbitRender.Renderer;

namespace OrbitRender.UI
{
    internal static class ExportVideoDialog
    {
        private const int WindowId = 18473;
        private static bool open;
        private static scnEditor editor;
        private static Draft draft;
        private static string error;
        private static Rect windowRect;
        private static bool renderOptionsExpanded = true;
        private static bool visibleComponentsExpanded = true;
        private static bool encodingExpanded;

        internal static bool IsOpen => open;

        internal static void CloseDialog()
        {
            Close();
        }

        internal static void Open(scnEditor owner)
        {
            if (owner == null || Main.Settings == null) return;
            editor = owner;
            draft = Draft.From(Main.Settings);
            error = string.Empty;
            open = true;
            editor.ShowFileActionsPanel(false);
            windowRect = new Rect(0f, 0f, 720f, 610f);
        }

        internal static void Draw(RendererController renderer)
        {
            if (!open || draft == null) return;
            var width = Mathf.Min(760f, Screen.width - 36f);
            var height = Mathf.Min(680f, Screen.height - 36f);
            if (windowRect.width != width || windowRect.height != height)
            {
                windowRect.width = width;
                windowRect.height = height;
            }
            windowRect.x = (Screen.width - windowRect.width) * 0.5f;
            windowRect.y = Mathf.Max(18f, (Screen.height - windowRect.height) * 0.5f);

            if (Event.current.type == EventType.Repaint)
            {
                var previous = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = previous;
            }

            windowRect = GUI.Window(WindowId, windowRect, id => DrawWindow(id, renderer),
                Localization.Text("Export Video", "비디오 내보내기"));
            if (Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint)
                Event.current.Use();
        }

        private static void DrawWindow(int id, RendererController renderer)
        {
            GUILayout.BeginVertical();
            GUILayout.Label(Localization.Text("Choose the settings for this video export.",
                "비디오 내보내기 설정을 선택하세요."));
            GUILayout.Space(6f);

            GUILayout.Label(Localization.Text("Preset", "프리셋"));
            var preset = (RendererPreset)GUILayout.Toolbar((int)draft.Preset,
                new[] {
                    Localization.Text("Custom", "사용자 지정"),
                    Localization.Text("Preview", "미리보기"),
                    "FullHD", "QHD", "UHD 4K"
                });
            if (preset != draft.Preset)
            {
                draft.Preset = preset;
                if (preset != RendererPreset.Custom) draft.ApplyPreset();
            }

            if (draft.Preset == RendererPreset.Custom)
            {
                GUILayout.BeginHorizontal();
                draft.WidthText = LabeledField(Localization.Text("Width", "너비"), draft.WidthText, 90f);
                draft.HeightText = LabeledField(Localization.Text("Height", "높이"), draft.HeightText, 90f);
                draft.FpsText = LabeledField("FPS", draft.FpsText, 80f);
                draft.BitrateText = LabeledField(Localization.Text("Bitrate", "비트레이트"), draft.BitrateText, 80f);
                GUILayout.Label("Mbps", GUILayout.Width(44f));
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label(Localization.Format(
                    "Preset output: {0} × {1} @ {2} FPS, {3} Mbps",
                    "프리셋 출력: {0} × {1} @ {2} FPS, {3} Mbps",
                    draft.WidthText, draft.HeightText, draft.FpsText, draft.BitrateText));
            }

            GUILayout.Space(6f);
            if (DrawSectionHeader(Localization.Text("Render options", "렌더 옵션"), ref renderOptionsExpanded))
            {
                draft.EndDelayText = LabeledField(Localization.Text("End delay (seconds)", "종료 지연(초)"),
                    draft.EndDelayText, 90f);
                draft.CaptureAudio = GUILayout.Toggle(draft.CaptureAudio,
                    Localization.Text("Capture audio", "오디오 캡처"));
                draft.BgaMode = GUILayout.Toggle(draft.BgaMode,
                    Localization.Text("BGA mode (hide tiles, planets & hit sounds)",
                        "BGA 모드 (타일, 행성 및 타격음 숨기기)"));
                draft.OpenOutputFolder = GUILayout.Toggle(draft.OpenOutputFolder,
                    Localization.Text("Open output folder after render", "렌더 후 출력 폴더 열기"));
                draft.SaveAsDefault = GUILayout.Toggle(draft.SaveAsDefault,
                    Localization.Text("Save these values as the default renderer settings",
                        "이 값을 렌더러 기본 설정으로 저장"));
            }

            if (DrawSectionHeader(Localization.Text("Visible components", "표시할 구성 요소"),
                ref visibleComponentsExpanded))
            {
                draft.ShowPlanetRings = GUILayout.Toggle(draft.ShowPlanetRings,
                    Localization.Text("Show planet rings", "행성 고리 표시"));
                draft.ShowSongTitle = GUILayout.Toggle(draft.ShowSongTitle,
                    Localization.Text("Show song title", "곡 제목 표시"));
                draft.ShowCountdown = GUILayout.Toggle(draft.ShowCountdown,
                    Localization.Text("Show countdown", "카운트다운 표시"));
                draft.ShowResultText = GUILayout.Toggle(draft.ShowResultText,
                    Localization.Text("Show result text (hit judgments stay hidden)",
                        "결과 텍스트 표시 (판정은 숨김)"));
            }

            GUILayout.Space(6f);
            if (DrawSectionHeader(Localization.Text("Encoding", "인코딩"), ref encodingExpanded))
            {
                draft.Encoding = DrawEncoding(draft.Encoding);
                draft.Encoder = DrawEncoder(draft.Encoder);
                draft.Codec = DrawCodec(draft.Codec);
                draft.BitDepth = DrawBitDepth(draft.BitDepth);
            }

            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(error))
            {
                var previous = GUI.color;
                GUI.color = new Color(1f, 0.55f, 0.55f, 1f);
                GUILayout.Label(error);
                GUI.color = previous;
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Text("Cancel", "취소"), GUILayout.Width(120f))) Close();
            if (GUILayout.Button(Localization.Text("Export Video", "비디오 내보내기"), GUILayout.Width(150f)))
                Confirm(renderer);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 26f));
        }

        private static string LabeledField(string label, string value, float width)
        {
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            return GUILayout.TextField(value ?? string.Empty, GUILayout.Width(width));
        }

        private static bool DrawSectionHeader(string title, ref bool expanded)
        {
            var marker = expanded ? "▼ " : "▶ ";
            if (GUILayout.Button(marker + title, GUI.skin.button, GUILayout.ExpandWidth(true)))
                expanded = !expanded;
            return expanded;
        }

        private static EncoderSpeed DrawEncoding(EncoderSpeed value)
        {
            GUILayout.Label(Localization.Text("Encoding speed", "인코딩 속도"));
            return (EncoderSpeed)GUILayout.Toolbar((int)value, new[] {
                Localization.Text("Maximum", "최대 속도"),
                Localization.Text("Balanced", "균형"),
                Localization.Text("Quality", "품질")
            });
        }

        private static VideoEncoder DrawEncoder(VideoEncoder value)
        {
            GUILayout.Label(Localization.Text("Video encoder", "비디오 인코더"));
            var selected = value == VideoEncoder.Auto ? 0
                : value == VideoEncoder.NvidiaNvenc ? 1
                : value == VideoEncoder.IntelQsv ? 2
                : value == VideoEncoder.AmdAmf ? 3 : 4;
            selected = GUILayout.Toolbar(selected,
                new[] {
                    Localization.Text("Auto", "자동"), "NVIDIA NVENC", "Intel QSV", "AMD AMF",
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

        private static VideoCodec DrawCodec(VideoCodec value)
        {
            GUILayout.Label(Localization.Text("Video codec", "비디오 코덱"));
            return (VideoCodec)GUILayout.Toolbar((int)value, new[] { "H.264", "H.265", "VP9", "AV1" });
        }

        private static VideoBitDepth DrawBitDepth(VideoBitDepth value)
        {
            GUILayout.Label(Localization.Text("Video bit depth", "비트 깊이"));
            return (VideoBitDepth)GUILayout.Toolbar((int)value, new[] { "8-bit", "10-bit" });
        }

        private static void Confirm(RendererController renderer)
        {
            if (renderer == null || renderer.Busy)
            {
                error = Localization.Text("A render is already in progress.", "렌더가 이미 진행 중입니다.");
                return;
            }

            if (!draft.TryCreateOptions(out var options, out var message))
            {
                error = message;
                return;
            }

            if (draft.SaveAsDefault)
            {
                draft.ApplyTo(Main.Settings, options);
                Main.Settings.Save(Main.Entry);
            }

            var targetEditor = editor;
            Close();
            if (targetEditor != null) targetEditor.ShowFileActionsPanel(false);
            renderer.StartRender(options);
        }

        private static void Close()
        {
            open = false;
            editor = null;
            draft = null;
            error = string.Empty;
        }

        private sealed class Draft
        {
            internal RendererPreset Preset;
            internal string WidthText;
            internal string HeightText;
            internal string FpsText;
            internal string BitrateText;
            internal string EndDelayText;
            internal bool CaptureAudio;
            internal bool BgaMode;
            internal bool ShowPlanetRings;
            internal bool ShowSongTitle;
            internal bool ShowCountdown;
            internal bool ShowResultText;
            internal bool OpenOutputFolder;
            internal bool SaveAsDefault;
            internal EncoderSpeed Encoding;
            internal VideoEncoder Encoder;
            internal VideoCodec Codec;
            internal VideoBitDepth BitDepth;

            internal static Draft From(RendererSettings settings)
            {
                return new Draft {
                    Preset = settings.Preset,
                    WidthText = settings.Width.ToString(CultureInfo.InvariantCulture),
                    HeightText = settings.Height.ToString(CultureInfo.InvariantCulture),
                    FpsText = settings.Fps.ToString(CultureInfo.InvariantCulture),
                    BitrateText = settings.BitrateMbps.ToString(CultureInfo.InvariantCulture),
                    EndDelayText = settings.EndDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture),
                    CaptureAudio = settings.CaptureAudio,
                    BgaMode = settings.BgaMode,
                    ShowPlanetRings = settings.ShowPlanetRings,
                    ShowSongTitle = settings.ShowSongTitle,
                    ShowCountdown = settings.ShowCountdown,
                    ShowResultText = settings.ShowResultText,
                    OpenOutputFolder = settings.OpenOutputFolder,
                    Encoding = settings.Encoding,
                    Encoder = settings.Encoder,
                    Codec = settings.Codec,
                    BitDepth = settings.BitDepth
                };
            }

            internal void ApplyPreset()
            {
                switch (Preset)
                {
                    case RendererPreset.Preview: SetVideoValues(1280, 720, 30, 8); break;
                    case RendererPreset.QHD: SetVideoValues(2560, 1440, 60, 30); break;
                    case RendererPreset.UHD4K: SetVideoValues(3840, 2160, 60, 50); break;
                    case RendererPreset.FullHD: SetVideoValues(1920, 1080, 60, 18); break;
                }
            }

            internal bool TryCreateOptions(out RenderRequestOptions options, out string message)
            {
                options = null;
                message = string.Empty;
                int width = 0, height = 0, fps = 0, bitrate = 0;
                float endDelay;
                if (Preset == RendererPreset.Custom)
                {
                    if (!int.TryParse(WidthText, out width) || !int.TryParse(HeightText, out height)
                        || !int.TryParse(FpsText, out fps) || !int.TryParse(BitrateText, out bitrate))
                    {
                        message = Localization.Text(
                            "Width, height, FPS and bitrate must be valid numbers.",
                            "너비, 높이, FPS 및 비트레이트는 유효한 숫자여야 합니다.");
                        return false;
                    }
                    if (width < 320 || width > 3840 || height < 180 || height > 2160
                        || fps < 15 || fps > 240 || bitrate < 1 || bitrate > 200)
                    {
                        message = Localization.Text("Custom values are outside the supported ranges.",
                            "사용자 지정 값이 지원 범위를 벗어났습니다.");
                        return false;
                    }
                    if ((width & 1) != 0 || (height & 1) != 0)
                    {
                        message = Localization.Text("Width and height must be even numbers.",
                            "너비와 높이는 짝수여야 합니다.");
                        return false;
                    }
                }
                if (!TryParseFloat(EndDelayText, out endDelay) || endDelay < 0f || endDelay > 30f)
                {
                    message = Localization.Text("End delay must be between 0 and 30 seconds.",
                        "종료 지연은 0~30초 사이여야 합니다.");
                    return false;
                }

                options = new RenderRequestOptions {
                    Preset = Preset,
                    EndDelaySeconds = endDelay,
                    CaptureAudio = CaptureAudio,
                    BgaMode = BgaMode,
                    ShowPlanetRings = ShowPlanetRings,
                    ShowSongTitle = ShowSongTitle,
                    ShowCountdown = ShowCountdown,
                    ShowResultText = ShowResultText,
                    Encoding = Encoding,
                    Encoder = Encoder,
                    VideoCodec = Codec,
                    BitDepth = BitDepth,
                    OpenOutputFolder = OpenOutputFolder
                };
                if (Preset == RendererPreset.Custom)
                {
                    options.Width = width;
                    options.Height = height;
                    options.Fps = fps;
                    options.BitrateMbps = bitrate;
                }
                return true;
            }

            internal void ApplyTo(RendererSettings settings, RenderRequestOptions options)
            {
                settings.Preset = Preset;
                if (Preset == RendererPreset.Custom)
                {
                    settings.Width = options.Width.Value;
                    settings.Height = options.Height.Value;
                    settings.Fps = options.Fps.Value;
                    settings.BitrateMbps = options.BitrateMbps.Value;
                }
                settings.EndDelaySeconds = options.EndDelaySeconds.Value;
                settings.CaptureAudio = CaptureAudio;
                settings.BgaMode = BgaMode;
                settings.ShowPlanetRings = ShowPlanetRings;
                settings.ShowSongTitle = ShowSongTitle;
                settings.ShowCountdown = ShowCountdown;
                settings.ShowResultText = ShowResultText;
                settings.Encoding = Encoding;
                settings.Encoder = Encoder;
                settings.Codec = Codec;
                settings.BitDepth = BitDepth;
                settings.OpenOutputFolder = OpenOutputFolder;
                settings.OnChange();
            }

            private void SetVideoValues(int width, int height, int fps, int bitrate)
            {
                WidthText = width.ToString(CultureInfo.InvariantCulture);
                HeightText = height.ToString(CultureInfo.InvariantCulture);
                FpsText = fps.ToString(CultureInfo.InvariantCulture);
                BitrateText = bitrate.ToString(CultureInfo.InvariantCulture);
            }

            private static bool TryParseFloat(string value, out float result)
            {
                return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                    || float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
            }
        }
    }
}
