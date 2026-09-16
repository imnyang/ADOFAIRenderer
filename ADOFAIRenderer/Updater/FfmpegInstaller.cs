using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityModManagerNet;

namespace ADOFAIRenderer
{
    // FFmpeg is an optional runtime dependency. Keeping the installer here
    // means release ZIPs contain only the managed mod and download one native
    // binary after the mod is loaded on the user's platform.
    internal static class FfmpegInstaller
    {
        private const long MaximumDownloadBytes = 200L * 1024L * 1024L;
        private const int NotStarted = 0;
        private const int Downloading = 1;
        private const int Ready = 2;
        private const int Failed = 3;
        private const int Skipped = 4;
        private static int state = NotStarted;
        private static string error;

        private sealed class PlatformSpec
        {
            internal readonly string Name;
            internal readonly string ExecutableName;
            internal readonly string Url;
            internal readonly bool TarXz;

            internal PlatformSpec(string name, string executableName, string url, bool tarXz)
            {
                Name = name;
                ExecutableName = executableName;
                Url = url;
                TarXz = tarXz;
            }
        }

        internal static bool IsDownloading
        {
            get { return Interlocked.CompareExchange(ref state, NotStarted, NotStarted) == Downloading; }
        }

        internal static string StatusMessage
        {
            get
            {
                var current = Interlocked.CompareExchange(ref state, NotStarted, NotStarted);
                if (current == Downloading) return "Downloading FFmpeg for this platform...";
                if (current == Failed) return "FFmpeg could not be installed automatically: " + error + ". Check the log or set FFmpeg executable manually.";
                return string.Empty;
            }
        }

        internal static void Start(UnityModManager.ModEntry entry, RendererSettings settings)
        {
            if (entry == null || Interlocked.CompareExchange(ref state, Downloading, NotStarted) != NotStarted) return;

            try
            {
                if (settings != null && !string.IsNullOrWhiteSpace(settings.FfmpegExecutable))
                {
                    Interlocked.Exchange(ref state, Skipped);
                    entry.Logger.Log("FFmpeg auto-install skipped because a custom executable is configured.");
                    return;
                }

                var spec = GetPlatformSpec();
                var destinationDirectory = Path.Combine(entry.Path, "FFmpeg", spec.Name);
                var destination = Path.Combine(destinationDirectory, spec.ExecutableName);
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                {
                    EnsureUnixExecutable(destination, spec);
                    Interlocked.Exchange(ref state, Ready);
                    entry.Logger.Log("Using installed FFmpeg: " + destination);
                    return;
                }

                entry.Logger.Log("FFmpeg is not installed. Downloading the " + spec.Name + " binary.");
                ThreadPool.QueueUserWorkItem(_ => Install(entry, spec, destinationDirectory, destination));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Interlocked.Exchange(ref state, Failed);
                entry.Logger.Log("FFmpeg auto-install is unavailable: " + ex.Message);
            }
        }

        private static void Install(UnityModManager.ModEntry entry, PlatformSpec spec,
            string destinationDirectory, string destination)
        {
            string temporaryRoot = null;
            string temporaryDestination = null;
            try
            {
                temporaryRoot = Path.Combine(Path.GetTempPath(), "ADOFAIRenderer-ffmpeg-" + Guid.NewGuid().ToString("N"));
                var archivePath = Path.Combine(temporaryRoot, spec.TarXz ? "ffmpeg.tar.xz" : "ffmpeg.zip");
                var extractionPath = Path.Combine(temporaryRoot, "extracted");
                Directory.CreateDirectory(extractionPath);
                Download(spec.Url, archivePath);
                if (spec.TarXz) ExtractTarXz(archivePath, extractionPath);
                else ExtractZip(archivePath, extractionPath);

                var source = FindFile(extractionPath, spec.ExecutableName);
                if (source == null) throw new InvalidDataException("The FFmpeg archive did not contain " + spec.ExecutableName + ".");

                Directory.CreateDirectory(destinationDirectory);
                temporaryDestination = destination + ".download-" + Guid.NewGuid().ToString("N");
                File.Copy(source, temporaryDestination, false);
                EnsureUnixExecutable(temporaryDestination, spec);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporaryDestination, destination);
                temporaryDestination = null;
                InstallMetadata(extractionPath, destinationDirectory, spec);
                error = null;
                Interlocked.Exchange(ref state, Ready);
                entry.Logger.Log("FFmpeg installed for " + spec.Name + ".");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Interlocked.Exchange(ref state, Failed);
                entry.Logger.Log("FFmpeg auto-install failed: " + ex.Message);
            }
            finally
            {
                if (temporaryDestination != null) TryDeleteFile(temporaryDestination);
                if (temporaryRoot != null) TryDeleteDirectory(temporaryRoot);
            }
        }

        internal static string GetBundledExecutable(string modPath)
        {
            try
            {
                var spec = GetPlatformSpec();
                return Path.Combine(modPath, "FFmpeg", spec.Name, spec.ExecutableName);
            }
            catch { return null; }
        }

        private static PlatformSpec GetPlatformSpec()
        {
            var platform = Application.platform;
            if (platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor)
                return new PlatformSpec("windows-x64", "ffmpeg.exe", "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", false);
            if (platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor)
            {
                if (IsAppleSilicon())
                    return new PlatformSpec("macos-arm64", "ffmpeg", "https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/ffmpeg.zip", false);
                return new PlatformSpec("macos-x64", "ffmpeg", "https://evermeet.cx/ffmpeg/getrelease/zip", false);
            }
            if (platform == RuntimePlatform.LinuxPlayer || platform == RuntimePlatform.LinuxEditor)
                return new PlatformSpec("linux-x64", "ffmpeg", "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz", true);
            throw new PlatformNotSupportedException("This platform is not supported by the automatic FFmpeg installer.");
        }

        private static bool IsAppleSilicon()
        {
            try
            {
                var output = RunProcess("/usr/bin/uname", "-m", 15000);
                return string.Equals(output.Trim(), "arm64", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(output.Trim(), "aarch64", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Download(string url, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "ADOFAIRenderer-FFmpegInstaller/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (response.ContentLength > MaximumDownloadBytes)
                    throw new InvalidDataException("The FFmpeg download is larger than the safety limit.");
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumDownloadBytes)
                        throw new InvalidDataException("The FFmpeg download is larger than the safety limit.");
                    output.Write(buffer, 0, read);
                }
            }
        }

        private static void ExtractZip(string archivePath, string destination)
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(destination, entryPath));
                    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The FFmpeg archive contains an unsafe path.");
                }
            }
            ZipFile.ExtractToDirectory(archivePath, destination);
        }

        private static void ExtractTarXz(string archivePath, string destination)
        {
            var listing = RunProcess("tar", "-tJf " + QuoteArgument(archivePath), 60000);
            using (var reader = new StringReader(listing))
            {
                string line;
                while ((line = reader.ReadLine()) != null) ValidateArchivePath(line);
            }
            RunProcess("tar", "-xJf " + QuoteArgument(archivePath) + " -C " + QuoteArgument(destination), 60000);
        }

        private static void ValidateArchivePath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(":"))
                throw new InvalidDataException("The FFmpeg archive contains an unsafe path.");
            foreach (var part in normalized.Split('/'))
            {
                if (part == "..") throw new InvalidDataException("The FFmpeg archive contains an unsafe path.");
            }
        }

        private static string FindFile(string root, string name)
        {
            foreach (var path in Directory.GetFiles(root, name, SearchOption.AllDirectories)) return path;
            return null;
        }

        private static void InstallMetadata(string extractionPath, string destinationDirectory, PlatformSpec spec)
        {
            var license = FindNamedFile(extractionPath, new[] { "FFmpeg-LICENSE.txt", "GPLv3.txt", "LICENSE", "LICENSE.txt", "COPYING" });
            if (license != null) File.Copy(license, Path.Combine(destinationDirectory, "FFmpeg-LICENSE.txt"), true);
            else
            {
                var licenseText = "FFmpeg is distributed under the GNU GPLv3 license." + Environment.NewLine
                    + "Full license: https://www.gnu.org/licenses/gpl-3.0.txt" + Environment.NewLine
                    + "Source archive: " + spec.Url + Environment.NewLine;
                File.WriteAllText(Path.Combine(destinationDirectory, "FFmpeg-LICENSE.txt"), licenseText, new UTF8Encoding(false));
            }

            var readme = FindNamedFile(extractionPath, new[] { "FFmpeg-README.txt", "README.txt", "readme.txt" });
            if (readme != null) File.Copy(readme, Path.Combine(destinationDirectory, "FFmpeg-README.txt"), true);
            else
            {
                var text = "FFmpeg platform package: " + spec.Name + Environment.NewLine
                    + "Source archive: " + spec.Url + Environment.NewLine
                    + "The FFmpeg binary is a separate GPLv3 component." + Environment.NewLine;
                File.WriteAllText(Path.Combine(destinationDirectory, "FFmpeg-README.txt"), text, new UTF8Encoding(false));
            }
        }

        private static string FindNamedFile(string root, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                foreach (var path in Directory.GetFiles(root, name, SearchOption.AllDirectories)) return path;
            }
            return null;
        }

        private static void EnsureUnixExecutable(string path, PlatformSpec spec)
        {
            if (spec.Name == "windows-x64") return;
            RunProcess("/bin/chmod", "+x " + QuoteArgument(path), 15000);
        }

        private static string RunProcess(string fileName, string arguments, int timeoutMilliseconds)
        {
            using (var process = Process.Start(new ProcessStartInfo {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                if (process == null) throw new InvalidOperationException("Could not start " + fileName + ".");
                var output = process.StandardOutput.ReadToEnd();
                var errorOutput = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException(fileName + " timed out.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(fileName + " failed: " + errorOutput.Trim());
                return output;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
