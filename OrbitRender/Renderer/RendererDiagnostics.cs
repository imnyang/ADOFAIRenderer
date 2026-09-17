using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace OrbitRender.Renderer
{
    internal sealed class RendererDiagnosticResult
    {
        private readonly List<string> lines = new List<string>();
        internal int Errors { get; private set; }
        internal int Warnings { get; private set; }
        internal bool HasErrors => Errors > 0;
        internal string Summary
        {
            get
            {
                if (Errors > 0)
                    return Localization.FormatWithCurrentCulture(
                        "Diagnostics found {0} error(s) and {1} warning(s).",
                        "진단에서 오류 {0}개와 경고 {1}개를 발견했습니다.", Errors, Warnings);
                if (Warnings > 0)
                    return Localization.FormatWithCurrentCulture(
                        "Diagnostics passed with {0} warning(s).",
                        "진단을 통과했지만 경고가 {0}개 있습니다.", Warnings);
                return Localization.Text("All diagnostics passed.", "모든 진단을 통과했습니다.");
            }
        }
        internal string Report => string.Join(Environment.NewLine, lines.ToArray());

        internal void Info(string message) { lines.Add("[INFO] " + message); }
        internal void Pass(string message) { lines.Add("[OK] " + message); }
        internal void Warn(string message) { Warnings++; lines.Add("[WARN] " + message); }
        internal void Error(string message) { Errors++; lines.Add("[ERROR] " + message); }
    }

    internal static class RendererDiagnostics
    {
        private sealed class ProcessResult
        {
            internal int ExitCode;
            internal string StandardOutput;
            internal string StandardError;
            internal string Error;
        }

        internal static RendererDiagnosticResult Run(RendererSettings settings)
        {
            var result = new RendererDiagnosticResult();
            result.Info("OrbitRender diagnostics started at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            result.Info("Platform: " + Application.platform + ", Unity " + Application.unityVersion);
            result.Info("Graphics: " + (SystemInfo.graphicsDeviceName ?? "unknown"));
            result.Info("Graphics vendor: " + (SystemInfo.graphicsDeviceVendor ?? "unknown")
                + ", API: " + SystemInfo.graphicsDeviceType
                + ", driver: " + (SystemInfo.graphicsDeviceVersion ?? "unknown")
                + ", memory: " + SystemInfo.graphicsMemorySize + " MB");
            result.Info("Async GPU readback: " + (SystemInfo.supportsAsyncGPUReadback ? "available" : "fallback path"));

            if (RendererController.Instance != null && RendererController.Instance.Busy)
                result.Warn("A render is currently active. Run diagnostics again while idle for a clean preflight.");
            if (!SystemInfo.supportsAsyncGPUReadback)
                result.Warn("Async GPU readback is unavailable; rendering will use the slower synchronous fallback.");

            if (settings == null)
            {
                result.Error("Renderer settings are unavailable.");
                return result;
            }

            RenderProfile profile;
            try
            {
                profile = settings.ResolveProfile();
                result.Info(string.Format("Profile: {0}x{1} @ {2} fps, {3} Mbps, {4}, {5}, {6}",
                    profile.Width, profile.Height, profile.Fps, profile.BitrateMbps, profile.FfmpegCodec,
                    profile.PixelFormat, profile.ContainerExtension));
            }
            catch (Exception ex)
            {
                result.Error("Could not resolve the render profile: " + ex.Message);
                return result;
            }

            CheckOutputDirectory(settings, result);
            CheckFfmpeg(settings, profile, result);
            CheckAudio(settings, result);
            return result;
        }

        private static void CheckOutputDirectory(RendererSettings settings, RendererDiagnosticResult result)
        {
            try
            {
                var directory = settings.ResolveOutputDirectory();
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".adofai-renderer-diagnostic-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "OrbitRender diagnostics" + Environment.NewLine, new UTF8Encoding(false));
                File.Delete(probe);
                result.Pass("Output folder is writable: " + directory);
            }
            catch (Exception ex)
            {
                result.Error("Output folder is not writable: " + ex.Message);
            }
        }

        private static void CheckFfmpeg(RendererSettings settings, RenderProfile profile, RendererDiagnosticResult result)
        {
            string executable;
            try { executable = RendererController.ResolveFfmpegExecutable(settings); }
            catch (Exception ex)
            {
                result.Error("Could not resolve FFmpeg: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(executable))
            {
                result.Error("FFmpeg executable is not configured or installed.");
                return;
            }
            result.Info("FFmpeg candidate: " + executable);

            var version = RunProcess(executable, "-hide_banner -version", 10000);
            if (!string.IsNullOrEmpty(version.Error))
            {
                result.Error("FFmpeg could not be started: " + version.Error);
                return;
            }
            if (version.ExitCode != 0)
            {
                result.Error("FFmpeg version check failed (exit code " + version.ExitCode + "). " + ShortError(version));
                return;
            }

            var versionLine = FirstNonEmptyLine(version.StandardOutput, version.StandardError);
            result.Pass("FFmpeg runs" + (string.IsNullOrEmpty(versionLine) ? "." : ": " + versionLine));

            var encoders = RunProcess(executable, "-hide_banner -encoders", 15000);
            if (!string.IsNullOrEmpty(encoders.Error))
            {
                result.Error("FFmpeg encoder check could not be started: " + encoders.Error);
                return;
            }
            if (encoders.ExitCode != 0)
            {
                result.Error("FFmpeg encoder check failed (exit code " + encoders.ExitCode + "). " + ShortError(encoders));
                return;
            }

            var encoderText = (encoders.StandardOutput ?? string.Empty) + Environment.NewLine + (encoders.StandardError ?? string.Empty);
            if (encoderText.IndexOf(profile.FfmpegCodec, StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Error("Selected video encoder is unavailable: " + profile.FfmpegCodec);
            }
            else
            {
                result.Pass("Video encoder is available: " + profile.FfmpegCodec);
            }

            if (settings.CaptureAudio)
            {
                var audioCodec = profile.AudioEncoder;
                if (encoderText.IndexOf(audioCodec, StringComparison.OrdinalIgnoreCase) < 0)
                    result.Error("Selected audio encoder is unavailable: " + audioCodec);
                else
                    result.Pass("Audio encoder is available: " + audioCodec);
            }

            result.Info("Container: " + profile.ContainerExtension + " (" + profile.ContainerMimeType + ").");
            if (FFmpegEncoder.TryValidateVideo(executable, profile.BitrateMbps, profile.FfmpegPreset,
                profile.FfmpegCodec, profile.PixelFormat, profile.ContainerExtension, !settings.CaptureAudio,
                out var validationError))
                result.Pass("Encoder smoke test passed: " + profile.FfmpegCodec + " / " + profile.PixelFormat + ".");
            else
                result.Error("Encoder smoke test failed: " + validationError);
        }

        private static void CheckAudio(RendererSettings settings, RendererDiagnosticResult result)
        {
            if (!settings.CaptureAudio)
            {
                result.Info("Game audio capture is disabled.");
                return;
            }

            try
            {
                if (AudioSettings.outputSampleRate <= 0)
                    result.Error("Unity audio output has no valid sample rate.");
                else
                {
                    result.Pass("Game audio output is available: " + AudioSettings.outputSampleRate
                        + " Hz, " + AudioSettings.speakerMode + ".");
                    var audioConfiguration = AudioSettings.GetConfiguration();
                    result.Info("Unity DSP buffer: " + audioConfiguration.dspBufferSize
                        + " samples; real voices=" + audioConfiguration.numRealVoices
                        + ", virtual voices=" + audioConfiguration.numVirtualVoices + ".");
                    if (audioConfiguration.dspBufferSize >= 1024)
                        result.Warn("The renderer will temporarily use a 512-sample DSP buffer during audio capture for macOS Unity compatibility.");
                    var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
                    var activeListener = listeners.FirstOrDefault(listener => listener != null && listener.enabled
                        && listener.gameObject != null && listener.gameObject.activeInHierarchy);
                    if (activeListener == null)
                        result.Warn("No active AudioListener was found; the audio fallback cannot capture the game mix.");
                    else if (activeListener.gameObject.GetComponents<AudioSource>().Length > 0
                        || activeListener.gameObject.GetComponents<AudioListener>().Length > 1)
                        result.Pass("AudioListener master-output fallback is available; the existing mixed AudioSource/AudioListener object will be left untouched.");
                    else
                        result.Pass("AudioListener fallback is available if Unity AudioRenderer returns no samples.");
                    switch (AudioSettings.speakerMode)
                    {
                        case AudioSpeakerMode.Mono:
                        case AudioSpeakerMode.Stereo:
                        case AudioSpeakerMode.Prologic:
                        case AudioSpeakerMode.Quad:
                        case AudioSpeakerMode.Surround:
                        case AudioSpeakerMode.Mode5point1:
                        case AudioSpeakerMode.Mode7point1:
                            break;
                        default:
                            result.Error("Unity audio speaker mode is not supported by the capture path: "
                                + AudioSettings.speakerMode + ".");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error("Could not inspect Unity audio output: " + ex.Message);
            }
        }

        private static ProcessResult RunProcess(string fileName, string arguments, int timeoutMilliseconds)
        {
            var result = new ProcessResult { StandardOutput = string.Empty, StandardError = string.Empty };
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    if (!process.Start())
                    {
                        result.Error = "Process.Start returned false.";
                        return result;
                    }

                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        result.Error = "Process timed out after " + timeoutMilliseconds + " ms.";
                        return result;
                    }
                    process.WaitForExit();
                    result.ExitCode = process.ExitCode;
                    result.StandardOutput = output.GetAwaiter().GetResult();
                    result.StandardError = error.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { result.Error = ex.Message; }
            return result;
        }

        private static string FirstNonEmptyLine(string first, string second)
        {
            foreach (var text in new[] { first, second })
            {
                if (string.IsNullOrEmpty(text)) continue;
                var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrEmpty(line)) return line.Trim();
            }
            return string.Empty;
        }

        private static string ShortError(ProcessResult result)
        {
            var text = FirstNonEmptyLine(result.StandardError, result.StandardOutput);
            return string.IsNullOrEmpty(text) ? "No details were returned." : text;
        }
    }
}
