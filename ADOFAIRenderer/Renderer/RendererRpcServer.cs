using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using ADOFAIRenderer;

namespace ADOFAIRenderer.Renderer
{
    internal sealed class RpcRenderRequest
    {
        public RpcRenderJob Job;
    }

    internal sealed class RpcRenderOptions
    {
        public RendererPreset? Preset;
        public int? Width;
        public int? Height;
        public int? Fps;
        public int? BitrateMbps;
        public float? EndDelaySeconds;

        public bool HasValues
        {
            get { return Preset.HasValue || Width.HasValue || Height.HasValue || Fps.HasValue
                || BitrateMbps.HasValue || EndDelaySeconds.HasValue; }
        }

        public object Snapshot()
        {
            return new
            {
                preset = Preset.HasValue ? Preset.Value.ToString() : null,
                width = Width,
                height = Height,
                fps = Fps,
                bitrateMbps = BitrateMbps,
                endDelaySeconds = EndDelaySeconds
            };
        }
    }

    internal sealed class RpcCancelRequest
    {
        public string JobId;
    }

    internal enum RpcJobState
    {
        Queued,
        Loading,
        Preparing,
        Rendering,
        Finishing,
        Completed,
        Failed,
        Cancelled
    }

    internal sealed class RpcRenderJob
    {
        private readonly object sync = new object();
        private RpcJobState state = RpcJobState.Queued;
        private string error;
        private string outputPath;
        private long totalFrames;
        private long capturedFrames;
        private DateTime updatedUtc = DateTime.UtcNow;

        public RpcRenderJob(string id, string levelPath, bool captureAudio, RpcRenderOptions options)
        {
            Id = id;
            LevelPath = levelPath;
            CaptureAudio = captureAudio;
            Options = options;
        }

        public string Id { get; }
        public string LevelPath { get; }
        public bool CaptureAudio { get; }
        public RpcRenderOptions Options { get; }

        public void SetState(RpcJobState value, string message = null)
        {
            lock (sync)
            {
                state = value;
                if (!string.IsNullOrEmpty(message)) error = message;
                updatedUtc = DateTime.UtcNow;
            }
        }

        public void SetProgress(long captured, long total, string output)
        {
            lock (sync)
            {
                capturedFrames = captured;
                totalFrames = total;
                if (!string.IsNullOrEmpty(output)) outputPath = output;
                updatedUtc = DateTime.UtcNow;
            }
        }

        public void Fail(string message) { SetState(RpcJobState.Failed, message); }
        public void Cancel() { SetState(RpcJobState.Cancelled); }

        public RpcJobState State
        {
            get
            {
                lock (sync) return state;
            }
        }

        public bool IsTerminal
        {
            get
            {
                var value = State;
                return value == RpcJobState.Completed || value == RpcJobState.Failed || value == RpcJobState.Cancelled;
            }
        }

        public string OutputPath
        {
            get { lock (sync) return outputPath; }
        }

        public object Snapshot()
        {
            lock (sync)
            {
                return new
                {
                    id = Id,
                    state = state.ToString().ToLowerInvariant(),
                    levelPath = LevelPath,
                    settings = Options?.Snapshot(),
                    outputPath,
                    totalFrames,
                    capturedFrames,
                    progress = totalFrames > 0 ? Math.Min(1.0, capturedFrames / (double)totalFrames) : 0.0,
                    error,
                    updatedUtc = updatedUtc.ToString("o")
                };
            }
        }
    }

    internal sealed class RendererRpcServer : IDisposable
    {
        private volatile RendererController controller;
        private readonly int port;
        private readonly HttpListener listener = new HttpListener();
        private readonly ConcurrentDictionary<string, RpcRenderJob> jobs =
            new ConcurrentDictionary<string, RpcRenderJob>(StringComparer.OrdinalIgnoreCase);
        private Thread thread;
        private volatile bool stopping;

        public RendererRpcServer(RendererController controller, int port)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            this.port = port;
        }

        public void Start()
        {
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.IgnoreWriteExceptions = true;
            listener.Start();
            thread = new Thread(ListenLoop) { IsBackground = true, Name = "ADOFAI Renderer RPC" };
            thread.Start();
            Main.Entry.Logger.Log("Renderer RPC listening on http://127.0.0.1:" + port + "/");
        }

        public void Rebind(RendererController replacement)
        {
            if (replacement == null) return;
            controller = replacement;
            foreach (var job in jobs.Values)
            {
                if (job.State != RpcJobState.Queued && job.State != RpcJobState.Loading) continue;
                job.SetState(RpcJobState.Queued);
                replacement.EnqueueRpcRender(new RpcRenderRequest { Job = job });
            }
        }

        private void ListenLoop()
        {
            while (!stopping)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!stopping) Main.Entry.Logger.Error("Renderer RPC listener: " + ex);
                    break;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,DELETE,OPTIONS";
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                var path = context.Request.Url.AbsolutePath.Trim('/');
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1 && parts[0].Equals("health", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 200, new { ok = true, renderer = controller.State.ToString().ToLowerInvariant() });
                    return;
                }
                if (parts.Length == 1 && parts[0].Equals("jobs", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod == "GET")
                {
                    WriteJson(context, 200, jobs.Values.Select(j => j.Snapshot()).ToArray());
                    return;
                }
                if (parts.Length == 1 && parts[0].Equals("render", StringComparison.OrdinalIgnoreCase)
                    && context.Request.HttpMethod == "POST")
                {
                    CreateJob(context);
                    return;
                }
                if (parts.Length >= 2 && parts[0].Equals("render", StringComparison.OrdinalIgnoreCase))
                {
                    var job = jobs.TryGetValue(parts[1], out var found) ? found : null;
                    if (job == null) { WriteJson(context, 404, new { error = "Unknown render job." }); return; }
                    if (parts.Length == 2 && context.Request.HttpMethod == "GET")
                    {
                        WriteJson(context, 200, job.Snapshot());
                        return;
                    }
                    if (parts.Length == 3 && parts[2].Equals("download", StringComparison.OrdinalIgnoreCase)
                        && context.Request.HttpMethod == "GET")
                    {
                        Download(context, job);
                        return;
                    }
                    if (parts.Length == 3 && parts[2].Equals("cancel", StringComparison.OrdinalIgnoreCase)
                        && (context.Request.HttpMethod == "POST" || context.Request.HttpMethod == "DELETE"))
                    {
                        if (job.IsTerminal) { WriteJson(context, 409, new { error = "Render job has already finished." }); return; }
                        controller.EnqueueRpcCancel(new RpcCancelRequest { JobId = job.Id });
                        WriteJson(context, 202, new { id = job.Id, state = "cancelling" });
                        return;
                    }
                }
                WriteJson(context, 404, new { error = "Unknown renderer RPC endpoint." });
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("Renderer RPC request: " + ex);
                try { WriteJson(context, 500, new { error = ex.Message }); } catch { }
            }
        }

        private void CreateJob(HttpListenerContext context)
        {
            if (controller.Busy)
            {
                WriteJson(context, 409, new { error = "A render is already in progress." });
                return;
            }
            if (context.Request.ContentLength64 < 0 || context.Request.ContentLength64 > 1024 * 1024)
            {
                WriteJson(context, 413, new { error = "Request body is too large." });
                return;
            }
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            var payload = JsonConvert.DeserializeObject<RpcRenderPayload>(body ?? "{}");
            var path = payload?.LevelPath ?? payload?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                WriteJson(context, 400, new { error = "JSON field 'levelPath' is required." });
                return;
            }
            try { path = Path.GetFullPath(path); }
            catch (Exception ex) { WriteJson(context, 400, new { error = "Invalid levelPath: " + ex.Message }); return; }
            if (!File.Exists(path))
            {
                WriteJson(context, 400, new { error = "Level file does not exist: " + path });
                return;
            }
            var options = ParseOptions(payload, out var optionsError);
            if (optionsError != null)
            {
                WriteJson(context, 400, new { error = optionsError });
                return;
            }
            var id = Guid.NewGuid().ToString("N");
            var job = new RpcRenderJob(id, path,
                payload.CaptureAudio ?? payload.Audio ?? (Main.Settings == null || Main.Settings.CaptureAudio), options);
            jobs[id] = job;
            controller.EnqueueRpcRender(new RpcRenderRequest { Job = job });
            WriteJson(context, 202, new
            {
                id,
                state = "queued",
                statusUrl = "/render/" + id,
                downloadUrl = "/render/" + id + "/download"
            });
        }

        private static RpcRenderOptions ParseOptions(RpcRenderPayload payload, out string error)
        {
            error = null;
            if (payload == null) return null;
            RendererPreset? preset = null;
            if (!string.IsNullOrWhiteSpace(payload.Preset))
            {
                if (!TryParsePreset(payload.Preset, out var parsed))
                {
                    error = "Unknown preset. Use Custom, Preview, FullHD, QHD, or UHD4K.";
                    return null;
                }
                preset = parsed;
            }
            if (payload.Width.HasValue && (payload.Width.Value < 320 || payload.Width.Value > 3840))
            {
                error = InvalidOption("width", "320..3840");
                return null;
            }
            if (payload.Height.HasValue && (payload.Height.Value < 180 || payload.Height.Value > 2160))
            {
                error = InvalidOption("height", "180..2160");
                return null;
            }
            var fps = payload.Fps ?? payload.TargetFps;
            if (payload.Fps.HasValue && payload.TargetFps.HasValue && payload.Fps.Value != payload.TargetFps.Value)
            {
                error = "Use either 'fps' or 'targetFps'; both values must match.";
                return null;
            }
            if (fps.HasValue && (fps.Value < 15 || fps.Value > 240))
            {
                error = InvalidOption("fps", "15..240");
                return null;
            }
            var bitrate = payload.BitrateMbps ?? payload.Bitrate;
            if (payload.BitrateMbps.HasValue && payload.Bitrate.HasValue && payload.BitrateMbps.Value != payload.Bitrate.Value)
            {
                error = "Use either 'bitrateMbps' or 'bitrate'; both values must match.";
                return null;
            }
            if (bitrate.HasValue && (bitrate.Value < 1 || bitrate.Value > 200))
            {
                error = InvalidOption("bitrateMbps", "1..200");
                return null;
            }
            if (payload.EndDelaySeconds.HasValue && (float.IsNaN(payload.EndDelaySeconds.Value)
                || float.IsInfinity(payload.EndDelaySeconds.Value) || payload.EndDelaySeconds.Value < 0f
                || payload.EndDelaySeconds.Value > 30f))
            {
                error = InvalidOption("endDelaySeconds", "0..30");
                return null;
            }
            var options = new RpcRenderOptions {
                Preset = preset,
                Width = payload.Width,
                Height = payload.Height,
                Fps = fps,
                BitrateMbps = bitrate,
                EndDelaySeconds = payload.EndDelaySeconds
            };
            return options.HasValues ? options : null;
        }

        private static string InvalidOption(string name, string range)
        {
            return "Option '" + name + "' must be in range " + range + ".";
        }

        private static bool TryParsePreset(string value, out RendererPreset preset)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "custom": preset = RendererPreset.Custom; return true;
                case "preview": preset = RendererPreset.Preview; return true;
                case "fullhd": case "1080p": preset = RendererPreset.FullHD; return true;
                case "qhd": case "1440p": preset = RendererPreset.QHD; return true;
                case "uhd4k": case "4k": case "2160p": preset = RendererPreset.UHD4K; return true;
                default: preset = RendererPreset.Custom; return false;
            }
        }

        private static void Download(HttpListenerContext context, RpcRenderJob job)
        {
            var path = job.OutputPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                WriteJson(context, job.IsTerminal ? 409 : 404,
                    new { error = job.IsTerminal ? "Render output is unavailable." : "Render is not complete." });
                return;
            }
            var info = new FileInfo(path);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "video/mp4";
            context.Response.ContentLength64 = info.Length;
            context.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + Uri.EscapeDataString(info.Name) + "\"");
            using (var file = File.OpenRead(path)) file.CopyTo(context.Response.OutputStream);
            context.Response.Close();
        }

        private static void WriteJson(HttpListenerContext context, int status, object value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            using (var stream = context.Response.OutputStream) stream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        public void Dispose()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            if (thread != null && thread.IsAlive && !thread.Join(500))
                Main.Entry.Logger.Log("Renderer RPC listener thread did not stop immediately.");
            thread = null;
        }

        private sealed class RpcRenderPayload
        {
            [JsonProperty("levelPath")] public string LevelPath { get; set; }
            [JsonProperty("path")] public string Path { get; set; }
            [JsonProperty("preset")] public string Preset { get; set; }
            [JsonProperty("width")] public int? Width { get; set; }
            [JsonProperty("height")] public int? Height { get; set; }
            [JsonProperty("fps")] public int? Fps { get; set; }
            [JsonProperty("targetFps")] public int? TargetFps { get; set; }
            [JsonProperty("bitrateMbps")] public int? BitrateMbps { get; set; }
            [JsonProperty("bitrate")] public int? Bitrate { get; set; }
            [JsonProperty("endDelaySeconds")] public float? EndDelaySeconds { get; set; }
            [JsonProperty("captureAudio")] public bool? CaptureAudio { get; set; }
            [JsonProperty("audio")] public bool? Audio { get; set; }
        }
    }
}
