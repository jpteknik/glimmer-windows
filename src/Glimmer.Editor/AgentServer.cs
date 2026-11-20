// Ported from App/AgentServer.swift
//
// Network.framework's NWListener becomes a TcpListener on 127.0.0.1 (no URL reservation and no administrator rights are
// needed for that on Windows, unlike HttpListener). The documents the API addresses are the opener's windows
// (AgentRegistry on the Mac). The debug-only routes of the Mac build (journal-idle, journal-stop, fixture, raw) are not
// here: they exist for the Mac's own GUI checks.
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

// MARK: Jobs

/// <summary>Long requests (anything with a paid step, or "async": true) run as jobs: POST answers 202 with the job, GET
/// /api/v1/jobs/{id} reads it, DELETE cancels it. The same shape as Flimmer's and Jämmer's jobs.</summary>
public sealed class AgentJobs
{
    public sealed class Job(string kind, string documentID)
    {
        public string Id { get; } = Guid.NewGuid().ToString().ToUpperInvariant();
        public string Kind { get; } = kind;
        public string DocumentID { get; } = documentID;
        /// <summary>queued, running, done, failed, canceled.</summary>
        public string Status { get; set; } = "queued";
        public double Progress { get; set; }
        public JsonObject? Error { get; set; }
        public JsonObject? Result { get; set; }
        public DateTimeOffset? Finished { get; set; }
        internal CancellationTokenSource Cancellation { get; } = new();

        public JsonObject Json
        {
            get
            {
                var d = new JsonObject { ["id"] = Id, ["kind"] = Kind, ["document"] = DocumentID, ["status"] = Status, ["progress"] = Progress };
                if (Error is not null) d["error"] = Error.DeepClone();
                if (Result is not null) d["result"] = Result.DeepClone();
                return d;
            }
        }
    }

    private readonly Dictionary<string, Job> jobs = [];
    private readonly List<string> order = [];

    public void Add(Job job)
    {
        jobs[job.Id] = job;
        order.Add(job.Id);
        // Keep the 50 most recent finished jobs.
        var finished = order.Where(id => jobs[id].Finished is not null).ToList();
        foreach (var id in finished.Take(Math.Max(0, finished.Count - 50)))
        {
            jobs.Remove(id);
            order.Remove(id);
        }
    }

    public Job? Find(string id) => jobs.TryGetValue(id, out var job) ? job : jobs.Values.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<Job> Running => [.. order.Select(id => jobs[id]).Where(j => j.Finished is null)];
    public IReadOnlyList<Job> All => [.. order.Select(id => jobs[id])];
}

// MARK: Server

/// <summary>The local HTTP server for agents: the guide at /glimmer and the API under /api/v1. It listens on 127.0.0.1
/// only, on port 9001 or the next free one of Glimmer's range.</summary>
public sealed class AgentServer : ObservableObject, IAgentServer, IDisposable
{
    private readonly IReadOnlyList<ushort> candidatePorts;
    private TcpListener? listener;
    private CancellationTokenSource? stopping;
    private ushort? port;
    private string? failure;

    /// <param name="opener">The app's windows: the documents the API can address.</param>
    /// <param name="candidatePorts">Where to listen, first free wins (AgentAPI.CandidatePorts; tests give their own, 0 for
    /// any).</param>
    public AgentServer(DocumentOpener opener, IReadOnlyList<ushort>? candidatePorts = null, string? appVersion = null)
    {
        Opener = opener;
        this.candidatePorts = candidatePorts ?? AgentAPI.CandidatePorts;
        AppVersion = appVersion ?? (Assembly.GetEntryAssembly() ?? typeof(AgentServer).Assembly).GetName().Version?.ToString(3) ?? "?";
        Router = new AgentRouter(this);
    }

    public DocumentOpener Opener { get; }
    public string AppVersion { get; }
    public AgentJobs Jobs { get; } = new();
    internal AgentRouter Router { get; }
    internal IUiScheduler Scheduler => Opener.Environment.Scheduler;

    /// <summary>Opens a pane of Settings for the person (POST /api/v1/settings/open); the app gives it.</summary>
    public Action<string>? OpenSettings { get; init; }

    public ushort? Port { get => port; private set { port = value; Raise(nameof(Port), nameof(GuideURL), nameof(AgentPrompt)); } }
    public string? Failure { get => failure; private set => Set(ref failure, value); }

    public string? GuideURL => Port is { } p ? $"http://127.0.0.1:{p}/glimmer" : null;

    /// <summary>The text for the splash's field: an agent that gets only this can find its way.</summary>
    public string? AgentPrompt => GuideURL is { } url ? $"Learn all about Glimmer and how to interact with it: fetch and follow this guide {url}" : null;

    public void Start()
    {
        if (listener is not null) return;
        foreach (var candidate in candidatePorts)
        {
            var l = new TcpListener(IPAddress.Loopback, candidate);
            // Never share a port with another process that has it (allowLocalEndpointReuse = false on the Mac).
            if (OperatingSystem.IsWindows()) l.ExclusiveAddressUse = true;
            try
            {
                l.Start();
            }
            catch (SocketException)
            {
                continue;
            }
            listener = l;
            stopping = new CancellationTokenSource();
            var bound = (ushort)((IPEndPoint)l.LocalEndpoint).Port;
            Port = bound;
            _ = AcceptLoop(l, bound, stopping.Token);
            return;
        }
        Failure = candidatePorts.Count == 20 && candidatePorts[0] == 9001
            ? "Port 9001 and ports 9101–9119 are all in use."
            : $"Ports {string.Join(", ", candidatePorts)} are all in use.";
    }

    public void Dispose()
    {
        stopping?.Cancel();
        listener?.Stop();
        listener = null;
    }

    private async Task AcceptLoop(TcpListener l, ushort boundPort, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client, boundPort, token), CancellationToken.None);
        }
    }

    private const int ReadLimit = AgentAPI.MaxBody + 64 * 1024;

    private async Task Serve(TcpClient client, ushort boundPort, CancellationToken token)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            long? needed = null;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                int n = await stream.ReadAsync(chunk, idle.Token).ConfigureAwait(false);
                if (n == 0) return;   // closed before a whole request came
                buffer.Write(chunk, 0, n);
                // The headers say how long the body is: parse once it is all here, not after every read.
                needed ??= ExpectedLength(buffer);
                if (needed is { } total && buffer.Length < total && buffer.Length < ReadLimit) continue;
                var parsed = AgentAPI.Parse(buffer.ToArray());
                AgentAPI.Response response;
                switch (parsed)
                {
                    case AgentAPI.ParseResult.Incomplete:
                        if (buffer.Length >= ReadLimit) response = AgentAPI.Response.Error(new AgentAPI.APIError(413, "too_large", "The request is too large."));
                        else continue;
                        break;
                    case AgentAPI.ParseResult.Invalid(var message):
                        bool tooLarge = message.Contains("too large", StringComparison.Ordinal);
                        response = AgentAPI.Response.Error(new AgentAPI.APIError(tooLarge ? 413 : 400, tooLarge ? "too_large" : "bad_request", message));
                        break;
                    case AgentAPI.ParseResult.Parsed(var request):
                        response = AgentAPI.Check(request, boundPort) is { } refusal
                            ? AgentAPI.Response.Error(refusal)
                            : await OnUiThread(request, boundPort).ConfigureAwait(false);
                        break;
                    default:
                        return;
                }
                await stream.WriteAsync(response.Serialized, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception)
        {
            // A connection that broke or went quiet: nothing to answer.
        }
    }

    /// <summary>The whole request's length once its headers are in (headers plus Content-Length), else null.</summary>
    private static long? ExpectedLength(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        int end = bytes.IndexOf("\r\n\r\n"u8);
        if (end < 0) return null;
        var head = Encoding.Latin1.GetString(bytes[..end]);
        foreach (var line in head.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line[(colon + 1)..].Trim(), out var length) && length >= 0)
                return end + 4 + length;
        }
        return end + 4;
    }

    /// <summary>Routes a request on the UI thread, where the documents live.</summary>
    private Task<AgentAPI.Response> OnUiThread(AgentAPI.Request request, ushort boundPort)
    {
        var done = new TaskCompletionSource<AgentAPI.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        Scheduler.Post(() => _ = Route());
        return done.Task;

        async Task Route()
        {
            try { done.SetResult(await Router.Route(request, boundPort)); }
            catch (Exception error) { done.SetResult(AgentAPI.Response.Error(AgentAPI.APIError.InternalError(AgentAPI.Words(error)))); }
        }
    }
}

// MARK: Routes

internal sealed class AgentRouter(AgentServer server)
{
    private static IAgentOperationCatalog Catalog => AgentOperationCatalog.Instance;

    private IReadOnlyList<EditorSession> Sessions => server.Opener.Sessions;
    private EditorSession? Front => server.Opener.FrontSession;

    private EditorSession? Session(string id) => Sessions.FirstOrDefault(s => string.Equals(s.AgentID, id, StringComparison.OrdinalIgnoreCase));

    public async Task<AgentAPI.Response> Route(AgentAPI.Request r, ushort port)
    {
        try
        {
            return await Dispatch(r, port);
        }
        catch (AgentAPI.APIError error)
        {
            return AgentAPI.Response.Error(error);
        }
        catch (Exception error)
        {
            return AgentAPI.Response.Error(AgentAPI.APIError.InternalError(AgentAPI.Words(error)));
        }
    }

    private static AgentAPI.Response Json(JsonObject body, int status = 200) => AgentAPI.Response.FromJson(body, status);

    private async Task<AgentAPI.Response> Dispatch(AgentAPI.Request r, ushort port)
    {
        var s = r.Segments;
        void Only(params string[] methods)
        {
            if (!methods.Contains(r.Method))
                throw new AgentAPI.APIError(405, "method_not_allowed", $"{r.Method} isn't allowed here; use {string.Join(" or ", methods)}.");
        }
        // A query parameter the path doesn't take is refused by name, not passed over.
        if (s.Count > 2 && s[0] == "api" && s[1] == "v1"
            && StepParameters.StrayMessage(r.Query.Keys, StepParameters.QueryParameters([.. s.Skip(2)], r.Method), $"{r.Method} {r.Path}") is { } stray)
            throw AgentAPI.APIError.BadRequest(stray);
        string Joined(int count) => string.Join('/', s.Take(count));
        switch (s.Count)
        {
            case 0:
            case 1 when s[0] == "api":
            case 2 when Joined(2) == "api/v1":
                Only("GET");
                return Json(new JsonObject { ["guide"] = $"http://127.0.0.1:{port}/glimmer", ["api"] = $"http://127.0.0.1:{port}/api/v1" });
            case 1 when s[0] is "glimmer" or "glimmer.md":
                Only("GET");
                return AgentAPI.Response.Text(AgentAPI.Guide(port, server.AppVersion, Catalog, heal: true, psd: true, raw: true));
            case 3 when Joined(3) == "api/v1/status":
                Only("GET");
                return Json(new JsonObject
                {
                    ["app"] = new JsonObject { ["name"] = "Glimmer", ["version"] = server.AppVersion, ["bundleID"] = AppIdentity.OwnBundleID },
                    // The process answering: a caller that started its own copy checks it is talking to that one.
                    ["api"] = AgentAPI.Version, ["port"] = (int)port, ["pid"] = Environment.ProcessId,
                    ["documents"] = new JsonArray([.. Sessions.Select(x => (JsonNode?)Summary(x))]),
                    ["jobs"] = new JsonArray([.. server.Jobs.Running.Select(j => (JsonNode?)j.Json)]),
                    // Which services have a key (never the key): paid steps need the one they use.
                    ["providerKeys"] = ProviderKeysJson(),
                    ["linkJournal"] = JsonValueOf(server.Opener.Journal.Summary),
                });
            case 3 when Joined(3) == "api/v1/operations":
                Only("GET");
                return Json(new JsonObject { ["operations"] = new JsonArray([.. AgentAPI.Operations(Catalog).Select(o => (JsonNode?)o.DeepClone())]) });
            case 3 when Joined(3) == "api/v1/requirements":
                // What may need installing, from the register. Reading it downloads nothing.
                Only("GET");
                return Json(new JsonObject { ["items"] = new JsonArray([.. Requirement.Installable.Select(x => (JsonNode?)RequirementJson(x))]) });
            case 4 when Joined(4) == "api/v1/settings/open":
            {
                // Only opens a pane of Settings for the person; it changes no setting and starts nothing.
                Only("POST");
                var pane = Body(r, "settings.open")["pane"]?.GetValue<string>() ?? "general";
                string[] panes = ["general", "ai", "models", "performance", "tablet"];
                if (!panes.Contains(pane)) throw AgentAPI.APIError.BadRequest($"pane must be one of: {string.Join(", ", panes)}.");
                server.OpenSettings?.Invoke(pane);
                return Json(new JsonObject { ["opened"] = pane });
            }
            case 3 when Joined(3) == "api/v1/documents":
                Only("GET", "POST");
                if (r.Method == "GET") return Json(new JsonObject { ["documents"] = new JsonArray([.. Sessions.Select(x => (JsonNode?)Summary(x))]) });
                return CreateDocument(r);
            case 3 when Joined(3) == "api/v1/jobs":
                Only("GET");
                return Json(new JsonObject { ["jobs"] = new JsonArray([.. server.Jobs.All.Select(j => (JsonNode?)j.Json)]) });
            case 4 when Joined(3) == "api/v1/jobs":
            {
                Only("GET", "DELETE");
                var job = server.Jobs.Find(s[3])
                    ?? throw AgentAPI.APIError.NotFound($"No job with id {s[3]}. Finished jobs are kept for a while; GET /api/v1/jobs lists them.");
                if (r.Method == "DELETE" && job.Finished is null)
                {
                    job.Cancellation.Cancel();
                    Session(job.DocumentID)?.AI.CancelRunningJobs();
                }
                return Json(new JsonObject { ["job"] = job.Json });
            }
            case >= 4 when Joined(3) == "api/v1/documents":
            {
                var session = Session(s[3]) ?? throw AgentAPI.APIError.NotFound($"No open document with id {s[3]}. GET /api/v1/documents lists them.");
                var rest = string.Join('/', s.Skip(4));
                switch (rest)
                {
                    case "":
                        Only("GET");
                        return Json(new JsonObject { ["document"] = Detail(session) });
                    case "image":
                        Only("GET");
                        return await Image(session, r);
                    case "steps":
                        Only("POST");
                        return await Steps(session, r);
                    case "undo" or "redo":
                        Only("POST");
                        _ = Body(r, "undo");
                        return UndoRedo(session, redo: rest == "redo");
                    case "export":
                        Only("POST");
                        return await Export(session, r);
                    case "layers":
                        Only("POST");
                        return AddImage(session, r);
                }
                if (s.Count == 8 && s[4] == "layers" && s[6] == "raw" && s[7] == "info")
                {
                    Only("GET");
                    var key = Uri.UnescapeDataString(s[5]);
                    var layers = session.State.AllLayers;
                    var layer = layers.FirstOrDefault(l => string.Equals(l.Id.ToString(), key, StringComparison.OrdinalIgnoreCase))
                        ?? layers.FirstOrDefault(l => l.Name == key)
                        ?? throw AgentAPI.APIError.NotFound($"No layer “{key}” in this document.");
                    try { return Json(new JsonObject { ["raw"] = AIController.RawInfo(layer) }); }
                    catch (AIController.StepError e) { throw AgentAPI.APIError.Unprocessable(e.Reason ?? "not_raw", e.Message); }
                }
                throw AgentAPI.APIError.NotFound($"No such path: {r.Path}. See /glimmer.");
            }
        }
        throw AgentAPI.APIError.NotFound($"No such path: {r.Path}. See /glimmer.");
    }

    private static JsonNode? JsonValueOf(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string text => text,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        IReadOnlyDictionary<string, object> dictionary => new JsonObject(dictionary.Select(p => KeyValuePair.Create(p.Key, JsonValueOf(p.Value)))),
        IEnumerable<object> list => new JsonArray([.. list.Select(JsonValueOf)]),
        _ => value.ToString(),
    };

    // MARK: Documents

    internal JsonObject Summary(EditorSession session)
    {
        var st = session.State;
        return new JsonObject
        {
            ["id"] = session.AgentID, ["name"] = session.ExplicitName ?? (st.Layers.Length > 0 ? st.Layers[0].Name : "Untitled"),
            ["width"] = st.Width, ["height"] = st.Height, ["layerCount"] = st.AllLayers.Length, ["hasSelection"] = st.Selection is not null,
            ["frontmost"] = ReferenceEquals(Front, session),
            ["startDocument"] = session.IsUntouchedBlank && !session.MadeByAPI,
            ["linkedFile"] = LinkedFile(session),
        };
    }

    internal JsonObject Detail(EditorSession session)
    {
        var d = Summary(session);
        var st = session.State;
        d["layers"] = new JsonArray([.. st.Layers.Reverse().Select(l => (JsonNode?)Describe(l))]);
        d["activeLayer"] = st.ActiveLayer is { } active ? new JsonObject { ["id"] = active.Id.ToString().ToUpperInvariant(), ["name"] = active.Name } : null;
        if (st.Selection?.Bounds(st.Bounds) is { } sel) d["selectionBounds"] = AIController.RectJson(sel);
        // A document made from a Photoshop file says what came through and what did not (in English here; the window says
        // it in the person's language).
        if (session.ImportReport is { } report)
        {
            var r = new JsonObject
            {
                ["source"] = report.Source, ["layersInFile"] = report.LayersInFile, ["layersKept"] = report.LayersKept,
                ["flattened"] = report.Flattened, ["looksTheSame"] = report.LooksTheSame, ["summary"] = report.Lines.FirstOrDefault() ?? "",
                ["notes"] = new JsonArray([.. report.Notes.Select(n => (JsonNode?)new JsonObject
                {
                    ["layer"] = n.Layer, ["kind"] = CamelCase(n.Kind), ["what"] = n.What, ["changesLook"] = n.ChangesLook,
                })]),
            };
            if (report.MeanDifference is { } mean && report.ShareVisiblyDifferent is { } share)
                r["differenceFromFile"] = new JsonObject { ["mean"] = mean, ["shareVisiblyDifferent"] = share };
            d["importReport"] = r;
        }
        return d;
    }

    /// <summary>The tight bounds of a pixel layer's visible content (tile bounds are up to a tile too big on each side).
    /// Kept per layer until its pixels change.</summary>
    private static readonly Dictionary<Guid, (long Signature, PixelRect? Bounds)> BoundsCache = [];
    private static readonly TileBoundsCache TileBounds = new();
    private static readonly Lock BoundsLock = new();

    private static PixelRect? ContentBounds(TileGrid grid, Guid id)
    {
        lock (BoundsLock)
        {
            long signature = grid.ContentSignature;
            if (BoundsCache.TryGetValue(id, out var hit) && hit.Signature == signature) return hit.Bounds;
            // Per tile, remembered by the tile's identity: a step that changed a few tiles reads those few again, not the
            // layer.
            var bounds = grid.ContentBounds(TileBounds);
            if (BoundsCache.Count > 2000) BoundsCache.Clear();
            BoundsCache[id] = (signature, bounds);
            return bounds;
        }
    }

    /// <summary>Where a layer's content is, in document pixels (null for adjustments and plain fills).</summary>
    internal static PixelRect? LayerBounds(Layer layer)
    {
        switch (layer.Content)
        {
            case RasterContent raster:
                return ContentBounds(raster.Tiles, layer.Id);
            case TextContent text:
                return text.Text.Bounds;
            case SmartContent smart:
            {
                var z = smart.Smart.SourceSize;
                var c = new[] { new Vec2(0, 0), new Vec2(z.X, 0), z, new Vec2(0, z.Y) }.Select(smart.Smart.Transform.Apply).ToList();
                double loX = c.Min(p => p.X), loY = c.Min(p => p.Y), hiX = c.Max(p => p.X), hiY = c.Max(p => p.Y);
                return new PixelRect((int)Math.Floor(loX), (int)Math.Floor(loY), (int)Math.Ceiling(hiX - loX), (int)Math.Ceiling(hiY - loY));
            }
            default:
                if (layer.VectorMask?.ControlBounds is not { } r) return null;
                return new PixelRect((int)r.MinX, (int)r.MinY, (int)Math.Ceiling(r.MaxX - r.MinX), (int)Math.Ceiling(r.MaxY - r.MinY));
        }
    }

    /// <summary>LayerBounds cut down to what the layer's mask lets through.</summary>
    internal static PixelRect? VisibleBounds(Layer layer)
    {
        if (LayerBounds(layer) is not { } r) return null;
        if (layer.Mask is not { IsEnabled: true, DefaultValue: 0 } m || layer.VectorMask is not null) return r;
        return new Selection(m.Tiles).Bounds(r) ?? r;
    }

    internal static JsonObject Describe(Layer layer)
    {
        var d = new JsonObject
        {
            ["id"] = layer.Id.ToString().ToUpperInvariant(), ["name"] = layer.Name, ["visible"] = layer.IsVisible,
            ["opacity"] = layer.Opacity, ["blendMode"] = layer.BlendMode.RawValue(), ["hasMask"] = layer.Mask is not null,
        };
        if (VisibleBounds(layer) is { } r) d["bounds"] = AIController.RectJson(r);
        // The words of a layer that was text in an imported file and is pixels now.
        if (layer.FormerText is { } words) d["formerText"] = words;
        // A layer opened from a camera's RAW file: it can be developed again (develop_raw).
        if (AIController.DescribeRaw(layer) is { } raw) d["raw"] = raw;
        switch (layer.Content)
        {
            case RasterContent:
                d["kind"] = "pixels";
                break;
            case AdjustmentContent a:
                d["kind"] = "adjustment";
                d["adjustment"] = a.Adjustment.Kind.RawValue();
                break;
            case TextContent { Text: var t }:
                d["kind"] = "text";
                d["text"] = t.String;
                if (t.Styles is { IsEmpty: false } runs)
                {
                    // In characters (grapheme clusters), like style_text's from and length.
                    d["styles"] = new JsonArray([.. runs.Select(run =>
                    {
                        var c = t.CharacterRange(run.Range);
                        var o = new JsonObject { ["from"] = c.From, ["length"] = c.Length };
                        if (run.FontName is { } f) o["fontName"] = f;
                        if (run.Size is { } size) o["size"] = size;
                        if (run.Color is { } col) o["color"] = col.HexString();
                        return (JsonNode?)o;
                    })]);
                }
                break;
            case GroupContent g:
                d["kind"] = "group";
                d["children"] = new JsonArray([.. g.Group.Children.Reverse().Select(c => (JsonNode?)Describe(c))]);
                break;
            case FillContent:
                d["kind"] = "fill";
                break;
            case SmartContent:
                d["kind"] = "smartObject";
                break;
        }
        return d;
    }

    /// <summary>The request's JSON body. kind names what it is for (StepParameters.BodyParameters): a name the body doesn't
    /// have a place for is refused, not passed over.</summary>
    private static JsonObject Body(AgentAPI.Request r, string kind)
    {
        if (r.Body.Length == 0) return [];
        JsonObject? obj;
        try { obj = JsonNode.Parse(r.Body) as JsonObject; }
        catch (Exception) { obj = null; }
        if (obj is null) throw AgentAPI.APIError.BadRequest("The body isn't a JSON object.");
        if (StepParameters.BodyParameters.TryGetValue(kind, out var allowed)
            && StepParameters.StrayMessage(obj.Select(p => p.Key), allowed, $"The body of {r.Method} {r.Path}") is { } message)
            throw AgentAPI.APIError.BadRequest(message);
        return obj;
    }

    private static string? String(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Bool(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A whole number (Swift's as? Int: 3 and 3.0 are, 3.5 and "3" are not).</summary>
    private static int? Int(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue) return (int)d;
        return null;
    }

    private static double? Number(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private AgentAPI.Response CreateDocument(AgentAPI.Request r)
    {
        var type = r.Headers.GetValueOrDefault("content-type", "").ToLowerInvariant();
        DocumentState state;
        r.Query.TryGetValue("name", out var name);
        if (type.StartsWith("image/", StringComparison.Ordinal) || type == "application/octet-stream")
        {
            state = DocumentFromImage(r.Body, name ?? "Image");
        }
        else
        {
            var obj = Body(r, "documents");
            name = String(obj, "name") ?? name;
            if (String(obj, "image") is { } b64)
            {
                byte[] data;
                try { data = Convert.FromBase64String(new string([.. b64.Where(c => !char.IsWhiteSpace(c))])); }
                catch (FormatException) { throw AgentAPI.APIError.BadRequest("\"image\" isn't valid base64."); }
                state = DocumentFromImage(data, name ?? "Image");
            }
            else if (Int(obj, "width") is { } w && Int(obj, "height") is { } h)
            {
                if (w is < 1 or > 30_000 || h is < 1 or > 30_000) throw AgentAPI.APIError.BadRequest("width and height must be 1…30000 pixels.");
                if (DocumentLimits.Refusal(w, h) is { } tooLarge) throw AgentAPI.APIError.Unprocessable("too_large", tooLarge);
                var background = (String(obj, "background") ?? "white") switch
                {
                    "transparent" => DocumentState.Background.Transparent,
                    "black" => DocumentState.Background.Black,
                    _ => DocumentState.Background.White,
                };
                state = DocumentState.New(w, h, background);
            }
            else
            {
                throw AgentAPI.APIError.BadRequest("Send image bytes (Content-Type: image/…), {\"image\": \"<base64>\"} or {\"width\": …, \"height\": …}.");
            }
        }
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length > 200) name = name[..200];
        }
        // Glimmer's untouched start document becomes this one, instead of leaving an empty window behind (as opening an
        // image does). Documents the API made are never reused.
        if (Sessions.FirstOrDefault(x => x.IsUntouchedBlank && !x.MadeByAPI) is { } blank)
        {
            blank.ReplaceUntouched(state);
            blank.MadeByAPI = true;
            if (!string.IsNullOrEmpty(name)) blank.SetDocumentName(name);
            ShowRequester(r, blank);
            return Json(new JsonObject { ["document"] = Detail(blank) }, 201);
        }
        var session = server.Opener.CreateDocument(state);
        session.MadeByAPI = true;
        if (!string.IsNullOrEmpty(name)) session.SetDocumentName(name);
        ShowRequester(r, session);
        return Json(new JsonObject { ["document"] = Detail(session) }, 201);
    }

    private static DocumentState DocumentFromImage(byte[] data, string name)
    {
        if (data.Length == 0) throw AgentAPI.APIError.BadRequest("The image body is empty.");
        // A Photoshop file comes in with its layers; what did not come through is in the answer (importReport).
        if (DocumentOpener.IsPhotoshopFile(data))
        {
            try { return DocumentOpener.ImportPhotoshopFile(data); }
            catch (Exception error) { throw AgentAPI.APIError.BadRequest(error.Message is { Length: > 0 } m ? m : "Glimmer couldn't read this Photoshop file."); }
        }
        try
        {
            var image = ImageImporter.Image(data);
            return new DocumentState(image.Width, image.Height, layers: [new Layer(name, new RasterContent(ImageImporter.Tiles(image)))]);
        }
        catch (ImageImportError error)
        {
            throw AgentAPI.APIError.BadRequest(ImageRefusal(error));
        }
        catch (Exception)
        {
            throw AgentAPI.APIError.BadRequest(ImageRefusal(ImageImportError.Unreadable()));
        }
    }

    /// <summary>Why an image wasn't opened, for an agent: what was wrong and what to send instead.</summary>
    internal static string ImageRefusal(ImageImportError error) => error.Reason switch
    {
        ImageImportError.Kind.TooLarge or ImageImportError.Kind.FileTooLarge => AgentAPI.Words(error) + " Send a smaller image.",
        ImageImportError.Kind.Damaged => AgentAPI.Words(error) + " Send the whole file.",
        _ => "Glimmer couldn't read this as an image. Send PNG, JPEG, HEIC, TIFF or WebP bytes.",
    };

    // MARK: Images

    private static ExportFormat Format(string? name)
    {
        if (name is null) return ExportFormat.Png;
        var n = name.ToLowerInvariant() == "jpg" ? "jpeg" : name.ToLowerInvariant();
        return ExportFormatExtensions.FormatFromRawValue(n) ?? throw AgentAPI.APIError.BadRequest("format must be one of: png, jpeg, heic, tiff, webp.");
    }

    private async Task<AgentAPI.Response> Render(EditorSession session, ExportOptions options)
    {
        var snapshot = session.State;
        var data = await session.AI.Detached(() =>
        {
            using var flat = ImageExporter.Flatten(snapshot);
            return ImageExporter.Encode(flat, options);
        });
        return new AgentAPI.Response(200, options.Format.MimeType(), data);
    }

    private Task<AgentAPI.Response> Image(EditorSession session, AgentAPI.Request r)
    {
        var options = new ExportOptions(Format(r.Query.GetValueOrDefault("format")), quality: 0.9);
        if (r.Query.TryGetValue("maxSide", out var side))
        {
            if (!int.TryParse(side, out var v) || v < 16) throw AgentAPI.APIError.BadRequest("maxSide must be a whole number ≥ 16.");
            options = options with { MaxPixelSide = v };
        }
        return Render(session, options);
    }

    private Task<AgentAPI.Response> Export(EditorSession session, AgentAPI.Request r)
    {
        var obj = Body(r, "export");
        if (obj.ContainsKey("preset"))
        {
            if (String(obj, "preset") != "podcast-cover") throw AgentAPI.APIError.BadRequest("preset must be \"podcast-cover\".");
            return PodcastCover(session, obj);
        }
        // A Photoshop file with the layers. What is not written as it is comes back in a header, as JSON.
        if (String(obj, "format")?.ToLowerInvariant() == "psd") return PhotoshopFile(session, obj);
        var options = new ExportOptions(Format(String(obj, "format")));
        if (Number(obj, "quality") is { } q)
        {
            if (q is < 0.05 or > 1) throw AgentAPI.APIError.BadRequest("quality must be 0.05…1.");
            options = options with { Quality = q };
        }
        if (Int(obj, "maxSide") is { } side) options = options with { MaxPixelSide = Math.Max(16, side) };
        if (Int(obj, "maxBytes") is { } bytes) options = options with { MaxBytes = Math.Max(1_000, bytes) };
        return Render(session, options);
    }

    private async Task<AgentAPI.Response> PhotoshopFile(EditorSession session, JsonObject obj)
    {
        int depth = Int(obj, "depth") ?? 8;
        if (depth != 8 && depth != 16) throw AgentAPI.APIError.BadRequest("depth must be 8 or 16.");
        bool flattened = Bool(obj, "flatten") ?? false;
        var snapshot = session.State;
        (byte[] Data, PSDExportReport Report) result;
        try
        {
            result = await session.AI.Detached(() => PSDExport.Data(snapshot, depth, flattened));
        }
        catch (PSDFile.Problem problem)
        {
            throw AgentAPI.APIError.Unprocessable("cannot_export", problem.Message);
        }
        var report = new JsonObject
        {
            ["layersWritten"] = result.Report.LayersWritten,
            ["notes"] = new JsonArray([.. result.Report.Notes.Select(n => (JsonNode?)new JsonObject
            {
                ["changesLook"] = n.ChangesLook, ["kind"] = CamelCase(n.Kind), ["layer"] = n.Layer, ["what"] = n.What,
            })]),
        };
        if (result.Report.GammaDifference is { } d)
            report["layersBlendedInGamma"] = new JsonObject { ["mean"] = d.Mean, ["shareVisiblyDifferent"] = d.ShareVisible };
        // Header values are ASCII: anything else is written as \u escapes, which JSON readers take back.
        var header = new StringBuilder();
        foreach (var c in report.ToJsonString()) header.Append(c < 128 ? c.ToString() : $"\\u{(int)c:x4}");
        return new AgentAPI.Response(200, "image/vnd.adobe.photoshop", result.Data,
                                     new Dictionary<string, string> { ["X-Glimmer-Export-Report"] = header.ToString() });
    }

    /// <summary>An enum case as the Swift raw value it stands for (writtenAsPixels).</summary>
    private static string CamelCase(Enum value)
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Square cover art in the sizes podcast directories take: the centre square of the image at exactly 3000 or
    /// 1400 pixels, JPEG or PNG, opaque, under maxBytes. Without a size, 3000 falls back to 1400 when 3000 can't meet the
    /// byte limit.</summary>
    private async Task<AgentAPI.Response> PodcastCover(EditorSession session, JsonObject obj)
    {
        var format = Format(String(obj, "format") ?? "jpeg");
        if (format is not (ExportFormat.Jpeg or ExportFormat.Png)) throw AgentAPI.APIError.BadRequest("A podcast cover is jpeg or png.");
        var size = Int(obj, "size");
        if (size is { } given && given != 1400 && given != 3000) throw AgentAPI.APIError.BadRequest("size must be 3000 or 1400.");
        var options = new ExportOptions(format, quality: 0.92);
        if (Number(obj, "quality") is { } q)
        {
            if (q is < 0.05 or > 1) throw AgentAPI.APIError.BadRequest("quality must be 0.05…1.");
            options = options with { Quality = q };
        }
        if (Int(obj, "maxBytes") is { } bytes)
        {
            if (bytes < 1_000) throw AgentAPI.APIError.BadRequest("maxBytes must be at least 1000.");
            options = options with { MaxBytes = bytes };
        }
        var snapshot = session.State;
        int[] sides = size is { } one ? [one] : [3000, 1400];
        try
        {
            var data = await session.AI.Detached(() =>
            {
                using var flat = ImageExporter.Flatten(snapshot);
                Exception? last = null;
                foreach (var side in sides)
                {
                    try
                    {
                        using var square = ImageExporter.SquareCover(flat, side, new Vec3(1, 1, 1));
                        return ImageExporter.Encode(square, options, keepSize: true);
                    }
                    catch (ExportLimitError e)
                    {
                        last = e;
                    }
                }
                throw last ?? new ExportLimitError(0, options.MaxBytes ?? 0);
            });
            return new AgentAPI.Response(200, format.MimeType(), data);
        }
        catch (ExportLimitError e)
        {
            var hint = format == ExportFormat.Png ? " Try jpeg." : size == 3000 ? " Try size 1400." : "";
            throw AgentAPI.APIError.Unprocessable("over_byte_limit", $"The cover can't get under {e.Limit} bytes: the smallest is {e.Smallest} bytes.{hint}");
        }
    }

    // MARK: Editing

    /// <summary>"Requested by Flimmer" over the canvas, when a sibling app makes the change.</summary>
    private static void ShowRequester(AgentAPI.Request r, EditorSession session)
    {
        if (AgentAPI.Sibling(r) is { } sibling) session.ShowToast(new UiText("Requested by {0}", sibling));
    }

    /// <summary>add_image: the body's image becomes a new layer, one undo step of its own.</summary>
    private AgentAPI.Response AddImage(EditorSession session, AgentAPI.Request r)
    {
        var type = r.Headers.GetValueOrDefault("content-type", "").ToLowerInvariant();
        // An app sends the image's bytes; an assistant, which has none, names an open document.
        EditorSession? source = null;
        if (!(type.StartsWith("image/", StringComparison.Ordinal) || type == "application/octet-stream"))
        {
            if (String(Body(r, "layers"), "fromDocument") is not { Length: > 0 } key)
                throw AgentAPI.APIError.BadRequest("Send the image's bytes with Content-Type: image/…, or JSON {\"fromDocument\": \"<document id>\"}. The options go in the query.");
            var other = Session(key) ?? throw AgentAPI.APIError.Unprocessable("unknown_document", $"No open document with id {key}. GET /api/v1/documents lists them.");
            if (ReferenceEquals(other, session)) throw AgentAPI.APIError.Unprocessable("same_document", "fromDocument is this document. Name another open document.");
            source = other;
        }
        var fitName = r.Query.GetValueOrDefault("fit") ?? "contain";
        var fit = CanvasSteps.FitFromRaw(fitName) ?? throw AgentAPI.APIError.BadRequest("fit must be contain, cover or none.");
        double? QueryNumber(string key)
        {
            if (!r.Query.TryGetValue(key, out var text)) return null;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                || !double.IsFinite(v) || Math.Abs(v) > 1e6)
                throw AgentAPI.APIError.BadRequest($"{key} must be a number.");
            return v;
        }
        var x = QueryNumber("x");
        var y = QueryNumber("y");
        bool smart = (r.Query.GetValueOrDefault("smartObject") ?? "true") switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw AgentAPI.APIError.BadRequest("smartObject must be true or false."),
        };
        DecodedImage image;
        if (source is not null)
        {
            try
            {
                // The other document flattened, read back as an image (lossless).
                using var flat = ImageExporter.FlattenedSRGB(source.State);
                image = ImageImporter.Image(ImageExporter.Encode(flat, new ExportOptions(ExportFormat.Png)));
            }
            catch (Exception error)
            {
                throw AgentAPI.APIError.InternalError(AgentAPI.Words(error));
            }
        }
        else
        {
            if (r.Body.Length == 0) throw AgentAPI.APIError.Unprocessable("bad_image", "The image body is empty.");
            try
            {
                image = ImageImporter.Image(r.Body);
            }
            catch (ImageImportError error) when (error.Reason is ImageImportError.Kind.TooLarge)
            {
                throw AgentAPI.APIError.Unprocessable("too_large", ImageRefusal(error));
            }
            catch (ImageImportError error)
            {
                throw AgentAPI.APIError.Unprocessable("bad_image", ImageRefusal(error));
            }
            catch (Exception)
            {
                throw AgentAPI.APIError.Unprocessable("bad_image", ImageRefusal(ImageImportError.Unreadable()));
            }
        }
        if (image.Width > CanvasSteps.MaxSide || image.Height > CanvasSteps.MaxSide)
            throw AgentAPI.APIError.Unprocessable("too_large", $"The image is {image.Width} × {image.Height}; the most is {CanvasSteps.MaxSide} pixels a side.");
        var given = r.Query.GetValueOrDefault("name")?.Trim();
        if (given is { Length: > 200 }) given = given[..200];
        var name = string.IsNullOrEmpty(given)
            ? source is not null ? source.ExplicitName ?? (source.State.Layers.Length > 0 ? source.State.Layers[0].Name : null) ?? "Image" : session.Environment.LayerName("Image")
            : given;
        Layer layer;
        try
        {
            layer = session.AddImageLayer(image, name, fit, x, y, smart);
        }
        catch (Exception error)
        {
            throw AgentAPI.APIError.InternalError(AgentAPI.Words(error));
        }
        ShowRequester(r, session);
        var info = new JsonObject { ["id"] = layer.Id.ToString().ToUpperInvariant(), ["name"] = layer.Name };
        if (LayerBounds(layer) is { } b) info["bounds"] = AIController.RectJson(b);
        return Json(new JsonObject { ["layer"] = info, ["document"] = Detail(session) }, 201);
    }

    private async Task<AgentAPI.Response> Steps(EditorSession session, AgentAPI.Request r)
    {
        var obj = Body(r, "steps");
        if (obj["steps"] is not JsonArray raw || raw.Count == 0)
            throw AgentAPI.APIError.BadRequest("Send {\"steps\": [{\"op\": \"…\", \"params\": {…}}]}. GET /api/v1/operations lists the ops.");
        bool allowPaid = Bool(obj, "allowPaid") ?? false;
        // Said on every call, never remembered: allowPaid alone does not mean "whatever it costs".
        bool allowUnknownPrice = Bool(obj, PaidPrice.ConsentParameter) ?? false;
        var paidOps = AgentAPI.PaidOps(Catalog);
        var steps = new List<AgentStep>();
        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i] is not JsonObject item || String(item, "op") is not { } op) throw AgentAPI.APIError.BadRequest($"Step {i} needs an \"op\".");
            if (AgentPlanner.Op(op) is null)
                throw new AgentAPI.APIError(404, "not_found", $"Step {i}: unknown op \"{op}\". GET /api/v1/operations lists them.");
            // A step's own names: a setting put beside "op" instead of inside "params" would otherwise vanish.
            if (StepParameters.StrayMessage(item.Select(p => p.Key), StepParameters.BodyParameters["step"], $"Step {i}") is { } message)
                throw AgentAPI.APIError.BadRequest(message + " An operation's settings go inside \"params\". No step of this request was run.");
            ImmutableDictionary<string, AgentValue> parameters;
            if (item["params"] is null) parameters = ImmutableDictionary<string, AgentValue>.Empty;
            else if (AgentValue.FromNode(item["params"]).AsObject is { } given) parameters = given;
            else throw AgentAPI.APIError.BadRequest($"Step {i}: \"params\" must be an object.");
            var decoded = new AgentStep(op, parameters, item["rationale"] is JsonValue rv && rv.TryGetValue<string>(out var rationale) ? rationale : null);
            // The unknown name first: "crop needs width…" would hide that "widht" was the trouble.
            if (StepParameters.StrayIn(decoded) is { } stray) throw AgentAPI.APIError.BadRequest($"Step {i}: {stray.Message} No step of this request was run.");
            if (CanvasSteps.Malformed(decoded) is { } problem) throw AgentAPI.APIError.BadRequest($"Step {i}: {problem}");
            // A step that needs a model not on this computer: refused before anything runs, never downloaded.
            if (Requirement.ModelsFor(op, decoded.String("id")) is { } ids)
            {
                var missing = ids.Where(id => OnDeviceModels.With(id) is { } model && !ModelStore.Shared.IsInstalled(model)).ToList();
                if (missing.Count > 0) throw AgentAPI.APIError.ModelMissing(i, op, missing);
            }
            if (paidOps.Contains(op))
            {
                // The picture service chosen in Glimmer's Settings: said by name, since that is where the picture goes.
                var service = session.AI.Setup.ImageProvider;
                if (!allowPaid)
                    throw new AgentAPI.APIError(402, "paid_not_allowed", $"Step {i} ({op}) calls {service.Name} and costs money. Pass \"allowPaid\": true to allow it. No step of this request was run.");
                if (!session.AI.HasImageKey)
                    throw new AgentAPI.APIError(402, "paid_not_allowed", $"Step {i} ({op}) needs a key for {service.Name}, the picture service chosen in Glimmer, and none is saved in Glimmer › Settings. No step of this request was run.");
                if (PaidPrice.KnownPrice(op, service.Slug) is null && !allowUnknownPrice)
                    throw AgentAPI.APIError.Unprocessable(PaidPrice.Reason, $"Step {i} ({op}) is paid, and Glimmer does not know its price in advance: {service.Name} states none. Pass \"allowUnknownPrice\": true as well to run it without knowing the price. No step of this request was run.");
            }
            steps.Add(decoded);
        }
        ShowRequester(r, session);
        // Paid steps take a while: they always run as a job, and so does any request that asks.
        if (Bool(obj, "async") == true || steps.Any(st => paidOps.Contains(st.Op)))
        {
            var job = new AgentJobs.Job("steps", session.AgentID);
            server.Jobs.Add(job);
            double total = steps.Count;
            _ = RunJob();
            return Json(new JsonObject { ["job"] = job.Json }, 202);

            async Task RunJob()
            {
                job.Status = "running";
                var results = await session.AI.RunAPISteps(steps, allowUnknownPrice, done => job.Progress = done / total, job.Cancellation.Token);
                job.Result = new JsonObject { ["results"] = StepList(results), ["document"] = Detail(session) };
                int failedAt = results.FindIndex(x => x.Status == "failed");
                if (failedAt >= 0)
                {
                    job.Status = "failed";
                    job.Error = StepFailure(results, failedAt);
                }
                else if (results.Any(x => x.Status == "canceled"))
                {
                    job.Status = "canceled";
                }
                else
                {
                    job.Status = "done";
                    job.Progress = 1;
                }
                job.Finished = DateTimeOffset.Now;
            }
        }
        var list = await session.AI.RunAPISteps(steps);
        int failed = list.FindIndex(x => x.Status == "failed");
        if (failed >= 0)
        {
            var error = StepFailure(list, failed);
            // Installed but not loadable: 503 model_unavailable, with the step and the entry.
            if (list[failed].Reason == "model_unavailable")
            {
                error["code"] = "model_unavailable";
                return Json(new JsonObject { ["error"] = error, ["results"] = StepList(list), ["document"] = Detail(session) }, 503);
            }
            return Json(new JsonObject { ["error"] = error, ["results"] = StepList(list), ["document"] = Detail(session) }, 422);
        }
        return Json(new JsonObject { ["results"] = StepList(list), ["document"] = Detail(session) });
    }

    /// <summary>One item of GET /api/v1/requirements: the register's entry, and whether it is on this computer.</summary>
    internal static JsonObject RequirementJson(Requirement r)
    {
        if (OnDeviceModels.With(r.Id) is { } model)
        {
            bool installed = ModelStore.Shared.IsInstalled(model);
            var verification = ModelStore.Shared.Verification(model);
            // The version is known only from a receipt; an unverified install's is not.
            return r.ToJson(installed, verification == ModelVerification.Verified ? r.Source.Commit : null, verification.Json());
        }
        // A tool the person installs: Glimmer doesn't look for it, so it doesn't say.
        return r.ToJson(null, null);
    }

    /// <summary>Present or missing per offered service that needs a key.</summary>
    private JsonObject ProviderKeysJson()
    {
        var front = Front ?? Sessions.FirstOrDefault();
        var environment = server.Opener.Environment;
        var status = environment.Keys.Status();
        status["openrouter"] = new AISetup(environment).ApiKey() is not null;
        return new JsonObject(status.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value)));
    }

    /// <summary>What waits to be saved back, and how the document came to be open: recovered after a stop (a crash, a
    /// kill), kept when the person left it knowingly. Both go when the edits are saved back.</summary>
    private static void AddWaiting(EditorSession session, JsonObject d)
    {
        if (session.HasUnreturnedEdits) d["unreturnedSteps"] = session.UnreturnedSteps;
        if (session.Recovered) d[session.KeptFromLastTime ? "kept" : "recovered"] = true;
    }

    /// <summary>linkedFile of a document: the file it saves back to. available is false when the document was linked
    /// before a restart and the file can no longer be reached or written.</summary>
    internal static JsonObject? LinkedFile(EditorSession session)
    {
        if (session.LinkedImage is { } link)
        {
            var d = new JsonObject { ["name"] = link.FileName, ["format"] = link.Format.RawValue(), ["requester"] = link.Requester, ["available"] = true };
            AddWaiting(session, d);
            return d;
        }
        if (session.BrokenLink is { } broken)
        {
            // The reason in English: the window's own sentence is in the person's language.
            var d = new JsonObject
            {
                ["name"] = broken.Link.FileName, ["requester"] = broken.Link.Requester, ["available"] = false,
                ["reason"] = EditorSession.LinkTroubleForAgent(broken.Link.Path, broken.Link.FileName, broken.Link.Requester),
            };
            AddWaiting(session, d);
            if (LinkedImage.FormatFor(broken.Link.Path) is { } format) d["format"] = format.RawValue();
            return d;
        }
        return null;
    }

    /// <summary>A failed step: always step_failed, with a reason when the step refused for a known reason.</summary>
    private static JsonObject StepFailure(List<AIController.APIStepResult> results, int failed)
    {
        var body = AgentAPI.APIError.StepFailedBody(failed, results[failed].Op, results[failed].Error ?? "", results[failed].Reason);
        if (results[failed].Hard) body["hard"] = true;
        if (results[failed].Requirements is { Count: > 0 } ids)
        {
            body["requirement"] = ids[0];
            body["requirements"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]);
        }
        return body;
    }

    private static JsonArray StepList(List<AIController.APIStepResult> results) =>
        new([.. results.Select((res, i) =>
        {
            var d = new JsonObject { ["index"] = i, ["op"] = res.Op, ["status"] = res.Status };
            if (res.Error is { } e) d["error"] = e;
            if (res.Info is { } info) d["info"] = info.DeepClone();
            return (JsonNode?)d;
        })]);

    private AgentAPI.Response UndoRedo(EditorSession session, bool redo)
    {
        var undo = session.Document.UndoManager;
        if (redo)
        {
            if (!undo.CanRedo) throw AgentAPI.APIError.Unprocessable("nothing_to_redo", "Nothing to redo.");
            var name = undo.RedoActionName;
            session.Document.Redo();
            return Json(new JsonObject { ["redone"] = name, ["document"] = Detail(session) });
        }
        if (!undo.CanUndo) throw AgentAPI.APIError.Unprocessable("nothing_to_undo", "Nothing to undo.");
        var undone = undo.UndoActionName;
        session.Document.Undo();
        return Json(new JsonObject { ["undone"] = undone, ["document"] = Detail(session) });
    }
}
