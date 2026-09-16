using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;
using ADOFAIRenderer.Renderer;

namespace ADOFAIRenderer
{
    internal static class UpdateManager
    {
        private const string Repository = "imnyang/ADOFAIRenderer";
        private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const long MaximumDownloadBytes = 1024L * 1024L * 1024L;
        private static int started;
        private static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        internal static void Start(UnityModManager.ModEntry entry)
        {
            if (entry == null || Interlocked.Exchange(ref started, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => CheckForUpdate(entry));
        }

        internal static void PumpMainThread()
        {
            if (RendererController.Instance != null && RendererController.Instance.Busy) return;
            Action action;
            while (mainThreadActions.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception ex) { Main.Entry?.Logger.Error("Update action failed: " + ex); }
                if (RendererController.Instance != null && RendererController.Instance.Busy) return;
            }
        }

        private static void QueueMainThread(Action action)
        {
            if (action != null) mainThreadActions.Enqueue(action);
        }

        private static void CheckForUpdate(UnityModManager.ModEntry entry)
        {
            string tempRoot = null;
            try
            {
                var release = GetLatestRelease();
                if (release == null) return;

                var tag = release.Value<string>("tag_name");
                Version latestVersion;
                if (!TryParseVersion(tag, out latestVersion))
                {
                    entry.Logger.Log("Automatic update skipped: GitHub release tag is not a supported version: " + tag);
                    return;
                }

                Version currentVersion;
                if (!TryParseVersion(entry.Version == null ? null : entry.Version.ToString(), out currentVersion))
                {
                    entry.Logger.Log("Automatic update skipped: installed version is not a supported version: " + entry.Version);
                    return;
                }
                if (latestVersion.CompareTo(currentVersion) <= 0) return;

                var asset = FindReleaseAsset(release["assets"] as JArray);
                if (asset == null)
                {
                    entry.Logger.Log("Automatic update skipped: release " + tag + " has no ADOFAIRenderer ZIP asset.");
                    return;
                }

                tempRoot = Path.Combine(Path.GetTempPath(), "ADOFAIRenderer-update-" + Guid.NewGuid().ToString("N"));
                var archivePath = Path.Combine(tempRoot, "release.zip");
                var extractionPath = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(tempRoot);
                DownloadAsset((string)asset["browser_download_url"], archivePath, asset);
                var packageRoot = ExtractPackage(archivePath, extractionPath, latestVersion);
                if (!IsWindows()) EnsureUnixExecutables(packageRoot);
                QueueHotApply(packageRoot, entry.Path, tempRoot, entry, latestVersion);
                tempRoot = null;
                entry.Logger.Log("Update " + FormatVersion(latestVersion) + " downloaded. It will be applied while ADOFAI is running.");
            }
            catch (Exception ex)
            {
                if (tempRoot != null) TryDelete(tempRoot);
                entry.Logger.Log("Automatic update check failed: " + ex.Message);
            }
        }

        private static void QueueHotApply(string packageRoot, string targetDirectory, string tempRoot,
            UnityModManager.ModEntry entry, Version version)
        {
            QueueMainThread(() => StartPackageCopy(packageRoot, targetDirectory, tempRoot, entry, version));
        }

        private static void StartPackageCopy(string packageRoot, string targetDirectory, string tempRoot,
            UnityModManager.ModEntry entry, Version version)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    CopyPackageToTarget(packageRoot, targetDirectory);
                    QueueMainThread(() => ReloadCurrentMod(entry, packageRoot, targetDirectory, tempRoot, version));
                }
                catch (Exception ex)
                {
                    entry.Logger.Log("Hot update could not replace the loaded files: " + ex.Message);
                    // Keep the staged package and fall back to the old safe
                    // path if UMM or the OS still has the DLL locked.
                    try
                    {
                        StartApplyHelper(packageRoot, targetDirectory, tempRoot, Process.GetCurrentProcess().Id);
                        entry.Logger.Log("The update will be installed after ADOFAI exits.");
                    }
                    catch (Exception fallbackException)
                    {
                        TryDelete(tempRoot);
                        entry.Logger.Error("Could not schedule the update installer: " + fallbackException);
                    }
                }
            });
        }

        private static void CopyPackageToTarget(string packageRoot, string targetDirectory)
        {
            foreach (var source in Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var relative = source.Substring(packageRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var destination = Path.Combine(targetDirectory, relative);
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                Exception lastException = null;
                var copied = false;
                for (var attempt = 0; attempt < 10 && !copied; attempt++)
                {
                    try
                    {
                        File.Copy(source, destination, true);
                        copied = true;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        Thread.Sleep(250);
                    }
                }
                if (!copied) throw new IOException("Could not replace " + relative, lastException);
            }
        }

        private static void ReloadCurrentMod(UnityModManager.ModEntry entry, string packageRoot,
            string targetDirectory, string tempRoot, Version version)
        {
            try
            {
                var reload = entry.GetType().GetMethod("Reload", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (reload == null) throw new MissingMethodException("Unity Mod Manager ModEntry.Reload was not found.");
                reload.Invoke(entry, null);
                TryDelete(tempRoot);
                entry.Logger.Log("Update " + FormatVersion(version) + " applied without restarting ADOFAI.");
            }
            catch (Exception ex)
            {
                entry.Logger.Log("Hot reload failed: " + ex.Message);
                try
                {
                    StartApplyHelper(packageRoot, targetDirectory, tempRoot, Process.GetCurrentProcess().Id);
                    entry.Logger.Log("The update will be installed after ADOFAI exits.");
                }
                catch (Exception fallbackException)
                {
                    TryDelete(tempRoot);
                    entry.Logger.Error("Could not schedule the update installer: " + fallbackException);
                }
            }
        }

        private static JObject GetLatestRelease()
        {
            string json;
            try
            {
                json = DownloadText(LatestReleaseUrl, "application/vnd.github+json");
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound) return null;
                throw;
            }
            var release = JObject.Parse(json);

            // /releases/latest already excludes drafts and pre-releases. Keep
            // these checks explicit so a future API/proxy change cannot make a
            // beta build install automatically.
            if (release.Value<bool?>("draft") == true || release.Value<bool?>("prerelease") == true)
                return null;
            return release;
        }

        private static JObject FindReleaseAsset(JArray assets)
        {
            if (assets == null) return null;
            JObject fallback = null;
            foreach (var token in assets)
            {
                var asset = token as JObject;
                if (asset == null) continue;
                var name = asset.Value<string>("name") ?? string.Empty;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "ADOFAIRenderer.zip", StringComparison.OrdinalIgnoreCase)) return asset;
                if (fallback == null && name.StartsWith("ADOFAIRenderer", StringComparison.OrdinalIgnoreCase)) fallback = asset;
            }
            return fallback;
        }

        private static string DownloadText(string url, string accept)
        {
            using (var response = SendRequest(url, accept))
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void DownloadAsset(string url, string destination, JObject asset)
        {
            var expectedSize = asset.Value<long?>("size") ?? -1;
            if (expectedSize > MaximumDownloadBytes)
                throw new InvalidDataException("GitHub asset is larger than the update limit.");

            using (var response = SendRequest(url, "application/octet-stream"))
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumDownloadBytes) throw new InvalidDataException("Downloaded update is too large.");
                    output.Write(buffer, 0, read);
                }
                if (expectedSize >= 0 && total != expectedSize)
                    throw new InvalidDataException("Downloaded update size does not match the GitHub asset metadata.");
            }

            var digest = asset.Value<string>("digest");
            if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expectedHash = digest.Substring("sha256:".Length).Trim();
                using (var sha256 = SHA256.Create())
                using (var input = File.OpenRead(destination))
                {
                    var actualHash = ToHex(sha256.ComputeHash(input));
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Downloaded update SHA-256 does not match the GitHub asset digest.");
                }
            }
        }

        private static HttpWebResponse SendRequest(string url, string accept)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "ADOFAIRenderer-Updater/1.0";
            request.Accept = accept;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            return (HttpWebResponse)request.GetResponse();
        }

        private static string ExtractPackage(string archivePath, string extractionPath, Version expectedVersion)
        {
            Directory.CreateDirectory(extractionPath);
            var extractionRoot = Path.GetFullPath(extractionPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.GetFullPath(Path.Combine(extractionPath, entryPath));
                    if (!destination.StartsWith(extractionRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The update archive contains an unsafe path.");
                }
            }
            ZipFile.ExtractToDirectory(archivePath, extractionPath);

            foreach (var infoPath in Directory.GetFiles(extractionPath, "Info.json", SearchOption.AllDirectories))
            {
                try
                {
                    var info = JObject.Parse(File.ReadAllText(infoPath));
                    if (!string.Equals((string)info["Id"], "ADOFAIRenderer", StringComparison.OrdinalIgnoreCase)) continue;
                    Version packageVersion;
                    if (!TryParseVersion((string)info["Version"], out packageVersion)
                        || packageVersion.CompareTo(expectedVersion) != 0)
                        throw new InvalidDataException("The update package version does not match the release tag.");
                    return Path.GetDirectoryName(infoPath);
                }
                catch (JsonReaderException)
                {
                    // Ignore unrelated Info.json files in the archive.
                }
            }
            throw new InvalidDataException("The update archive does not contain a valid ADOFAIRenderer package.");
        }

        private static void StartApplyHelper(string sourceDirectory, string targetDirectory, string tempRoot, int processId)
        {
            var scriptPath = Path.Combine(tempRoot, IsWindows() ? "apply-update.ps1" : "apply-update.sh");
            if (IsWindows())
            {
                var script =
                    "$ErrorActionPreference = 'Stop'\r\n" +
                    "$pidToWait = " + processId.ToString() + "\r\n" +
                    "$source = " + PowerShellLiteral(sourceDirectory) + "\r\n" +
                    "$target = " + PowerShellLiteral(targetDirectory) + "\r\n" +
                    "$cleanup = " + PowerShellLiteral(tempRoot) + "\r\n" +
                    "for ($i = 0; $i -lt 240; $i++) { if (-not (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }\r\n" +
                    "$updated = $false\r\n" +
                    "for ($i = 0; $i -lt 30 -and -not $updated; $i++) { try { New-Item -ItemType Directory -Force -Path $target | Out-Null; Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force; $updated = $true } catch { Start-Sleep -Seconds 1 } }\r\n" +
                    "if ($updated) { Remove-Item -LiteralPath $cleanup -Recurse -Force } else { exit 1 }\r\n";
                File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
                StartDetachedProcess("powershell.exe", "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + WindowsArgument(scriptPath));
            }
            else
            {
                var script =
                    "#!/bin/sh\n" +
                    "pid_to_wait=" + ShellLiteral(processId.ToString()) + "\n" +
                    "source=" + ShellLiteral(sourceDirectory) + "\n" +
                    "target=" + ShellLiteral(targetDirectory) + "\n" +
                    "cleanup=" + ShellLiteral(tempRoot) + "\n" +
                    "i=0\n" +
                    "while kill -0 \"$pid_to_wait\" 2>/dev/null && [ $i -lt 240 ]; do sleep 0.5; i=$((i + 1)); done\n" +
                    "attempt=0\n" +
                    "while [ $attempt -lt 30 ]; do mkdir -p \"$target\" && cp -R \"$source/.\" \"$target/\" && rm -rf \"$cleanup\" && exit 0; attempt=$((attempt + 1)); sleep 1; done\n" +
                    "exit 1\n";
                File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
                StartDetachedProcess("/bin/sh", ShellLiteral(scriptPath));
            }
        }

        private static void EnsureUnixExecutables(string packageRoot)
        {
            foreach (var name in new[] { "ffmpeg" })
            {
                foreach (var path in Directory.GetFiles(packageRoot, name, SearchOption.AllDirectories))
                {
                    using (var chmod = Process.Start(new ProcessStartInfo {
                        FileName = "/bin/chmod",
                        Arguments = "+x " + UnixArgument(path),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        if (chmod == null || !chmod.WaitForExit(5000) || chmod.ExitCode != 0)
                            throw new InvalidOperationException("Could not make the Unix FFmpeg executable.");
                    }
                }
            }
        }

        private static void StartDetachedProcess(string fileName, string arguments)
        {
            var process = Process.Start(new ProcessStartInfo {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) throw new InvalidOperationException("Could not start the update installer.");
            process.Dispose();
        }

        private static bool IsWindows()
        {
            return Path.DirectorySeparatorChar == '\\';
        }

        private static string PowerShellLiteral(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private static string ShellLiteral(string value)
        {
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        private static string WindowsArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string UnixArgument(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(value)) return false;
            var normalized = value.Trim();
            if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
            var suffix = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0) normalized = normalized.Substring(0, suffix);
            var parts = normalized.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            var numbers = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
                    return false;
            }
            version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
            return true;
        }

        private static string FormatVersion(Version version)
        {
            return version.Build == 0 && version.Revision == 0
                ? version.Major + "." + version.Minor
                : version.Major + "." + version.Minor + "." + version.Build;
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }
    }
}
