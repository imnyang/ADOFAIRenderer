using System;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;
using UnityEngine.SceneManagement;
using OrbitRender.Renderer;
using OrbitRender.UI;
namespace OrbitRender
{
    [EnableReloading]
    public static class Main
    {
        internal static UnityModManager.ModEntry Entry;
        internal static bool Enabled;
        internal static bool RpcEnabled { get; private set; }
        internal static int RpcPort { get; private set; } = 1108;
        internal static RendererRpcServer RpcServer { get; private set; }
        internal static RendererSettings Settings;
        private static Harmony harmony;
        private static GameObject host;
        private static string diagnosticsSummary = Localization.Text(
            "Diagnostics have not been run.", "진단을 아직 실행하지 않았습니다.");
        private static string diagnosticsReport = Localization.Text(
            "Click Run diagnostics to check FFmpeg, the output folder, the encoder, and game audio.",
            "진단 실행을 눌러 FFmpeg, 출력 폴더, 인코더 및 게임 오디오를 확인하세요.");
        private static string localizedWidthText;
        private static string localizedHeightText;
        private static string localizedFpsText;
        private static string localizedBitrateText;
        private static string localizedEndDelayText;
        private static int localizedWidthValue = int.MinValue;
        private static int localizedHeightValue = int.MinValue;
        private static int localizedFpsValue = int.MinValue;
        private static int localizedBitrateValue = int.MinValue;
        private static float localizedEndDelayValue = float.NaN;
        private static bool diagnosticsHaveRun;
        private static bool renderOptionsExpanded = true;
        private static bool visibleComponentsExpanded = true;
        private static bool encodingExpanded;
        private static bool filesExpanded;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry;
            try
            {
                ReadCommandLineOptions();
                Settings = RendererSettings.Load(entry);
                Settings.OnChange();
                harmony = new Harmony(entry.Info.Id);
                harmony.PatchAll(typeof(Main).Assembly);
                host = new GameObject("OrbitRender");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<RendererController>();
                SceneManager.sceneLoaded += OnSceneLoaded;
                Enabled = true;
                StartRpcServer();
                FfmpegInstaller.Start(entry, Settings);
                UpdateManager.Start(entry);
                entry.OnToggle = (mod, enabled) =>
                {
                    if (!enabled)
                    {
                        ExportVideoDialog.CloseDialog();
                        RendererController.Instance?.StopAndClean();
                    }
                    Enabled = enabled;
                    if (!enabled) StopRpcServer();
                    else StartRpcServer();
                    return true;
                };
                entry.OnGUI = mod =>
                {
                    if (Settings == null) return;
                    if (!diagnosticsHaveRun)
                    {
                        diagnosticsSummary = Localization.Text(
                            "Diagnostics have not been run.", "진단을 아직 실행하지 않았습니다.");
                        diagnosticsReport = Localization.Text(
                            "Click Run diagnostics to check FFmpeg, the output folder, the encoder, and game audio.",
                            "진단 실행을 눌러 FFmpeg, 출력 폴더, 인코더 및 게임 오디오를 확인하세요.");
                    }
                    DrawSettings();
                };
                entry.OnUpdate = (mod, deltaTime) => UpdateManager.PumpMainThread();
                entry.OnSaveGUI = mod => Settings?.Save(mod);
                entry.OnUnload = mod =>
                {
                    ExportVideoDialog.CloseDialog();
                    RendererController.Instance?.StopAndClean();
                    StopRpcServer();
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    UnityEngine.Object.Destroy(host);
                    harmony.UnpatchAll(mod.Info.Id);
                    Settings = null;
                    return true;
                };
                entry.Logger.Log("Renderer loaded. Unity " + Application.unityVersion);
                return true;
            }
            catch (Exception ex)
            {
                if (host != null) UnityEngine.Object.Destroy(host);
                harmony?.UnpatchAll(entry.Info.Id);
                entry.Logger.Error(ex.ToString());
                return false;
            }
        }

        private static void ReadCommandLineOptions()
        {
            RpcEnabled = false;
            RpcPort = 1108;
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, "--renderer-rpc", StringComparison.OrdinalIgnoreCase))
                {
                    RpcEnabled = true;
                    continue;
                }
                const string prefix = "--renderer-rpc-port=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(argument.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && port >= 1 && port <= 65535)
                {
                    RpcPort = port;
                    RpcEnabled = true;
                }
            }
        }

        private static void StartRpcServer()
        {
            if (!RpcEnabled || RpcServer != null || RendererController.Instance == null) return;
            try
            {
                RpcServer = new RendererRpcServer(RendererController.Instance, RpcPort);
                RpcServer.Start();
            }
            catch (Exception ex)
            {
                RpcServer = null;
                Entry.Logger.Error("Renderer RPC could not start on port " + RpcPort + ": " + ex);
            }
        }

        private static void StopRpcServer()
        {
            var server = RpcServer;
            RpcServer = null;
            try { server?.Dispose(); } catch (Exception ex) { Entry.Logger.Error("Renderer RPC shutdown: " + ex); }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Enabled) return;
            // ADOFAI's KillAll cleanup can remove DontDestroyOnLoad objects
            // while switching between menu, editor, and gameplay scenes.
            // Recreate our tiny host so RPC jobs survive that transition.
            if (RendererController.Instance == null)
            {
                host = new GameObject("OrbitRender");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<RendererController>();
                RpcServer?.Rebind(RendererController.Instance);
                Entry.Logger.Log("Renderer host recreated after scene load: " + scene.name);
            }
        }

        private static void DrawSettings()
        {
            GUILayout.Label(Localization.Text(
                "Configure the defaults used when exporting a video. Per-export settings can be changed from the Export Video window.",
                "비디오 내보내기에 사용할 기본값입니다. 개별 내보내기 설정은 비디오 내보내기 창에서 바꿀 수 있습니다."));
            GUILayout.Space(4f);

            DrawRenderSettings();

            if (SettingsUi.DrawSectionHeader(Localization.Text("Render options", "렌더 옵션"),
                ref renderOptionsExpanded))
            {
                DrawRenderOptions();
            }

            if (SettingsUi.DrawSectionHeader(Localization.Text("Visible components", "표시할 구성 요소"),
                ref visibleComponentsExpanded))
            {
                DrawVisibleComponents();
            }

            GUILayout.Space(6f);
            if (SettingsUi.DrawSectionHeader(Localization.Text("Encoding", "인코딩"),
                ref encodingExpanded))
            {
                DrawEncodingSettings();
            }

            GUILayout.Space(6f);
            if (SettingsUi.DrawSectionHeader(Localization.Text("Files & troubleshooting", "파일 및 문제 해결"),
                ref filesExpanded))
            {
                DrawPathSettings();
                DrawFfmpegInstallControls();
                DrawDiagnostics();
                GUILayout.Space(4f);
                if (GUILayout.Button(Localization.Text("Reset render settings to defaults", "렌더링 설정을 기본값으로 복원")))
                {
                    Settings.ResetToDefaults();
                    Settings.OnChange();
                    ResetLocalizedFieldState();
                }
            }
        }

        private static void DrawRenderSettings()
        {
            GUILayout.Label(Localization.Text("Preset", "프리셋"));
            var previousPreset = Settings.Preset;
            Settings.Preset = SettingsUi.DrawPreset(Settings.Preset);
            if (Settings.Preset != previousPreset)
            {
                Settings.OnChange();
                ResetLocalizedFieldState();
            }

            if (Settings.Preset == RendererPreset.Custom)
            {
                GUILayout.BeginHorizontal();
                Settings.Width = DrawLocalizedIntField(
                    Localization.Text("Width", "너비"), Settings.Width,
                    ref localizedWidthText, ref localizedWidthValue, 90f);
                Settings.Height = DrawLocalizedIntField(
                    Localization.Text("Height", "높이"), Settings.Height,
                    ref localizedHeightText, ref localizedHeightValue, 90f);
                Settings.Fps = DrawLocalizedIntField(
                    "FPS", Settings.Fps,
                    ref localizedFpsText, ref localizedFpsValue, 80f);
                Settings.BitrateMbps = DrawLocalizedIntField(
                    Localization.Text("Bitrate", "비트레이트"), Settings.BitrateMbps,
                    ref localizedBitrateText, ref localizedBitrateValue, 80f);
                GUILayout.Label("Mbps", GUILayout.Width(44f));
                GUILayout.EndHorizontal();
            }
            else
            {
                var profile = Settings.ResolveProfile();
                GUILayout.Label(Localization.Format(
                    "Preset output: {0} × {1} @ {2} FPS, {3} Mbps",
                    "프리셋 출력: {0} × {1} @ {2} FPS, {3} Mbps",
                    profile.Width, profile.Height, profile.Fps, profile.BitrateMbps));
            }
        }

        private static void DrawRenderOptions()
        {
            Settings.EndDelaySeconds = DrawLocalizedFloatField(
                Localization.Text("End delay (seconds)", "종료 지연(초)"), Settings.EndDelaySeconds,
                ref localizedEndDelayText, ref localizedEndDelayValue, 90f);
            Settings.CaptureAudio = DrawLocalizedToggle(
                Localization.Text("Capture audio", "오디오 캡처"), Settings.CaptureAudio);
            Settings.BgaMode = DrawLocalizedToggle(
                Localization.Text("BGA mode (hide tiles, planets & hit sounds)",
                    "BGA 모드 (타일, 행성 및 타격음 숨기기)"), Settings.BgaMode);
            Settings.OpenOutputFolder = DrawLocalizedToggle(
                Localization.Text("Open output folder after render", "렌더 후 출력 폴더 열기"),
                Settings.OpenOutputFolder);
        }

        private static void DrawVisibleComponents()
        {
            Settings.ShowPlanetRings = DrawLocalizedToggle(
                Localization.Text("Show planet rings", "행성 고리 표시"), Settings.ShowPlanetRings);
            Settings.ShowSongTitle = DrawLocalizedToggle(
                Localization.Text("Show song title", "곡 제목 표시"), Settings.ShowSongTitle);
            Settings.ShowCountdown = DrawLocalizedToggle(
                Localization.Text("Show countdown", "카운트다운 표시"), Settings.ShowCountdown);
            Settings.ShowResultText = DrawLocalizedToggle(
                Localization.Text("Show result text (hit judgments stay hidden)",
                    "결과 텍스트 표시 (판정은 숨김)"), Settings.ShowResultText);
        }

        private static void DrawEncodingSettings()
        {
            Settings.Encoding = SettingsUi.DrawEncoding(Settings.Encoding);
            Settings.Encoder = SettingsUi.DrawEncoder(Settings.Encoder);
            Settings.Codec = SettingsUi.DrawCodec(Settings.Codec);
            Settings.BitDepth = SettingsUi.DrawBitDepth(Settings.BitDepth);
        }

        private static void DrawPathSettings()
        {
            DrawPathField(
                Localization.Text("Output folder", "출력 폴더"),
                ref Settings.OutputDirectory,
                () => FileDialogService.PickFolder(GetOutputDialogDirectory()));

            DrawPathField(
                Localization.Text("FFmpeg executable", "FFmpeg 실행 파일"),
                ref Settings.FfmpegExecutable,
                () => FileDialogService.PickFile(GetFfmpegDialogDirectory()));
        }

        private static void ResetLocalizedFieldState()
        {
            localizedWidthText = null;
            localizedHeightText = null;
            localizedFpsText = null;
            localizedBitrateText = null;
            localizedEndDelayText = null;
            localizedWidthValue = int.MinValue;
            localizedHeightValue = int.MinValue;
            localizedFpsValue = int.MinValue;
            localizedBitrateValue = int.MinValue;
            localizedEndDelayValue = float.NaN;
        }

        private static bool DrawLocalizedToggle(string label, bool value)
        {
            return GUILayout.Toggle(value, label);
        }

        private static int DrawLocalizedIntField(string label, int value, ref string text,
            ref int syncedValue, float width)
        {
            if (text == null || syncedValue != value)
            {
                text = value.ToString(CultureInfo.InvariantCulture);
                syncedValue = value;
            }
            var edited = SettingsUi.LabeledField(label, text, width);
            if (!string.Equals(edited, text, StringComparison.Ordinal)) text = edited;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                syncedValue = parsed;
                return parsed;
            }
            return value;
        }

        private static float DrawLocalizedFloatField(string label, float value, ref string text,
            ref float syncedValue, float width)
        {
            if (text == null || float.IsNaN(syncedValue) || Math.Abs(syncedValue - value) > 0.0001f)
            {
                text = value.ToString("0.##", CultureInfo.InvariantCulture);
                syncedValue = value;
            }
            var edited = SettingsUi.LabeledField(label, text, width);
            if (!string.Equals(edited, text, StringComparison.Ordinal)) text = edited;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                syncedValue = parsed;
                return parsed;
            }
            return value;
        }

        private static void DrawFfmpegInstallControls()
        {
            if (!FfmpegInstaller.NeedsInstallation) return;

            GUILayout.Space(8f);
            GUILayout.Label(Localization.Text("FFmpeg setup", "FFmpeg 설정"));
            GUILayout.Label(FfmpegInstaller.StatusMessage);
            if (FfmpegInstaller.IsDownloading) return;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Text("Install FFmpeg", "FFmpeg 설치"),
                GUILayout.ExpandWidth(false)))
                FfmpegInstaller.ConfirmInstall();
            if (FfmpegInstaller.IsAwaitingConsent
                && GUILayout.Button(Localization.Text("Not now", "나중에"), GUILayout.ExpandWidth(false)))
                FfmpegInstaller.DeclineInstall();
            GUILayout.EndHorizontal();
        }

        private static void DrawPathField(string label, ref string value, Func<string> pick)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            var current = value ?? string.Empty;
            var edited = GUILayout.TextField(current, GUILayout.ExpandWidth(true));
            if (!string.Equals(edited, current, StringComparison.Ordinal))
            {
                value = edited;
            }

            if (GUILayout.Button(Localization.Text("Browse...", "찾아보기..."), GUILayout.ExpandWidth(false)))
            {
                var selected = pick();
                if (!string.IsNullOrEmpty(selected))
                {
                    value = selected;
                }
            }
            GUILayout.EndHorizontal();
        }

        private static string GetOutputDialogDirectory()
        {
            try { return FileDialogService.FindExistingDirectory(Settings.ResolveOutputDirectory()); }
            catch (Exception ex)
            {
                Entry.Logger.Log("Could not resolve output folder for file picker: " + ex.Message);
                return FileDialogService.GetGameDirectory();
            }
        }

        private static string GetFfmpegDialogDirectory()
        {
            try
            {
                var configured = Environment.ExpandEnvironmentVariables((Settings.FfmpegExecutable ?? string.Empty).Trim());
                if (!string.IsNullOrEmpty(configured))
                {
                    if (!Path.IsPathRooted(configured))
                        configured = Path.Combine(Entry.Path, configured);
                    return FileDialogService.FindExistingDirectory(configured);
                }
            }
            catch (Exception ex)
            {
                Entry.Logger.Log("Could not resolve FFmpeg path for file picker: " + ex.Message);
            }
            return FileDialogService.GetGameDirectory();
        }

        private static void DrawDiagnostics()
        {
            GUILayout.Space(8f);
            GUILayout.Label(Localization.Text("Diagnostics", "진단"));
            if (!diagnosticsHaveRun)
            {
                GUILayout.Label(Localization.Text(
                    "Run this before reporting an export problem. It checks FFmpeg, the output folder, the encoder, and game audio.",
                    "내보내기 문제를 제보하기 전에 실행하세요. FFmpeg, 출력 폴더, 인코더 및 게임 오디오를 확인합니다."));
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Text("Run diagnostics", "진단 실행"),
                GUILayout.ExpandWidth(false))) RunDiagnostics();
            if (diagnosticsHaveRun
                && GUILayout.Button(Localization.Text("Copy report", "보고서 복사"),
                    GUILayout.ExpandWidth(false)))
                GUIUtility.systemCopyBuffer = diagnosticsSummary + Environment.NewLine + diagnosticsReport;
            GUILayout.EndHorizontal();
            if (diagnosticsHaveRun)
            {
                GUILayout.Label(diagnosticsSummary);
                GUILayout.TextArea(diagnosticsReport, GUILayout.MinHeight(92f));
            }
        }

        private static void RunDiagnostics()
        {
            try
            {
                var result = RendererDiagnostics.Run(Settings);
                diagnosticsHaveRun = true;
                diagnosticsSummary = result.Summary;
                diagnosticsReport = result.Report;
                if (result.HasErrors) Entry.Logger.Error(diagnosticsSummary + Environment.NewLine + diagnosticsReport);
                else Entry.Logger.Log(diagnosticsSummary + Environment.NewLine + diagnosticsReport);
            }
            catch (Exception ex)
            {
                diagnosticsHaveRun = true;
                diagnosticsSummary = Localization.Text("Diagnostics failed unexpectedly.",
                    "진단 중 예기치 않은 오류가 발생했습니다.");
                diagnosticsReport = ex.ToString();
                Entry.Logger.Error("Renderer diagnostics failed: " + ex);
            }
        }
    }
}
