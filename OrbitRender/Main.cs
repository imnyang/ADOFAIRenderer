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
                    if (Localization.IsKorean)
                        DrawLocalizedSettings();
                    else
                        UnityModManager.UI.DrawFields(ref Settings, mod, DrawFieldMask.Any, Settings.OnChange);
                    DrawPathSettings();
                    DrawFfmpegInstallControls();
                    DrawDiagnostics();
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

        private static void DrawLocalizedSettings()
        {
            GUILayout.Label("렌더러 설정");
            var previousPreset = Settings.Preset;
            Settings.Preset = (RendererPreset)GUILayout.Toolbar((int)Settings.Preset, new[] {
                "사용자 지정", "미리보기", "FullHD", "QHD", "UHD 4K"
            });
            if (Settings.Preset != previousPreset) Settings.OnChange();
            if (Settings.Preset == RendererPreset.Custom)
            {
                Settings.Width = DrawLocalizedIntField("너비", Settings.Width,
                    ref localizedWidthText, ref localizedWidthValue);
                Settings.Height = DrawLocalizedIntField("높이", Settings.Height,
                    ref localizedHeightText, ref localizedHeightValue);
                Settings.Fps = DrawLocalizedIntField("목표 FPS", Settings.Fps,
                    ref localizedFpsText, ref localizedFpsValue);
                Settings.BitrateMbps = DrawLocalizedIntField("비디오 비트레이트(Mbps)",
                    Settings.BitrateMbps, ref localizedBitrateText, ref localizedBitrateValue);
            }

            Settings.EndDelaySeconds = DrawLocalizedFloatField("종료 지연(초)", Settings.EndDelaySeconds,
                ref localizedEndDelayText, ref localizedEndDelayValue);
            Settings.CaptureAudio = DrawLocalizedToggle("오디오 캡처", Settings.CaptureAudio);
            Settings.BgaMode = DrawLocalizedToggle("BGA 모드 (타일, 행성 및 타격음 숨기기)", Settings.BgaMode);
            Settings.ShowPlanetRings = DrawLocalizedToggle("행성 고리 표시", Settings.ShowPlanetRings);
            Settings.ShowSongTitle = DrawLocalizedToggle("곡 제목 표시", Settings.ShowSongTitle);
            Settings.ShowCountdown = DrawLocalizedToggle("카운트다운 표시", Settings.ShowCountdown);
            Settings.ShowResultText = DrawLocalizedToggle("결과 텍스트 표시 (판정은 숨김)", Settings.ShowResultText);

            GUILayout.Label("인코딩 속도");
            Settings.Encoding = (EncoderSpeed)GUILayout.Toolbar((int)Settings.Encoding,
                new[] { "최대 속도", "균형", "품질" });

            GUILayout.Label("비디오 인코더");
            Settings.Encoder = (VideoEncoder)DrawLocalizedEncoderToolbar(Settings.Encoder);

            GUILayout.Label("비디오 코덱");
            Settings.Codec = (VideoCodec)GUILayout.Toolbar((int)Settings.Codec,
                new[] { "H.264", "H.265", "VP9", "AV1" });

            GUILayout.Label("비트 깊이");
            Settings.BitDepth = (VideoBitDepth)GUILayout.Toolbar((int)Settings.BitDepth,
                new[] { "8비트", "10비트" });
            Settings.OpenOutputFolder = DrawLocalizedToggle("렌더 후 출력 폴더 열기", Settings.OpenOutputFolder);
        }

        private static int DrawLocalizedEncoderToolbar(VideoEncoder value)
        {
            var selected = value == VideoEncoder.Auto ? 0
                : value == VideoEncoder.NvidiaNvenc ? 1
                : value == VideoEncoder.IntelQsv ? 2
                : value == VideoEncoder.AmdAmf ? 3 : 4;
            selected = GUILayout.Toolbar(selected,
                new[] { "자동", "NVIDIA NVENC", "Intel QSV", "AMD AMF", "소프트웨어" });
            switch (selected)
            {
                case 1: return (int)VideoEncoder.NvidiaNvenc;
                case 2: return (int)VideoEncoder.IntelQsv;
                case 3: return (int)VideoEncoder.AmdAmf;
                case 4: return (int)VideoEncoder.Software;
                default: return (int)VideoEncoder.Auto;
            }
        }

        private static bool DrawLocalizedToggle(string label, bool value)
        {
            return GUILayout.Toggle(value, label);
        }

        private static int DrawLocalizedIntField(string label, int value, ref string text, ref int syncedValue)
        {
            if (text == null || syncedValue != value)
            {
                text = value.ToString(CultureInfo.InvariantCulture);
                syncedValue = value;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            var edited = GUILayout.TextField(text, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (!string.Equals(edited, text, StringComparison.Ordinal)) text = edited;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                syncedValue = parsed;
                return parsed;
            }
            return value;
        }

        private static float DrawLocalizedFloatField(string label, float value, ref string text,
            ref float syncedValue)
        {
            if (text == null || float.IsNaN(syncedValue) || Math.Abs(syncedValue - value) > 0.0001f)
            {
                text = value.ToString("0.##", CultureInfo.InvariantCulture);
                syncedValue = value;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.ExpandWidth(false));
            var edited = GUILayout.TextField(text, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
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
            GUILayout.Label("FFmpeg");
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
                if (!Localization.IsKorean) Settings.OnChange();
            }

            if (GUILayout.Button(Localization.Text("Browse...", "찾아보기..."), GUILayout.ExpandWidth(false)))
            {
                var selected = pick();
                if (!string.IsNullOrEmpty(selected))
                {
                    value = selected;
                    if (!Localization.IsKorean) Settings.OnChange();
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
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.Text("Run diagnostics", "진단 실행"),
                GUILayout.ExpandWidth(false))) RunDiagnostics();
            if (GUILayout.Button(Localization.Text("Copy report", "보고서 복사"),
                GUILayout.ExpandWidth(false)))
                GUIUtility.systemCopyBuffer = diagnosticsSummary + Environment.NewLine + diagnosticsReport;
            GUILayout.EndHorizontal();
            GUILayout.Label(diagnosticsSummary);
            GUILayout.TextArea(diagnosticsReport, GUILayout.MinHeight(92f));
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
