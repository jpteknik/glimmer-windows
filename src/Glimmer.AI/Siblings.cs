// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Siblings.swift
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glimmer.Core;

namespace Glimmer.AI;

// The series' sibling protocol (the-apps/SIBLINGS.md): Glimmer's assistant asks
// Flimmer and Jämmer for things through their local agent APIs, exactly like an
// outside agent, with `X-Sibling: glimmer`. Everything stays on 127.0.0.1.
//
// On Windows there are no sibling apps: the client and the contracts are here, and the
// shared client (`SiblingClient.Shared`) does not look, so it finds nothing. What is left out is
// what only a Mac has: choosing among installed copies of an app (`bestCopy`) and launching one.

/// <summary>
/// Which build this is. Debug builds carry <c>.debug</c> on their bundle id and talk to the
/// siblings' debug builds (series rule), so tests never reach the real apps.
/// </summary>
public static class AppIdentity
{
    /// <summary>The Mac's <c>Bundle.main.bundleIdentifier</c>; the app sets it when it is a debug build.</summary>
    public static string OwnBundleID { get; set; } = "com.brattoo.glimmer";
    public static bool IsDebugBuild => IsDebug(OwnBundleID);

    /// <summary>A debug build, or its guest copy for a visiting test (<c>.debug.guest</c>, SIBLINGS.md §1: a copy with a container of its own).</summary>
    public static bool IsDebug(string bundleID) => bundleID.EndsWith(".debug", StringComparison.Ordinal) || bundleID.EndsWith(".debug.guest", StringComparison.Ordinal);

    /// <summary>Glimmer's own id in this build (com.brattoo.glimmer or com.brattoo.glimmer.debug).</summary>
    public static string GlimmerBundleID => "com.brattoo.glimmer" + (IsDebugBuild ? ".debug" : "");
}

/// <summary>A sibling app Glimmer can ask for things.</summary>
public enum SiblingApp { Flimmer, Jammer }

public static class SiblingApps
{
    internal static readonly IReadOnlyList<SiblingApp> All = [SiblingApp.Flimmer, SiblingApp.Jammer];

    extension(SiblingApp)
    {
        public static IReadOnlyList<SiblingApp> AllCases => SiblingApps.All;

        /// <summary>The sibling a tool name like <c>jammer_gain</c> belongs to, and the op name.</summary>
        public static (SiblingApp App, string Op)? Split(string tool)
        {
            foreach (var app in SiblingApp.AllCases)
            {
                var prefix = app.RawValue() + "_";
                if (tool.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var op = tool[prefix.Length..];
                    return op.Length == 0 ? null : (app, op);
                }
            }
            return null;
        }

        /// <summary>Swift's <c>SiblingApp(rawValue:)</c>.</summary>
        public static SiblingApp? FromRawValue(string raw) => raw switch { "flimmer" => SiblingApp.Flimmer, "jammer" => SiblingApp.Jammer, _ => null };
    }

    public static string RawValue(this SiblingApp app) => app == SiblingApp.Jammer ? "jammer" : "flimmer";

    /// <summary><c>app.name</c> in its <c>GET /api/v1/status</c>; a port counts only when this matches exactly.</summary>
    public static string AppName(this SiblingApp app) => app == SiblingApp.Jammer ? "Jammer" : "Flimmer";
    public static string DisplayName(this SiblingApp app) => app == SiblingApp.Jammer ? "Jämmer" : "Flimmer";

    /// <summary>The sibling's id in this kind of build: debug builds pair with debug builds.</summary>
    public static string BundleID(this SiblingApp app) => app.BundleID(AppIdentity.IsDebugBuild);
    public static string BundleID(this SiblingApp app, bool debug) => "com.brattoo." + app.RawValue() + (debug ? ".debug" : "");

    /// <summary>
    /// Whether a running app with this bundle id is this sibling, for a build of this kind. A debug
    /// build also accepts the sibling's guest copy (<c>….debug.guest</c>); a release build never does.
    /// </summary>
    public static bool Accepts(this SiblingApp app, string? bundleID, bool? debug = null)
    {
        if (bundleID is null) return false;
        var isDebug = debug ?? AppIdentity.IsDebugBuild;
        return bundleID == app.BundleID(isDebug) || (isDebug && bundleID == app.BundleID(true) + ".guest");
    }

    /// <summary>The header every request that isn't a GET must carry.</summary>
    public static string AgentHeader(this SiblingApp app) => $"X-{app.AppName()}-Agent";

    /// <summary>First port, then the reserved range (SIBLINGS.md §1).</summary>
    public static IReadOnlyList<int> Ports(this SiblingApp app) =>
        app == SiblingApp.Jammer ? new[] { 9003 }.Concat(Enumerable.Range(9301, 19)).ToList() : new[] { 9002 }.Concat(Enumerable.Range(9201, 19)).ToList();

    /// <summary>What the app is, for the assistant.</summary>
    internal static string Role(this SiblingApp app) => app == SiblingApp.Jammer ? "audio editor" : "video editor";
}

/// <summary>One entry of a sibling's <c>GET /api/v1/operations</c>.</summary>
public sealed record SiblingOperation
{
    public SiblingApp App { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    /// <summary>JSON Schema; properties may carry <c>"x-in": "path"|"query"</c>.</summary>
    public AgentValue Params { get; init; }
    public bool Paid { get; init; }
    public bool Undoable { get; init; }
    public bool StartsJob { get; init; }
    /// <summary>Set for requests that aren't steps; steps go through <c>/steps</c>.</summary>
    public CallInfo? Call { get; init; }

    public sealed record CallInfo(string Method, string Path);

    /// <summary>Provider tool names allow only <c>^[a-zA-Z0-9_-]{1,64}$</c>.</summary>
    public string ToolName => $"{App.RawValue()}_{Name}";

    public SiblingOperation(SiblingApp app, string name, string description, AgentValue? @params = null,
                            bool paid = false, bool undoable = true, bool startsJob = false, CallInfo? call = null)
    {
        App = app;
        Name = name;
        Description = description;
        Params = @params ?? AgentValue.Object();
        Paid = paid;
        Undoable = undoable;
        StartsJob = startsJob;
        Call = call;
    }

    static readonly Regex NamePattern = new("^[a-zA-Z0-9_-]{1,40}$", RegexOptions.CultureInvariant);

    /// <summary>Parses a sibling's operation list, skipping malformed entries.</summary>
    public static List<SiblingOperation> Parse(ReadOnlySpan<byte> data, SiblingApp app)
    {
        AgentValue? root;
        try { root = JsonSerializer.Deserialize<AgentValue>(data); } catch (JsonException) { return []; }
        if (root?.AsObject?.GetValueOrDefault("operations")?.AsArray is not { } list) return [];
        var result = new List<SiblingOperation>();
        foreach (var value in list)
        {
            if (value.AsObject is not { } o || o.GetValueOrDefault("name")?.AsString is not { } name || !NamePattern.IsMatch(name)) continue;
            CallInfo? call = null;
            if (o.GetValueOrDefault("call")?.AsObject is { } c && c.GetValueOrDefault("method")?.AsString is { } method
                && c.GetValueOrDefault("path")?.AsString is { } path && path.StartsWith("/api/", StringComparison.Ordinal))
                call = new CallInfo(method.ToUpperInvariant(), path);
            result.Add(new SiblingOperation(app, name, o.GetValueOrDefault("description")?.AsString ?? "",
                                            o.GetValueOrDefault("params") ?? AgentValue.Object(), o.GetValueOrDefault("paid")?.AsBool ?? false,
                                            o.GetValueOrDefault("undoable")?.AsBool ?? true, o.GetValueOrDefault("startsJob")?.AsBool ?? false, call));
        }
        return result;
    }

    /// <summary>
    /// A name in <paramref name="params"/> that the sibling's schema doesn't have, when that schema is closed
    /// (<c>"additionalProperties": false</c>, SIBLINGS.md §3): the sibling would refuse the call, so
    /// Glimmer refuses first, with the same words, and sends nothing. Null when every name is
    /// known, or when the sibling's schema is open (an older sibling: it decides for itself).
    /// <c>document</c> and <c>rationale</c> are Glimmer's own additions to the tool and are taken out before this.
    /// </summary>
    public (string Name, IReadOnlyList<string> Allowed)? Stray(IReadOnlyDictionary<string, AgentValue> @params)
    {
        if (Params.AsObject is not { } schema || schema.GetValueOrDefault("additionalProperties")?.AsBool != false
            || schema.GetValueOrDefault("properties")?.AsObject is not { } properties) return null;
        var unknown = @params.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !properties.ContainsKey(k));
        if (unknown is not null)
            return (unknown, properties.Keys.Where(k => k != "rationale").Order(StringComparer.Ordinal).ToList());
        // Settings that depend on a sort (SIBLINGS.md §3, as settled 2026-11-16): a table "x-byType"
        // on the schema object whose properties depend on the sort, with the chooser named beside
        // it in "x-byTypeKey". The table's keys are the chooser's values, each a closed schema.
        // 1. On the operation's own schema: the sort's properties are top-level, beside the chooser.
        if (schema.GetValueOrDefault("x-byType")?.AsObject is { } table && schema.GetValueOrDefault("x-byTypeKey")?.AsString is { } chooser
            && @params.GetValueOrDefault(chooser)?.AsString is { } sort
            && table.GetValueOrDefault(sort)?.AsObject?.GetValueOrDefault("properties")?.AsObject is { } own)
        {
            // Every name some sort has; one that belongs to another sort than the chosen one is refused.
            var dependent = new HashSet<string>();
            foreach (var other in table.Values)
                foreach (var key in other.AsObject?.GetValueOrDefault("properties")?.AsObject?.Keys ?? Enumerable.Empty<string>()) dependent.Add(key);
            var name = @params.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => dependent.Contains(k) && !own.ContainsKey(k));
            if (name is not null)
            {
                // What this sort does take: its own settings and everything that belongs to no sort.
                var valid = properties.Keys.Where(k => k != "rationale" && (!dependent.Contains(k) || own.ContainsKey(k))).Order(StringComparer.Ordinal).ToList();
                return ($"{name} for {sort}", valid);
            }
        }
        // 2. On a nested settings property: the settings are an object of their own.
        foreach (var (carrier, nestedSchema) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (nestedSchema.AsObject?.GetValueOrDefault("x-byType")?.AsObject is not { } nestedTable
                || nestedSchema.AsObject.GetValueOrDefault("x-byTypeKey")?.AsString is not { } nestedChooser
                || @params.GetValueOrDefault(carrier)?.AsObject is not { } given
                || @params.GetValueOrDefault(nestedChooser)?.AsString is not { } nestedSort
                || nestedTable.GetValueOrDefault(nestedSort)?.AsObject is not { } nested
                || nested.GetValueOrDefault("additionalProperties")?.AsBool != false
                || nested.GetValueOrDefault("properties")?.AsObject is not { } allowed) continue;
            var name = given.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !allowed.ContainsKey(k));
            if (name is not null) return ($"{name} in {carrier}", allowed.Keys.Order(StringComparer.Ordinal).ToList());
        }
        return null;
    }

    /// <summary>
    /// A table of settings per sort that doesn't name its chooser (<c>"x-byTypeKey"</c>): Glimmer does
    /// not guess which property chooses, so those settings go unchecked, and the plan says so.
    /// Returns where the table is ("the step" or the property's name), or null.
    /// </summary>
    public string? UncheckedTable(IReadOnlyDictionary<string, AgentValue> @params)
    {
        if (Params.AsObject is not { } schema) return null;
        if (schema.ContainsKey("x-byType") && schema.GetValueOrDefault("x-byTypeKey")?.AsString is null) return "the step";
        var properties = schema.GetValueOrDefault("properties")?.AsObject ?? ImmutableDictionary<string, AgentValue>.Empty;
        foreach (var (carrier, nested) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!@params.ContainsKey(carrier)) continue;
            if (nested.AsObject is { } n && n.ContainsKey("x-byType") && n.GetValueOrDefault("x-byTypeKey")?.AsString is null) return carrier;
        }
        return null;
    }

    /// <summary>
    /// The function tool for the assistant: the sibling's description and schema as they
    /// are, minus the <c>x-in</c> markers, plus <c>document</c> when the op doesn't name one itself.
    /// </summary>
    public JsonObject ToolSchema
    {
        get
        {
            var schema = Params.ToNode() as JsonObject ?? new JsonObject();
            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            foreach (var (_, value) in properties)
                if (value is JsonObject p) p.Remove("x-in");
            if (!properties.ContainsKey("id"))
                properties["document"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"Optional. The {App.DisplayName()} document id or name; default: the one in front.",
                };
            properties["rationale"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence: why this step." };
            schema["type"] = "object";
            schema["properties"] = properties;
            if (!schema.ContainsKey("required")) schema["required"] = new JsonArray();
            var text = $"In {App.DisplayName()} (the {App.Role()}, a separate app): {Description}";
            if (StartsJob) text += " Takes a while.";
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = ToolName, ["description"] = text, ["parameters"] = schema },
            };
        }
    }

    public bool Equals(SiblingOperation? other) =>
        other is not null && App == other.App && Name == other.Name && Description == other.Description && Params.Equals(other.Params)
        && Paid == other.Paid && Undoable == other.Undoable && StartsJob == other.StartsJob && Equals(Call, other.Call);
    public override int GetHashCode() => HashCode.Combine(App, Name, Paid, StartsJob);
}

/// <summary>An open document in a sibling.</summary>
public sealed record SiblingDocument(string Id, string Name, double? Duration = null)
{
    /// <summary>The document in the sibling's front window, when the sibling says so.</summary>
    public bool Frontmost { get; init; }
}

public enum SiblingErrorKind { NotRunning, NoDocument, UnknownOperation, Paid, Failed, StrayParameter }

public sealed class SiblingError : Exception, IEquatable<SiblingError>
{
    public SiblingErrorKind Kind { get; }
    public SiblingApp? App { get; }
    /// <summary>The document key, the operation, the tool or the message, by kind.</summary>
    public string? Detail { get; }
    public string? Name { get; }
    public IReadOnlyList<string> Allowed { get; }

    SiblingError(SiblingErrorKind kind, SiblingApp? app = null, string? detail = null, string? name = null, IReadOnlyList<string>? allowed = null)
    {
        Kind = kind; App = app; Detail = detail; Name = name; Allowed = allowed ?? [];
    }

    public static SiblingError NotRunning(SiblingApp app) => new(SiblingErrorKind.NotRunning, app);
    public static SiblingError NoDocument(SiblingApp app, string? key) => new(SiblingErrorKind.NoDocument, app, key);
    public static SiblingError UnknownOperation(string name) => new(SiblingErrorKind.UnknownOperation, detail: name);
    public static SiblingError Paid(string name) => new(SiblingErrorKind.Paid, detail: name);
    public static SiblingError Failed(SiblingApp app, string message) => new(SiblingErrorKind.Failed, app, message);
    /// <summary>The step carries a name the sibling's operation doesn't have; nothing was sent.</summary>
    public static SiblingError StrayParameter(string tool, string name, IReadOnlyList<string> allowed) => new(SiblingErrorKind.StrayParameter, detail: tool, name: name, allowed: allowed);

    public override string Message => Kind switch
    {
        SiblingErrorKind.StrayParameter => Allowed.Count == 0
            ? $"{Detail} has no parameter named {Name}. It takes none."
            : $"{Detail} has no parameter named {Name}. It has: {string.Join(", ", Allowed)}.",
        SiblingErrorKind.NotRunning => $"{App!.Value.DisplayName()} is not running.",
        SiblingErrorKind.NoDocument => Detail is { } key ? $"{App!.Value.DisplayName()} has no open document “{key}”." : $"{App!.Value.DisplayName()} has no open document.",
        SiblingErrorKind.UnknownOperation => $"Unknown step “{Detail}”.",
        SiblingErrorKind.Paid => $"“{Detail}” costs money in the other app, so Glimmer doesn't ask for it.",
        _ => $"{App!.Value.DisplayName()}: {Detail}",
    };

    public bool Equals(SiblingError? other) =>
        other is not null && Kind == other.Kind && App == other.App && Detail == other.Detail && Name == other.Name && Allowed.SequenceEqual(other.Allowed);
    public override bool Equals(object? obj) => Equals(obj as SiblingError);
    public override int GetHashCode() => HashCode.Combine(Kind, App, Detail, Name);
}

/// <summary>Waveform columns from a sibling, reduced to one amplitude per column (0…1).</summary>
public sealed record SiblingWaveform(IReadOnlyList<double> Amplitudes, string DocumentName)
{
    public bool Equals(SiblingWaveform? other) => other is not null && DocumentName == other.DocumentName && Amplitudes.SequenceEqual(other.Amplitudes);
    public override int GetHashCode() => HashCode.Combine(DocumentName, Amplitudes.Count);

    /// <summary>
    /// Jämmer's <c>{"waveform": {"tracks": [{"channels": [[[min, max], …]]}]}}</c>: the loudest
    /// track and channel per column.
    /// </summary>
    public static SiblingWaveform? Parse(ReadOnlySpan<byte> data, string documentName)
    {
        AgentValue? root;
        try { root = JsonSerializer.Deserialize<AgentValue>(data); } catch (JsonException) { return null; }
        if (root?.AsObject?.GetValueOrDefault("waveform")?.AsObject?.GetValueOrDefault("tracks")?.AsArray is not { } tracks) return null;
        var amps = new List<double>();
        foreach (var track in tracks)
        {
            if (track.AsObject?.GetValueOrDefault("channels")?.AsArray is not { } channels) continue;
            foreach (var channel in channels)
            {
                if (channel.AsArray is not { } columns) continue;
                while (amps.Count < columns.Length) amps.Add(0);
                for (int i = 0; i < columns.Length; i++)
                {
                    if (columns[i].AsArray is not { Length: 2 } mm || mm[0].AsNumber is not { } lo || mm[1].AsNumber is not { } hi) continue;
                    amps[i] = Math.Max(amps[i], Math.Min(1, Math.Max(Math.Abs(lo), Math.Abs(hi))));
                }
            }
        }
        return amps.Count == 0 ? null : new SiblingWaveform(amps, documentName);
    }

    public enum Style { Bars, Wave }

    /// <summary>
    /// The waveform as a closed vector path filling <paramref name="rect"/> (y grows downward), mirrored
    /// around its middle and scaled so the loudest column reaches the edges. Bars are one
    /// rectangle each; <c>wave</c> is a single outline through the column peaks.
    /// </summary>
    public VectorPath Path(CGRectLike rect, Style style, double gap = 0.35)
    {
        int n = Amplitudes.Count;
        double peak = Math.Max(Amplitudes.Count == 0 ? 0 : Amplitudes.Max(), 1e-6);
        double mid = (rect.MinY + rect.MaxY) / 2;
        double half = (rect.MaxY - rect.MinY) / 2;
        double step = (rect.MaxX - rect.MinX) / Math.Max(n, 1);
        // Quiet columns keep a sliver, so silence still reads as a line.
        double H(double a) => Math.Max(half * a / peak, Math.Min(half, Math.Max(1, half * 0.02)));
        var name = "Waveform";
        if (DocumentName.Length > 0) name += " – " + DocumentName;
        if (style == Style.Bars)
        {
            double w = Math.Max(1, step * (1 - Math.Min(Math.Max(gap, 0), 0.9)));
            var subpaths = Amplitudes.Select((a, i) =>
            {
                double x = rect.MinX + i * step + (step - w) / 2;
                double y = H(a);
                return new Subpath(new[] { new Vec2(x, mid - y), new Vec2(x + w, mid - y), new Vec2(x + w, mid + y), new Vec2(x, mid + y) }
                                       .Select(p => new PathAnchor(p)), isClosed: true);
            }).ToList();
            return new VectorPath(name, subpaths);
        }
        var xs = Enumerable.Range(0, n).Select(i => rect.MinX + (i + 0.5) * step).ToList();
        var top = xs.Zip(Amplitudes, (x, a) => new Vec2(x, mid - H(a)));
        var bottom = xs.Zip(Amplitudes, (x, a) => new Vec2(x, mid + H(a))).Reverse();
        var anchors = new[] { new Vec2(rect.MinX, mid) }.Concat(top).Append(new Vec2(rect.MaxX, mid)).Concat(bottom);
        return new VectorPath(name, [new Subpath(anchors.Select(p => new PathAnchor(p)), isClosed: true)]);
    }
}

public static class SiblingWaveformStyles
{
    /// <summary>The raw value: bars or wave.</summary>
    public static string RawValue(this SiblingWaveform.Style style) => style == SiblingWaveform.Style.Wave ? "wave" : "bars";
}

/// <summary>
/// Talks to the siblings. Discovery runs with short timeouts; the port is remembered and searched
/// again after the first failure. <see cref="Shared"/> does not look at all (there are no sibling apps on Windows);
/// a client built with <c>enabled: true</c> and an <see cref="HttpClient"/> does, which is how the protocol is tested.
/// </summary>
public sealed class SiblingClient
{
    /// <summary>The client the app uses: it finds nothing, because Windows has no Flimmer or Jämmer.</summary>
    public static SiblingClient Shared { get; } = new(enabled: false);

    readonly HttpClient? http;
    readonly TimeSpan portTimeout;
    readonly bool enabled;
    readonly object gate = new();
    readonly Dictionary<SiblingApp, int> ports = new();
    readonly Dictionary<SiblingApp, (List<SiblingOperation> Ops, DateTime At)> operationCache = new();

    public SiblingClient(HttpClient? http = null, TimeSpan? portTimeout = null, bool enabled = true)
    {
        this.enabled = enabled;
        this.http = enabled ? http ?? CreateDefaultClient() : null;
        this.portTimeout = portTimeout ?? TimeSpan.FromSeconds(0.3);
    }

    static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

    // MARK: Discovery

    /// <summary>
    /// The sibling's port, or null when it isn't running. The first port is tried first,
    /// then the whole reserved range at once.
    /// </summary>
    public async Task<int?> PortAsync(SiblingApp app, CancellationToken ct = default)
    {
        if (!enabled) return null;
        lock (gate) if (ports.TryGetValue(app, out var known)) return known;
        var all = app.Ports();
        // The first port is where the sibling nearly always is; it gets a longer timeout, since a
        // cold first connection on a busy machine can take longer than the range's 300 ms.
        if (await IsSiblingAsync(app, all[0], portTimeout > TimeSpan.FromSeconds(1) ? portTimeout : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            lock (gate) ports[app] = all[0];
            return all[0];
        }
        var hits = await Task.WhenAll(all.Skip(1).Select(async p => await IsSiblingAsync(app, p, portTimeout, ct).ConfigureAwait(false) ? (int?)p : null)).ConfigureAwait(false);
        var found = hits.Where(h => h is not null).Select(h => h!.Value).DefaultIfEmpty(-1).Min();
        if (found < 0) return null;
        lock (gate) ports[app] = found;
        return found;
    }

    async Task<bool> IsSiblingAsync(SiblingApp app, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/v1/status");
            request.Headers.TryAddWithoutValidation(AgentAPI.SiblingHeader, "glimmer");
            using var response = await http!.SendAsync(request, cts.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200) return false;
            var data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<AgentValue>(data)?.AsObject?.GetValueOrDefault("app")?.AsObject;
            if (info is null || info.GetValueOrDefault("name")?.AsString != app.AppName()) return false;
            // A sibling that says which build it is must be the matching one (debug with debug).
            if (info.GetValueOrDefault("bundleID")?.AsString is { } id) return app.Accepts(id);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Which siblings run right now (searched concurrently).</summary>
    public async Task<IReadOnlyList<SiblingApp>> RunningAsync(CancellationToken ct = default)
    {
        var found = await Task.WhenAll(SiblingApp.AllCases.Select(async app => await PortAsync(app, ct).ConfigureAwait(false) is not null ? (SiblingApp?)app : null)).ConfigureAwait(false);
        return SiblingApp.AllCases.Where(a => found.Contains(a)).ToList();
    }

    // MARK: Requests

    /// <summary>
    /// One request to a sibling: its agent header, <c>X-Sibling: glimmer</c>, no Origin. A
    /// connection failure forgets the port and searches once more.
    /// </summary>
    internal async Task<(int Status, byte[] Data)> SendAsync(SiblingApp app, string method, string path, IReadOnlyDictionary<string, string>? query = null,
                                                             JsonNode? body = null, bool retry = true, CancellationToken ct = default)
    {
        if (await PortAsync(app, ct).ConfigureAwait(false) is not { } port) throw SiblingError.NotRunning(app);
        var uri = new System.Text.StringBuilder($"http://127.0.0.1:{port}{path}");
        if (query is { Count: > 0 })
            uri.Append("?" + string.Join("&", query.OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}")));
        using var request = new HttpRequestMessage(new HttpMethod(method), uri.ToString());
        request.Headers.TryAddWithoutValidation(app.AgentHeader(), "1");
        request.Headers.TryAddWithoutValidation(AgentAPI.SiblingHeader, "glimmer");
        if (body is not null)
            request.Content = new ByteArrayContent(Json.Bytes(body)) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } };
        try
        {
            using var response = await http!.SendAsync(request, ct).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (!retry) throw SiblingError.NotRunning(app);
            lock (gate) { ports.Remove(app); operationCache.Remove(app); }
            return await SendAsync(app, method, path, query, body, retry: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The sibling's error message from <c>{"error": {"message": …}}</c>, or the status code.</summary>
    internal static string Message(int status, byte[] data)
    {
        try { return JsonSerializer.Deserialize<AgentValue>(data)?.AsObject?.GetValueOrDefault("error")?.AsObject?.GetValueOrDefault("message")?.AsString ?? $"HTTP {status}"; }
        catch (JsonException) { return $"HTTP {status}"; }
    }

    /// <summary>The sibling's operations, cached for a minute.</summary>
    public async Task<List<SiblingOperation>> OperationsAsync(SiblingApp app, CancellationToken ct = default)
    {
        lock (gate) if (operationCache.TryGetValue(app, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 60) return cached.Ops;
        var (status, data) = await SendAsync(app, "GET", "/api/v1/operations", ct: ct).ConfigureAwait(false);
        if (status != 200) throw SiblingError.Failed(app, Message(status, data));
        var ops = SiblingOperation.Parse(data, app);
        lock (gate) operationCache[app] = (ops, DateTime.UtcNow);
        return ops;
    }

    public async Task<List<SiblingDocument>> DocumentsAsync(SiblingApp app, CancellationToken ct = default)
    {
        var (status, data) = await SendAsync(app, "GET", "/api/v1/documents", ct: ct).ConfigureAwait(false);
        AgentValue? root = null;
        try { root = JsonSerializer.Deserialize<AgentValue>(data); } catch (JsonException) { }
        if (status != 200 || root?.AsObject?.GetValueOrDefault("documents")?.AsArray is not { } list) throw SiblingError.Failed(app, Message(status, data));
        var result = new List<SiblingDocument>();
        foreach (var d in list)
        {
            if (d.AsObject is not { } o || o.GetValueOrDefault("id")?.AsString is not { } id) continue;
            result.Add(new SiblingDocument(id, o.GetValueOrDefault("name")?.AsString ?? "", o.GetValueOrDefault("duration")?.AsNumber)
            { Frontmost = o.GetValueOrDefault("frontmost")?.AsBool ?? false });
        }
        return result;
    }

    /// <summary>The document an op works on: by id or name, else the front one, else the first.</summary>
    public async Task<SiblingDocument> DocumentAsync(SiblingApp app, string? key, CancellationToken ct = default)
    {
        var docs = await DocumentsAsync(app, ct).ConfigureAwait(false);
        key = key?.Trim(' ', '\t');
        if (!string.IsNullOrEmpty(key))
        {
            var hit = docs.FirstOrDefault(d => string.Equals(d.Id, key, StringComparison.InvariantCultureIgnoreCase) || string.Equals(d.Name, key, StringComparison.InvariantCultureIgnoreCase));
            return hit ?? throw SiblingError.NoDocument(app, key);
        }
        return docs.FirstOrDefault(d => d.Frontmost) ?? docs.FirstOrDefault() ?? throw SiblingError.NoDocument(app, null);
    }

    // MARK: Tools

    /// <summary>
    /// Every free operation of every running sibling, as assistant tools, plus a line for
    /// the editor state naming their open documents. Siblings that don't answer are left out.
    /// </summary>
    public async Task<(List<SiblingOperation> Tools, string? Context)> ToolboxAsync(CancellationToken ct = default)
    {
        var tools = new List<SiblingOperation>();
        var lines = new List<string>();
        foreach (var app in await RunningAsync(ct).ConfigureAwait(false))
        {
            List<SiblingOperation> ops;
            try { ops = await OperationsAsync(app, ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            tools.AddRange(ops.Where(o => !o.Paid));
            List<SiblingDocument> docs;
            try { docs = await DocumentsAsync(app, ct).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { docs = []; }
            var list = string.Join(", ", docs.Take(20).Select(d =>
                $"“{d.Name}” (id {d.Id}{(d.Duration is { } s ? ", " + s.ToString("F1", CultureInfo.InvariantCulture) + " s" : "")})"));
            lines.Add($"{app.DisplayName()} ({app.Role()}) is running" + (docs.Count == 0 ? " with no open document" : $" with: {list}"));
        }
        if (lines.Count == 0) return (tools, null);
        var prefixes = string.Join(", ", tools.Select(t => t.App.RawValue() + "_").ToHashSet().Order(StringComparer.Ordinal));
        return (tools, $"[Sibling apps: {string.Join("; ", lines)}. Tools starting with {prefixes} run in those apps; the result stays there.]");
    }

    /// <summary>
    /// Runs one sibling tool: a step through <c>/steps</c> (one undo step there), or a request
    /// with <c>call</c>, its params placed by <c>x-in</c>. Jobs are followed to the end.
    /// </summary>
    public async Task<AgentValue> PerformAsync(string tool, IReadOnlyDictionary<string, AgentValue> @params, CancellationToken ct = default)
    {
        if (SiblingApp.Split(tool) is not { } split) throw SiblingError.UnknownOperation(tool);
        var (app, name) = split;
        var op = (await OperationsAsync(app, ct).ConfigureAwait(false)).FirstOrDefault(o => o.Name == name) ?? throw SiblingError.UnknownOperation(tool);
        if (op.Paid) throw SiblingError.Paid(tool);
        var given = new Dictionary<string, AgentValue>(@params);
        given.Remove("rationale");
        string? key = null;
        if (given.Remove("document", out var documentValue)) key = documentValue.AsString;
        key ??= given.GetValueOrDefault("id")?.AsString;
        if (op.Stray(given) is { } stray) throw SiblingError.StrayParameter(tool, stray.Name, stray.Allowed);
        var doc = await DocumentAsync(app, key, ct).ConfigureAwait(false);
        int status;
        byte[] data;
        if (op.Call is { } call)
        {
            var properties = op.Params.AsObject?.GetValueOrDefault("properties")?.AsObject ?? ImmutableDictionary<string, AgentValue>.Empty;
            var path = call.Path.Replace("{id}", doc.Id);
            var query = new Dictionary<string, string>();
            var body = new JsonObject();
            foreach (var (k, v) in given)
            {
                if (k == "id") continue;
                var place = properties.GetValueOrDefault(k)?.AsObject?.GetValueOrDefault("x-in")?.AsString;
                if (place == "path") path = path.Replace("{" + k + "}", Uri.EscapeDataString(v.PlainString));
                else if (place == "query" || call.Method == "GET") query[k] = v.PlainString;
                else body[k] = v.ToNode();
            }
            (status, data) = await SendAsync(app, call.Method, path, query, call.Method == "GET" ? null : body, ct: ct).ConfigureAwait(false);
        }
        else
        {
            var step = new JsonObject { ["op"] = name, ["params"] = AgentValue.Object(given).ToNode() };
            (status, data) = await SendAsync(app, "POST", $"/api/v1/documents/{doc.Id}/steps",
                                             body: new JsonObject { ["steps"] = new JsonArray(step), ["allowPaid"] = false }, ct: ct).ConfigureAwait(false);
        }
        if (status < 200 || status >= 300) throw SiblingError.Failed(app, Message(status, data));
        AgentValue result;
        try { result = JsonSerializer.Deserialize<AgentValue>(data) ?? AgentValue.Null; } catch (JsonException) { result = AgentValue.Null; }
        if (status == 202 && result.AsObject?.GetValueOrDefault("job")?.AsObject?.GetValueOrDefault("id")?.AsString is { } id)
            return await FollowAsync(app, id, ct: ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Polls <c>GET /api/v1/jobs/{id}</c> until the job is done (SIBLINGS.md §2).</summary>
    internal async Task<AgentValue> FollowAsync(SiblingApp app, string id, TimeSpan? interval = null, CancellationToken ct = default)
    {
        var wait = interval ?? TimeSpan.FromMilliseconds(500);
        while (true)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
            var (status, data) = await SendAsync(app, "GET", $"/api/v1/jobs/{id}", ct: ct).ConfigureAwait(false);
            AgentValue? root = null;
            try { root = JsonSerializer.Deserialize<AgentValue>(data); } catch (JsonException) { }
            if (status != 200 || root?.AsObject?.GetValueOrDefault("job")?.AsObject is not { } job) throw SiblingError.Failed(app, Message(status, data));
            // SIBLINGS.md §2: queued, running, done, failed or canceled. A stopped job ends the wait in either spelling
            // (the series wrote "cancelled" before 2026-11-17), and a status that is none of these ends it with an error:
            // waiting on for a word this version does not know would be waiting for ever.
            var said = job.GetValueOrDefault("status")?.AsString;
            switch (said)
            {
                case "done": return job.GetValueOrDefault("result") ?? AgentValue.Null;
                case "failed": throw SiblingError.Failed(app, job.GetValueOrDefault("error")?.AsObject?.GetValueOrDefault("message")?.AsString ?? "The job failed.");
                case "canceled" or "cancelled": throw new OperationCanceledException();
                case "queued" or "running": continue;
                default: throw SiblingError.Failed(app, $"The job answered with a status Glimmer does not know ({said ?? "none"}), so Glimmer stopped waiting for it.");
            }
        }
    }

    /// <summary>The waveform of a sibling's document (<c>GET …/waveform?width=</c>), one amplitude per column.</summary>
    public async Task<SiblingWaveform> WaveformAsync(SiblingApp app, string? document, int columns, CancellationToken ct = default)
    {
        var doc = await DocumentAsync(app, document, ct).ConfigureAwait(false);
        var (status, data) = await SendAsync(app, "GET", $"/api/v1/documents/{doc.Id}/waveform",
                                             new Dictionary<string, string> { ["width"] = Math.Min(Math.Max(columns, 8), 4_000).ToString(CultureInfo.InvariantCulture) }, ct: ct).ConfigureAwait(false);
        if (status != 200) throw SiblingError.Failed(app, Message(status, data));
        return SiblingWaveform.Parse(data, doc.Name) ?? throw SiblingError.Failed(app, "The waveform couldn't be read.");
    }
}
