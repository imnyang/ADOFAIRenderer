using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ADOFAIRenderer.Renderer
{
    internal sealed class FFmpegEncoder : IDisposable
    {
        internal sealed class Frame
        {
            public readonly byte[] Bytes;
            public long Index;

            public Frame(int byteCount) { Bytes = new byte[byteCount]; }
        }
        private readonly BlockingCollection<Frame> free;
        private readonly BlockingCollection<Frame> work;
        private readonly StringBuilder stderr = new StringBuilder();
        private readonly Process process;
        private readonly Thread writer;
        private volatile Exception failure;
        private bool disposed;
        public long WrittenFrames => Interlocked.Read(ref written);
        public double WriteSeconds => Interlocked.Read(ref writeTicks) / (double)Stopwatch.Frequency;
        public int QueueDepth => work.Count;
        public int PeakQueueDepth => Volatile.Read(ref peakQueueDepth);
        public int BufferCapacity => work.BoundedCapacity;
        private long written;
        private long writeTicks;
        private int peakQueueDepth;

        // Kept for the standalone encoder tests and for callers that use the
        // original API. RendererController uses the configurable overload.
        public FFmpegEncoder(string executable, string output)
            : this(executable, output, 1920, 1080, 60, 18, "veryfast", true, true, "libx264", "yuv420p") { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps, string preset)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, true, "libx264", "yuv420p") { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool fastStart)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, fastStart, "libx264", "yuv420p") { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool fastStart, string codec)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, fastStart, codec, "yuv420p") { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool fastStart, string codec, string pixelFormat)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, fastStart, codec, pixelFormat) { }

        private FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool legacyCrf, bool fastStart, string codec, string pixelFormat)
        {
            if (string.IsNullOrWhiteSpace(executable)) throw new FileNotFoundException("FFmpeg executable was not configured.");
            if (LooksLikeFilePath(executable) && !File.Exists(executable))
                throw new FileNotFoundException("FFmpeg executable not found", executable);
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || fps <= 0 || bitrateMbps <= 0)
                throw new ArgumentOutOfRangeException();
            if (string.IsNullOrEmpty(preset)) preset = "fast";
            if (string.IsNullOrEmpty(codec)) codec = "libx264";
            if (!string.Equals(pixelFormat, "yuv420p10le", StringComparison.OrdinalIgnoreCase)) pixelFormat = "yuv420p";
            bool isHardwareEncoder = codec.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
                || codec.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
                || codec.EndsWith("_amf", StringComparison.OrdinalIgnoreCase);
            var frameByteCount = checked(width * height * 4);
            // Readback and encoding share this pool. Keep enough frames in flight
            // to hide GPU readback and x264 latency instead of making the Unity
            // thread wait after every few frames. Rendering is an offline job, so
            // throughput is more important than minimizing the temporary working set.
            var bufferCount = (int)Math.Max(4L, Math.Min(16L, (256L * 1024 * 1024) / frameByteCount));
            free = new BlockingCollection<Frame>(bufferCount);
            work = new BlockingCollection<Frame>(bufferCount);
            var bufferSizeMbps = Math.Max(1, bitrateMbps * 2);
            var rateControl = legacyCrf && string.Equals(codec, "libx264", StringComparison.OrdinalIgnoreCase)
                ? "-crf 18"
                : "-b:v " + bitrateMbps + "M -maxrate " + bitrateMbps + "M -bufsize " + bufferSizeMbps + "M";
            if (string.Equals(codec, "libsvtav1", StringComparison.OrdinalIgnoreCase))
            {
                // SVT-AV1 does not accept x264-style CBR flags. Use a target
                // bitrate for audio renders and capped CRF for video-only
                // renders so the existing quality/bitrate intent is retained.
                rateControl = legacyCrf
                    ? "-crf 30 -maxrate " + bitrateMbps + "M"
                    : "-b:v " + bitrateMbps + "M";
            }
            var encoderOptions = BuildEncoderOptions(codec, preset, rateControl, isHardwareEncoder, pixelFormat);
            process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable,
                Arguments = "-hide_banner -loglevel warning -nostdin -n -f rawvideo -pixel_format rgba -video_size "
                    + width + "x" + height + " -framerate " + fps + " -i pipe:0 -an -vf vflip "
                    + encoderOptions + " -pix_fmt " + pixelFormat
                    + (fastStart ? " -movflags +faststart" : "") + " \"" + output + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            }};
            process.ErrorDataReceived += (sender, args) => {
                if (args.Data == null) return;
                lock (stderr) {
                    stderr.AppendLine(args.Data);
                    if (stderr.Length > 16384) stderr.Remove(0, stderr.Length - 16384);
                }
            };
            try
            {
                process.Start();
                try { process.PriorityClass = ProcessPriorityClass.High; }
                catch { /* Restricted environments may not allow priority changes. */ }
                process.BeginErrorReadLine();
                for (int i = 0; i < bufferCount; i++) free.Add(new Frame(frameByteCount));
                writer = new Thread(WriteFrames) { IsBackground = true, Name = "ADOFAI FFmpeg" };
                writer.Start();
            }
            catch { AbortProcess(); process.Dispose(); throw; }
        }

        private void WriteFrames()
        {
            try
            {
                var stream = process.StandardInput.BaseStream;
                foreach (var frame in work.GetConsumingEnumerable())
                {
                    if (frame.Index != WrittenFrames) throw new InvalidDataException("Frame ordering violation.");
                    var writeStart = Stopwatch.GetTimestamp();
                    stream.Write(frame.Bytes, 0, frame.Bytes.Length);
                    Interlocked.Add(ref writeTicks, Stopwatch.GetTimestamp() - writeStart);
                    Interlocked.Increment(ref written);
                    free.Add(frame);
                }
                stream.Flush();
                stream.Close();
            }
            catch (Exception ex) { failure = ex; }
        }

        public void Check()
        {
            if (failure != null) throw new IOException("FFmpeg input failed: " + ErrorText(), failure);
            if (process.HasExited) throw new IOException("FFmpeg exited (" + process.ExitCode + "): " + ErrorText());
        }
        private string ErrorText() { lock (stderr) return stderr.ToString(); }
        public bool TryRent(out Frame frame) { Check(); return free.TryTake(out frame); }
        public Frame Rent()
        {
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                Check();
                if (free.TryTake(out var frame, 100)) return frame;
                if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("FFmpeg stopped consuming frames.");
            }
        }

        private static string NvencPreset(string preset)
        {
            if (string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase)) return "p1";
            if (string.Equals(preset, "fast", StringComparison.OrdinalIgnoreCase)) return "p6";
            return "p4";
        }

        private static string BuildEncoderOptions(string codec, string preset, string rateControl,
            bool isHardwareEncoder, string pixelFormat)
        {
            var normalized = (codec ?? string.Empty).Trim().ToLowerInvariant();
            var tenBitHevc = string.Equals(pixelFormat, "yuv420p10le", StringComparison.Ordinal)
                && (normalized == "libx265" || normalized == "hevc_nvenc"
                    || normalized == "hevc_qsv" || normalized == "hevc_amf");
            var profile = tenBitHevc ? " -profile:v main10" : string.Empty;
            if (isHardwareEncoder && normalized.EndsWith("_nvenc", StringComparison.Ordinal))
                return "-c:v " + normalized + " -preset " + NvencPreset(preset)
                    + " -tune hq -rc cbr " + rateControl + profile;

            if (isHardwareEncoder && normalized.EndsWith("_qsv", StringComparison.Ordinal))
                return "-c:v " + normalized + " -preset " + QsvPreset(preset)
                    + " " + rateControl + profile;

            if (isHardwareEncoder && normalized.EndsWith("_amf", StringComparison.Ordinal))
                return "-c:v " + normalized + " -quality " + AmfQuality(preset)
                    + " -rc cbr " + rateControl + profile;

            if (normalized == "libvpx-vp9")
            {
                var cpuUsed = string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase) ? 8
                    : string.Equals(preset, "veryfast", StringComparison.OrdinalIgnoreCase) ? 6 : 4;
                return "-c:v libvpx-vp9 -deadline good -cpu-used " + cpuUsed
                    + " -row-mt 1 " + rateControl;
            }

            if (normalized == "libsvtav1")
            {
                var svtPreset = string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase) ? 12
                    : string.Equals(preset, "veryfast", StringComparison.OrdinalIgnoreCase) ? 10 : 8;
                return "-c:v libsvtav1 -preset " + svtPreset + " " + rateControl;
            }

            var encoderPreset = preset;
            return "-c:v " + normalized + " -threads 0 -preset " + encoderPreset
                + (string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase)
                    && (normalized == "libx264" || normalized == "libx265")
                    ? " -tune zerolatency " : " ") + rateControl + profile;
        }

        private static string QsvPreset(string preset)
        {
            if (string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase)) return "veryfast";
            if (string.Equals(preset, "veryfast", StringComparison.OrdinalIgnoreCase)) return "faster";
            return "fast";
        }

        private static string AmfQuality(string preset)
        {
            if (string.Equals(preset, "ultrafast", StringComparison.OrdinalIgnoreCase)) return "speed";
            if (string.Equals(preset, "veryfast", StringComparison.OrdinalIgnoreCase)) return "balanced";
            return "quality";
        }

        internal static bool TryValidateVideo(string executable, int bitrateMbps, string preset, string codec,
            string pixelFormat, string extension, bool legacyCrf, out string error)
        {
            error = null;
            var output = Path.Combine(Path.GetTempPath(), "ADOFAIRenderer-encoder-check-"
                + Guid.NewGuid().ToString("N") + extension);
            try
            {
                using (var encoder = new FFmpegEncoder(executable, output, 128, 128, 30,
                    Math.Max(1, bitrateMbps), preset, legacyCrf, false, codec, pixelFormat))
                {
                    var frame = encoder.Rent();
                    frame.Index = 0;
                    for (int i = 0; i < frame.Bytes.Length; i += 4)
                    {
                        frame.Bytes[i] = 32;
                        frame.Bytes[i + 1] = 96;
                        frame.Bytes[i + 2] = 160;
                        frame.Bytes[i + 3] = 255;
                    }
                    encoder.Submit(frame);
                    encoder.Finish(1);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
            }
        }

        private static bool LooksLikeFilePath(string executable)
        {
            return Path.IsPathRooted(executable)
                || executable.IndexOf(Path.DirectorySeparatorChar) >= 0
                || executable.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
        }
        public void Submit(Frame frame)
        {
            Check();
            work.Add(frame);
            var depth = work.Count;
            while (true)
            {
                var previous = Volatile.Read(ref peakQueueDepth);
                if (depth <= previous || Interlocked.CompareExchange(ref peakQueueDepth, depth, previous) == previous)
                    break;
            }
        }
        public void Finish(long expectedFrames)
        {
            work.CompleteAdding();
            if (!writer.Join(30000)) { AbortProcess(); throw new TimeoutException("FFmpeg input did not finish."); }
            if (failure != null) throw new IOException("FFmpeg input failed: " + ErrorText(), failure);
            if (!process.WaitForExit(30000)) { AbortProcess(); throw new TimeoutException("FFmpeg did not finalize the video."); }
            process.WaitForExit(); // Drain asynchronous stderr events after process exit.
            if (process.ExitCode != 0) throw new IOException("FFmpeg exited (" + process.ExitCode + "): " + ErrorText());
            if (WrittenFrames != expectedFrames) throw new IOException("Encoded frame count does not match the render clock.");
        }
        private void AbortProcess() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        public static void MuxAudio(string executable, string video, string audio, string output)
        {
            var isWebm = string.Equals(Path.GetExtension(output), ".webm", StringComparison.OrdinalIgnoreCase);
            var audioEncoder = isWebm ? "libopus" : "aac";
            var audioBitrate = isWebm ? "160k" : "320k";
            var containerOptions = isWebm ? "-f webm" : "-movflags +faststart";
            using (var mux = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = "-hide_banner -loglevel error -nostdin -n -i \"" + video + "\" -i \"" + audio
                    + "\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a " + audioEncoder + " -b:a " + audioBitrate
                    + " " + containerOptions + " -shortest \"" + output + "\""
            }})
            {
                mux.Start();
                var errors = mux.StandardError.ReadToEndAsync();
                if (!mux.WaitForExit(60000)) { mux.Kill(); mux.WaitForExit(); throw new TimeoutException("Audio/video mux timed out."); }
                if (mux.ExitCode != 0) throw new IOException("Audio/video mux failed: " + errors.GetAwaiter().GetResult());
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            work.CompleteAdding();
            AbortProcess();
            if (writer != null && !writer.Join(5000)) return; // Do not dispose collections still used by a worker.
            process.Dispose(); work.Dispose(); free.Dispose();
        }
    }
}
