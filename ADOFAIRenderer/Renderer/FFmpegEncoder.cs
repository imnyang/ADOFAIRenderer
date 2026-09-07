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
        private long written;

        // Kept for the standalone encoder tests and for callers that use the
        // original API. RendererController uses the configurable overload.
        public FFmpegEncoder(string executable, string output)
            : this(executable, output, 1920, 1080, 60, 18, "veryfast", true, true) { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps, string preset)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, true) { }

        public FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool fastStart)
            : this(executable, output, width, height, fps, bitrateMbps, preset, false, fastStart) { }

        private FFmpegEncoder(string executable, string output, int width, int height, int fps, int bitrateMbps,
            string preset, bool legacyCrf, bool fastStart)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("FFmpeg executable not found", executable);
            if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0 || fps <= 0 || bitrateMbps <= 0)
                throw new ArgumentOutOfRangeException();
            if (string.IsNullOrEmpty(preset)) preset = "fast";
            var frameByteCount = checked(width * height * 4);
            // Readback and encoding share this pool. Four buffers leave almost
            // no overlap once two or three GPU requests are in flight, so use
            // the available 128 MiB pipeline budget without letting 4K buffers
            // grow memory usage unexpectedly.
            var bufferCount = (int)Math.Max(4L, Math.Min(8L, (128L * 1024 * 1024) / frameByteCount));
            free = new BlockingCollection<Frame>(bufferCount);
            work = new BlockingCollection<Frame>(bufferCount);
            var bufferSizeMbps = Math.Max(1, bitrateMbps * 2);
            var rateControl = legacyCrf
                ? "-crf 18"
                : "-b:v " + bitrateMbps + "M -maxrate " + bitrateMbps + "M -bufsize " + bufferSizeMbps + "M";
            process = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable,
                Arguments = "-hide_banner -loglevel warning -nostdin -n -f rawvideo -pixel_format rgba -video_size "
                    + width + "x" + height + " -framerate " + fps + " -i pipe:0 -an -vf vflip -c:v libx264 -preset "
                    + preset + " " + rateControl + " -pix_fmt yuv420p"
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
                    stream.Write(frame.Bytes, 0, frame.Bytes.Length);
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
        public void Submit(Frame frame) { Check(); work.Add(frame); }
        public void Finish(long expectedFrames)
        {
            work.CompleteAdding();
            if (!writer.Join(30000)) { AbortProcess(); throw new TimeoutException("FFmpeg input did not finish."); }
            if (failure != null) throw new IOException("FFmpeg input failed: " + ErrorText(), failure);
            if (!process.WaitForExit(30000)) { AbortProcess(); throw new TimeoutException("FFmpeg did not finalize the MP4."); }
            process.WaitForExit(); // Drain asynchronous stderr events after process exit.
            if (process.ExitCode != 0) throw new IOException("FFmpeg exited (" + process.ExitCode + "): " + ErrorText());
            if (WrittenFrames != expectedFrames) throw new IOException("Encoded frame count does not match the render clock.");
        }
        private void AbortProcess() { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
        public static void MuxAudio(string executable, string video, string audio, string output)
        {
            using (var mux = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = "-hide_banner -loglevel error -nostdin -n -i \"" + video + "\" -i \"" + audio
                    + "\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 320k -movflags +faststart \"" + output + "\""
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
