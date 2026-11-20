// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/SiblingTests.swift, and the sibling suites of StepParametersTests.swift
// (SiblingStrayParameterTests, RealSiblingOperations, GuestCopyTests).
// Not ported: bestCopy (which of several installed copies of a Mac app to open: Windows has no sibling apps to launch) and the
// live test against a running Jämmer (GLIMMER_LIVE_SIBLINGS, never in CI), which is kept below and skipped unless that is set.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

/// <summary>A made-up Jämmer behind a fake handler: no sockets, no real app. Flimmer isn't running.</summary>
internal sealed class FakeJammer : HttpMessageHandler
{
    public sealed record Seen(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    readonly object gate = new();
    readonly List<Seen> seen = new();
    public bool Running { get; set; } = true;
    public string? StatusBundleID { get; set; }

    public IReadOnlyList<Seen> Requests { get { lock (gate) return seen.ToList(); } }

    public SiblingClient Client() => new(new HttpClient(this, disposeHandler: false), TimeSpan.FromMilliseconds(300));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(", ", h.Value);
        var url = request.RequestUri!;
        bool running;
        lock (gate) { seen.Add(new Seen(request.Method.Method, url, headers, body)); running = Running; }
        if (!running || url.Port != 9003) throw new HttpRequestException("cannot connect to host");
        var (status, json) = Answer(request.Method.Method, url);
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json.ToJsonString())) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        return response;
    }

    (int, JsonNode) Answer(string method, Uri url)
    {
        switch (method, url.AbsolutePath)
        {
            case ("GET", "/api/v1/status"):
                var app = new JsonObject { ["name"] = "Jammer", ["version"] = "0.1.0" };
                if (StatusBundleID is { } id) app["bundleID"] = id;
                return (200, new JsonObject { ["app"] = app });
            case ("GET", "/api/v1/operations"):
                var start = new JsonObject { ["type"] = "number", ["x-in"] = "query" };
                var idProp = new JsonObject { ["type"] = "string", ["x-in"] = "path" };
                JsonObject Op(string name, string description, bool paid, bool startsJob, bool undoable, JsonObject props, JsonArray required, JsonObject? call = null)
                {
                    var o = new JsonObject
                    {
                        ["name"] = name, ["description"] = description, ["paid"] = paid, ["undoable"] = undoable, ["startsJob"] = startsJob,
                        ["params"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required },
                    };
                    if (call is not null) o["call"] = call;
                    return o;
                }
                return (200, new JsonObject
                {
                    ["operations"] = new JsonArray(
                        Op("gain", "Change the level.", false, false, true, new JsonObject { ["db"] = new JsonObject { ["type"] = "number" } }, new JsonArray("db")),
                        Op("echo", "Add an echo.", false, false, true, new JsonObject { ["delay"] = new JsonObject { ["type"] = "number" } }, new JsonArray()),
                        Op("analysis", "Measure loudness.", false, false, false, new JsonObject { ["id"] = idProp.DeepClone(), ["start"] = start }, new JsonArray("id"),
                           new JsonObject { ["method"] = "GET", ["path"] = "/api/v1/documents/{id}/analysis" }),
                        Op("restore", "Clean up the sound.", false, true, true, new JsonObject { ["id"] = idProp.DeepClone(), ["strength"] = new JsonObject { ["type"] = "number" } }, new JsonArray("id"),
                           new JsonObject { ["method"] = "POST", ["path"] = "/api/v1/documents/{id}/restore" }),
                        Op("studio_voice", "Cloud voice.", true, true, true, new JsonObject(), new JsonArray()),
                        new JsonObject { ["name"] = "bad name!", ["description"] = "Malformed." }),
                });
            case ("GET", "/api/v1/documents"):
                return (200, new JsonObject
                {
                    ["documents"] = new JsonArray(
                        new JsonObject { ["id"] = "D2", ["name"] = "Intro", ["duration"] = 3.0 },
                        new JsonObject { ["id"] = "D1", ["name"] = "Episode 7", ["duration"] = 42.5, ["frontmost"] = true }),
                });
            case ("POST", "/api/v1/documents/D1/steps"):
            case ("POST", "/api/v1/documents/D2/steps"):
                return (200, new JsonObject { ["results"] = new JsonArray(new JsonObject { ["index"] = 0, ["status"] = "done" }) });
            case ("GET", "/api/v1/documents/D1/analysis"): return (200, new JsonObject { ["analysis"] = new JsonObject { ["lufs"] = -16 } });
            case ("POST", "/api/v1/documents/D1/restore"): return (202, new JsonObject { ["job"] = new JsonObject { ["id"] = "J1", ["status"] = "running" } });
            case ("GET", "/api/v1/jobs/J1"): return (200, new JsonObject { ["job"] = new JsonObject { ["id"] = "J1", ["status"] = "done", ["result"] = new JsonObject { ["ok"] = true } } });
            // A stopped job in the series' spelling and in the one from before 2026-11-17, and a status no version knows.
            case ("GET", "/api/v1/jobs/STOPPED"): return (200, Job("STOPPED", "canceled"));
            case ("GET", "/api/v1/jobs/STOPPED-OLD"): return (200, Job("STOPPED-OLD", "cancelled"));
            case ("GET", "/api/v1/jobs/STRANGE"): return (200, Job("STRANGE", "paused"));
            case ("GET", "/api/v1/jobs/SILENT"): return (200, Job("SILENT", null));
            case ("GET", "/api/v1/jobs/WAITING"): return (200, Job("WAITING", "queued"));
            case ("GET", "/api/v1/documents/D1/waveform"):
                var query = System.Web.HttpUtility.ParseQueryString(url.Query);
                int width = int.TryParse(query["width"], out var w) ? w : 4;
                var left = new JsonArray(Enumerable.Range(0, width).Select(i => (JsonNode?)new JsonArray(-0.1 * (i % 5), 0.1 * (i % 5))).ToArray());
                var right = new JsonArray(Enumerable.Range(0, width).Select(_ => (JsonNode?)new JsonArray(-0.05, 0.05)).ToArray());
                return (200, new JsonObject
                {
                    ["waveform"] = new JsonObject
                    {
                        ["width"] = width,
                        ["tracks"] = new JsonArray(new JsonObject { ["name"] = "Voice", ["channels"] = new JsonArray(left, right) }),
                    },
                });
            default:
                return (404, new JsonObject { ["error"] = new JsonObject { ["code"] = "not_found", ["message"] = $"No such path: {url.AbsolutePath}." } });
        }
    }

    static JsonObject Job(string id, string? status)
    {
        var job = new JsonObject { ["id"] = id };
        if (status is not null) job["status"] = status;
        return new JsonObject { ["job"] = job };
    }
}

public class SiblingClientTests
{
    static AgentValue N(double v) => AgentValue.Number(v);

    [Fact]
    public async Task FindsJammerAndNotFlimmer()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        Assert.Equal([SiblingApp.Jammer], await client.RunningAsync());
        Assert.Equal(9003, await client.PortAsync(SiblingApp.Jammer));
        Assert.Null(await client.PortAsync(SiblingApp.Flimmer));
        // Every request looks like an outside agent's, with X-Sibling and no Origin.
        foreach (var r in fake.Requests)
        {
            Assert.Equal("glimmer", r.Headers["X-Sibling"]);
            Assert.False(r.Headers.ContainsKey("Origin"));
        }
    }

    [Fact]
    public async Task TheSharedClientDoesNotLook()
    {
        Assert.Empty(await SiblingClient.Shared.RunningAsync());
        var (tools, context) = await SiblingClient.Shared.ToolboxAsync();
        Assert.True(tools.Count == 0 && context is null);
        await Assert.ThrowsAsync<SiblingError>(() => SiblingClient.Shared.PerformAsync("jammer_gain", new Dictionary<string, AgentValue>()));
    }

    [Fact]
    public async Task ASiblingOfTheOtherBuildIsNotUsed()
    {
        // These tests run as a release-style host, so a debug Jämmer on the port doesn't count.
        var fake = new FakeJammer { StatusBundleID = "com.brattoo.jammer.debug" };
        Assert.Null(await fake.Client().PortAsync(SiblingApp.Jammer));
        var release = new FakeJammer { StatusBundleID = "com.brattoo.jammer" };
        Assert.Equal(9003, await release.Client().PortAsync(SiblingApp.Jammer));
    }

    [Fact]
    public async Task OperationsBecomeToolsNamedAfterTheApp()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        var ops = await client.OperationsAsync(SiblingApp.Jammer);
        Assert.Equal(["gain", "echo", "analysis", "restore", "studio_voice"], ops.Select(o => o.Name));
        var (tools, context) = await client.ToolboxAsync();
        Assert.Equal(["jammer_gain", "jammer_echo", "jammer_analysis", "jammer_restore"], tools.Select(t => t.ToolName));   // paid ops are left out
        Assert.True(context!.Contains("Jämmer") && context.Contains("“Episode 7” (id D1, 42.5 s)"));
        foreach (var tool in tools)
        {
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", tool.ToolName);
            var schema = (JsonObject)tool.ToolSchema["function"]!;
            var props = (JsonObject)schema["parameters"]!["properties"]!;
            Assert.All(props, p => Assert.Null(p.Value!["x-in"]));   // x-in stays out of the tool schema
            Assert.Equal(props["document"] is not null, props["id"] is null);
            Assert.StartsWith("In Jämmer", (string?)schema["description"]);
        }
    }

    [Fact]
    public async Task StepsGoThroughStepsWithTheAgentHeader()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        await client.PerformAsync("jammer_echo", new Dictionary<string, AgentValue> { ["delay"] = N(0.3), ["rationale"] = "Space.", ["document"] = "Intro" });
        var post = fake.Requests.Last(r => r.Method == "POST");
        Assert.Equal("/api/v1/documents/D2/steps", post.Url.AbsolutePath);
        Assert.True(post.Headers["X-Jammer-Agent"] == "1" && post.Headers["X-Sibling"] == "glimmer");
        var body = (JsonObject)JsonNode.Parse(post.Body)!;
        var step = (JsonObject)body["steps"]![0]!;
        Assert.True((string?)step["op"] == "echo" && (double?)step["params"]!["delay"] == 0.3);
        Assert.True(step["params"]!["rationale"] is null && body["allowPaid"]!.GetValue<bool>() == false);
    }

    [Fact]
    public async Task CallsPlaceParamsByXIn()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        var result = await client.PerformAsync("jammer_analysis", new Dictionary<string, AgentValue> { ["start"] = N(1) });
        Assert.Equal(-16, result.AsObject!["analysis"].AsObject!["lufs"].AsNumber);
        var get = fake.Requests.Last(r => r.Url.AbsolutePath.EndsWith("/analysis"));
        Assert.True(get.Url.AbsolutePath == "/api/v1/documents/D1/analysis" && get.Url.Query == "?start=1");
    }

    [Fact]
    public async Task JobsAreFollowedToTheEnd()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        var result = await client.PerformAsync("jammer_restore", new Dictionary<string, AgentValue> { ["strength"] = N(0.5) });
        Assert.True(result.AsObject!["ok"].AsBool);
        var post = fake.Requests.Last(r => r.Method == "POST");
        var body = (JsonObject)JsonNode.Parse(post.Body)!;
        Assert.Equal(0.5, (double?)body["strength"]);
    }

    /// <summary>
    /// SIBLINGS.md §2: a stopped job ends the wait in either spelling, and a status that is not one of the five
    /// ends it with an error. (Jämmer began to write "canceled" on 2026-11-17; a reader that knew only
    /// "cancelled" would have waited for ever.)
    /// </summary>
    [Fact]
    public async Task AStoppedOrUnknownJobEndsTheWait()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        foreach (var id in new[] { "STOPPED", "STOPPED-OLD" })
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.FollowAsync(SiblingApp.Jammer, id, TimeSpan.FromMilliseconds(5)));
        foreach (var (id, word) in new[] { ("STRANGE", "paused"), ("SILENT", "none") })
        {
            var error = await Assert.ThrowsAsync<SiblingError>(() => client.FollowAsync(SiblingApp.Jammer, id, TimeSpan.FromMilliseconds(5)));
            Assert.Contains($"a status Glimmer does not know ({word})", error.Message);
        }
        // A job that is only queued is still waited for: asked again and again until the caller gives up.
        using var cts = new CancellationTokenSource();
        var waiting = client.FollowAsync(SiblingApp.Jammer, "WAITING", TimeSpan.FromMilliseconds(5), cts.Token);
        await Task.Delay(300);
        Assert.True(fake.Requests.Count(r => r.Url.AbsolutePath.EndsWith("/jobs/WAITING")) >= 3);
        cts.Cancel();
        // Given up by the caller: the wait ends (as a cancellation, or as the cancelled request's own error).
        await Assert.ThrowsAnyAsync<Exception>(() => waiting);
    }

    [Fact]
    public async Task RefusalsAndErrors()
    {
        var client = new FakeJammer().Client();
        var paid = await Assert.ThrowsAsync<SiblingError>(() => client.PerformAsync("jammer_studio_voice", new Dictionary<string, AgentValue>()));
        Assert.Equal(SiblingError.Paid("jammer_studio_voice"), paid);
        var unknown = await Assert.ThrowsAsync<SiblingError>(() => client.PerformAsync("jammer_teleport", new Dictionary<string, AgentValue>()));
        Assert.Equal(SiblingError.UnknownOperation("jammer_teleport"), unknown);
        var noDocument = await Assert.ThrowsAsync<SiblingError>(() => client.PerformAsync("jammer_gain", new Dictionary<string, AgentValue> { ["db"] = N(3), ["document"] = "Outro" }));
        Assert.Equal(SiblingError.NoDocument(SiblingApp.Jammer, "Outro"), noDocument);
        var flimmer = await Assert.ThrowsAsync<SiblingError>(() => client.PerformAsync("flimmer_cut", new Dictionary<string, AgentValue>()));
        Assert.Equal(SiblingError.NotRunning(SiblingApp.Flimmer), flimmer);
        Assert.Equal("Jämmer has no open document “Outro”.", noDocument.Message);
        Assert.Equal("“jammer_studio_voice” costs money in the other app, so Glimmer doesn't ask for it.", paid.Message);
    }

    [Fact]
    public async Task NotRunningAfterTheAppQuits()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        await client.OperationsAsync(SiblingApp.Jammer);
        fake.Running = false;
        var error = await Assert.ThrowsAsync<SiblingError>(() => client.PerformAsync("jammer_gain", new Dictionary<string, AgentValue> { ["db"] = N(3) }));
        Assert.Equal(SiblingError.NotRunning(SiblingApp.Jammer), error);
    }

    [Fact]
    public async Task WaveformFromJammer()
    {
        var fake = new FakeJammer();
        var client = fake.Client();
        var wave = await client.WaveformAsync(SiblingApp.Jammer, null, 10);
        Assert.True(wave.DocumentName == "Episode 7" && wave.Amplitudes.Count == 10);
        // The louder channel wins per column.
        Assert.True(wave.Amplitudes[0] == 0.05 && Math.Abs(wave.Amplitudes[4] - 0.4) < 1e-9);
        Assert.Equal("?width=10", fake.Requests[^1].Url.Query);
    }
}

public class SiblingPlanningTests
{
    static AgentContext Context => new(100, 100, 1, false, "pixel", "Background");

    [Fact]
    public void DebugBuildsPairWithDebugBuilds()
    {
        Assert.Equal("com.brattoo.jammer.debug", SiblingApp.Jammer.BundleID(debug: true));
        Assert.Equal("com.brattoo.flimmer", SiblingApp.Flimmer.BundleID(debug: false));
        Assert.False(AppIdentity.IsDebugBuild, "the test host isn't a debug Glimmer");
    }

    [Fact]
    public void ToolNamesSplit()
    {
        Assert.Equal((SiblingApp.Jammer, "gain"), SiblingApp.Split("jammer_gain")!.Value);
        Assert.Equal((SiblingApp.Flimmer, "add_title"), SiblingApp.Split("flimmer_add_title")!.Value);
        Assert.True(SiblingApp.Split("jammer_") is null && SiblingApp.Split("add_waveform") is null);
    }

    [Fact]
    public void PlannerAcceptsOfferedSiblingTools()
    {
        var response = new TextToolReply("Louder, then the waveform.",
        [
            new("jammer_gain", """{"db": 3, "rationale": "Louder."}"""),
            new("add_waveform", """{"style": "bars"}"""),
            new("jammer_unoffered", "{}"),
        ], null);
        var plan = AgentPlanner.PlanFrom(response, new HashSet<string> { "jammer_gain" });
        Assert.Equal(["jammer_gain", "add_waveform"], plan.Steps.Select(s => s.Op));
        Assert.Equal(["jammer_unoffered"], plan.DroppedOps.ToArray());
        Assert.False(plan.UsesAI);
    }

    [Fact]
    public void AMadePlanReachesTheSiblings()
    {
        // The development hook's raw plan (no planner, no cost) keeps sibling steps.
        var plan = AgentPlanner.ParsePlan("""{"steps": [{"op": "jammer_echo", "params": {"delay": 0.3}}, {"op": "add_waveform", "params": {}}]}""");
        Assert.Equal(["jammer_echo", "add_waveform"], plan.Steps.Select(s => s.Op));
    }

    [Fact]
    public void SiblingContextReachesThePlanner()
    {
        var messages = AgentPlanner.ToolMessages("Make the cover", Context, [], siblingContext: "[Sibling apps: Jämmer is running.]");
        Assert.Contains("[Sibling apps: Jämmer is running.]", (string?)messages[^1]["content"]);
    }

    [Fact]
    public void AddWaveformIsAnOrdinaryFreeStep()
    {
        var op = AgentPlanner.Op("add_waveform")!;
        Assert.False(op.IsAIJob);
        Assert.Empty((JsonArray)AgentPlanner.ParamsSchema(op)["required"]!);
    }

    [Fact]
    public void WaveformPathFillsTheBox()
    {
        var wave = new SiblingWaveform([0, 0.5, 1, 0.25], "Episode 7");
        var rect = new CGRectLike(100, 200, 500, 300);
        var bars = wave.Path(rect, SiblingWaveform.Style.Bars);
        Assert.True(bars.Name == "Waveform – Episode 7" && bars.Subpaths.Length == 4);
        var all = bars.Subpaths.SelectMany(s => s.Anchors).Select(a => a.Point).ToList();
        Assert.True(all.Min(p => p.Y) == 200 && all.Max(p => p.Y) == 300, "the loudest bar spans the box");
        Assert.True(all.Min(p => p.X) >= 100 && all.Max(p => p.X) <= 500);
        // Silence keeps a sliver, so it still shows.
        var quiet = bars.Subpaths[0].Anchors.Select(a => a.Point.Y).ToList();
        Assert.True(quiet.Max() - quiet.Min() >= 1);
        var wavePath = wave.Path(rect, SiblingWaveform.Style.Wave);
        Assert.True(wavePath.Subpaths.Length == 1 && wavePath.Subpaths[0].IsClosed && wavePath.Subpaths[0].Anchors.Length == 2 + 2 * 4);
        Assert.NotNull(wavePath.AsSelection.Bounds(new PixelRect(0, 0, 600, 400)));
    }

    [Fact]
    public void JammersWaveformIsParsedLoudestFirst()
    {
        var data = Encoding.UTF8.GetBytes("""{"waveform": {"tracks": [{"channels": [[[-0.2, 0.1], [0, 0.5]], [[0, 0.3], [-0.9, 0]]]}, {"channels": [[[0, 2]]]}]}}""");
        var wave = SiblingWaveform.Parse(data, "X")!;
        Assert.Equal([1, 0.9], wave.Amplitudes);   // clamped to 1, the loudest per column
        Assert.Null(SiblingWaveform.Parse("{}"u8, "X"));
    }
}

/// <summary>What Glimmer sends to a sibling: a name the sibling's closed schema doesn't have is stopped here.</summary>
public class SiblingStrayParameterTests
{
    static SiblingOperation Op(bool closed)
    {
        var schema = new Dictionary<string, AgentValue>
        {
            ["type"] = "object",
            ["properties"] = AgentValue.Object(("seconds", AgentValue.Object(("type", "number"))), ("rationale", AgentValue.Object(("type", "string")))),
        };
        if (closed) schema["additionalProperties"] = false;
        return new SiblingOperation(SiblingApp.Flimmer, "trim_clip", "Trim.", AgentValue.Object(schema));
    }

    static Dictionary<string, AgentValue> P(params (string, AgentValue)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void AClosedSiblingSchemaIsCheckedBeforeAnythingIsSent()
    {
        var closed = Op(true);
        Assert.Null(closed.Stray(P(("seconds", 2))));
        var stray = closed.Stray(P(("seconds", 2), ("fade", true)));
        Assert.True(stray?.Name == "fade" && stray.Value.Allowed.SequenceEqual(["seconds"]));
        // An older sibling with an open schema decides for itself.
        Assert.Null(Op(false).Stray(P(("fade", true))));
        Assert.Equal("flimmer_trim_clip has no parameter named fade. It has: seconds.", SiblingError.StrayParameter("flimmer_trim_clip", "fade", ["seconds"]).Message);
    }

    static AgentValue Sort(params string[] keys) => AgentValue.Object(("type", "object"), ("additionalProperties", false),
        ("properties", AgentValue.Object(keys.Select(k => (k, AgentValue.Object(("type", "number")))).ToArray())));
    static readonly AgentValue Kinds = AgentValue.Object(("type", "string"), ("enum", AgentValue.Array([AgentValue.String("denoise"), AgentValue.String("declick")])));
    static readonly AgentValue Table = AgentValue.Object(("denoise", Sort("strength")), ("declick", Sort("sensitivity", "width")));

    /// <summary>Settings in an object of their own, the table on that property (Glimmer's own shape).</summary>
    [Fact]
    public void NestedSettingsPerSortAreChecked()
    {
        SiblingOperation Restore(bool named)
        {
            var settings = new Dictionary<string, AgentValue> { ["type"] = "object", ["x-byType"] = Table };
            if (named) settings["x-byTypeKey"] = "kind";
            return new SiblingOperation(SiblingApp.Jammer, "restore", "Restore.", AgentValue.Object(("type", "object"), ("additionalProperties", false),
                ("properties", AgentValue.Object(("kind", Kinds), ("settings", AgentValue.Object(settings))))));
        }
        var op = Restore(true);
        Assert.Null(op.Stray(P(("kind", "denoise"), ("settings", AgentValue.Object(("strength", 0.5))))));
        var stray = op.Stray(P(("kind", "denoise"), ("settings", AgentValue.Object(("sensitivity", 0.5)))));
        Assert.True(stray?.Name == "sensitivity in settings" && stray.Value.Allowed.SequenceEqual(["strength"]));
        Assert.Null(op.UncheckedTable(P(("kind", "denoise"), ("settings", AgentValue.Object()))));
        // A sort the table doesn't have is the sibling's to refuse.
        Assert.Null(op.Stray(P(("kind", "dehum"), ("settings", AgentValue.Object(("x", 1))))));
        // Without the chooser's name nothing is guessed: not checked, and that is known.
        var unnamed = Restore(false);
        Assert.Null(unnamed.Stray(P(("kind", "denoise"), ("settings", AgentValue.Object(("sensitivity", 0.5))))));
        Assert.Equal("settings", unnamed.UncheckedTable(P(("kind", "denoise"), ("settings", AgentValue.Object(("sensitivity", 0.5))))));
        Assert.Null(unnamed.UncheckedTable(P(("kind", "denoise"))));   // no settings given: nothing went unchecked
    }

    /// <summary>Settings beside the chooser at top level, the table on the operation's own schema (Jämmer's restore).</summary>
    [Fact]
    public void TopLevelSettingsPerSortAreChecked()
    {
        var schema = AgentValue.Object(("type", "object"), ("additionalProperties", false), ("x-byType", Table), ("x-byTypeKey", "kind"),
            ("properties", AgentValue.Object(("kind", Kinds), ("track", AgentValue.Object(("type", "string"))), ("strength", AgentValue.Object(("type", "number"))),
                                             ("sensitivity", AgentValue.Object(("type", "number"))), ("width", AgentValue.Object(("type", "number"))))));
        var op = new SiblingOperation(SiblingApp.Jammer, "restore", "Restore.", schema);
        Assert.Null(op.Stray(P(("kind", "denoise"), ("track", "a"), ("strength", 0.4))));
        // A setting of another sort: refused for this one, with what this sort has. `track` belongs to no sort and is always allowed.
        var stray = op.Stray(P(("kind", "denoise"), ("track", "a"), ("width", 2)));
        Assert.True(stray?.Name == "width for denoise" && stray.Value.Allowed.SequenceEqual(["kind", "strength", "track"]));   // what denoise does take
        Assert.Null(op.Stray(P(("kind", "declick"), ("sensitivity", 0.2), ("width", 2))));
        // A name the schema doesn't have at all is the ordinary refusal.
        Assert.Equal("amount", op.Stray(P(("kind", "denoise"), ("amount", 1)))?.Name);
    }

    /// <summary>A plan with a sibling step that carries an unknown name is known to be refused when it is made.</summary>
    [Fact]
    public void APlanIsNotRunAtOnceWhenItSaysAStepWillBeRefused()
    {
        var plan = new AgentPlan([new AgentStep("add_adjustment", ("type", "black_white"))]);
        Assert.True(plan.RunsAtOnce("Make it black and white"));
        plan = plan with { StrayParameters = ["flimmer_trim_clip has no parameter named fade. It has: seconds."] };
        Assert.False(plan.RunsAtOnce("Make it black and white"));
    }

    /// <summary>The tool Glimmer offers a model keeps the sibling's closed schema, with Glimmer's two additions inside it.</summary>
    [Fact]
    public void TheToolKeepsTheSiblingsSchemaClosed()
    {
        var parameters = (JsonObject)Op(true).ToolSchema["function"]!["parameters"]!;
        Assert.False(parameters["additionalProperties"]!.GetValue<bool>());
        var properties = (JsonObject)parameters["properties"]!;
        Assert.True(properties["document"] is not null && properties["rationale"] is not null && properties["seconds"] is not null);
    }

    /// <summary>A plan made from the planner carries a sibling step's stray name in its text and waits.</summary>
    [Fact]
    public async Task APlannedSiblingStepWithAStrayNameWaitsAndSaysSo()
    {
        var op = Op(true);
        var model = new ScriptedTextModel { Calls = [new("flimmer_trim_clip", """{"seconds": 2, "fade": true}""")], Text = "Trimmed." };
        var (plan, _) = await AgentPlanner.PlanAsync("Trim the clip by 2 seconds", new AgentContext(100, 100, 1, false, "pixel", "A"), [], model, siblings: [op]);
        Assert.Equal(["flimmer_trim_clip has no parameter named fade. It has: seconds."], plan.StrayParameters.ToArray());
        Assert.True(plan.StraySubject == "Flimmer" && plan.StrayName == "fade");
        Assert.Contains($"({StepParameters.StrayNote}: fade)", plan.Steps[0].Rationale);
        Assert.False(plan.RunsAtOnce("Trim the clip by 2 seconds"));
    }

    [Fact]
    public void ReadsARealSiblingsListWhenOneIsGiven()
    {
        var path = Environment.GetEnvironmentVariable("GLIMMER_SIBLING_OPS");
        if (path is null) return;   // GLIMMER_SIBLING_OPS=<a saved /api/v1/operations of Jämmer>
        var ops = SiblingOperation.Parse(File.ReadAllBytes(path), SiblingApp.Jammer);
        Assert.NotEmpty(ops);
        foreach (var name in new[] { "restore", "restore_preview" })
        {
            var op = ops.First(o => o.Name == name);
            string Verdict(params (string, AgentValue)[] p) => op.Stray(P(p)) is { } s ? $"refused: {s.Name}" : "passes";
            Assert.Equal("passes", Verdict(("kind", "denoise"), ("amount", 0.5)));
            Assert.NotEqual("passes", Verdict(("kind", "denoise"), ("sensitivity", 0.4)));
            Assert.NotEqual("passes", Verdict(("kind", "declick"), ("humFrequency", 50)));
            Assert.Null(op.UncheckedTable(P(("kind", "denoise"))));
        }
        Assert.All(ops, o => { Assert.Null(o.Stray(P())); Assert.Null(o.UncheckedTable(P())); });
        // The tools Glimmer would offer a model keep every schema closed.
        Assert.All(ops, o => Assert.Equal(false, (bool?)((JsonObject)o.ToolSchema["function"]!["parameters"]!)["additionalProperties"]));
    }
}

/// <summary>A visiting test's copy (<c>….debug.guest</c>) is a debug build, and debug builds accept each other's guest copies; a release never does.</summary>
public class GuestCopyTests
{
    [Fact]
    public void GuestCopiesAreDebugAndOnlyDebugAcceptsThem()
    {
        Assert.True(AppIdentity.IsDebug("com.brattoo.glimmer.debug") && AppIdentity.IsDebug("com.brattoo.glimmer.debug.guest") && AppIdentity.IsDebug("com.brattoo.glimmer.offline.debug"));
        Assert.True(!AppIdentity.IsDebug("com.brattoo.glimmer") && !AppIdentity.IsDebug("com.brattoo.glimmer.guest"));
        foreach (var app in SiblingApp.AllCases)
        {
            var id = "com.brattoo." + app.RawValue();
            Assert.True(app.Accepts(id + ".debug", debug: true) && app.Accepts(id + ".debug.guest", debug: true));
            Assert.False(app.Accepts(id, debug: true));   // debug talks to debug
            Assert.True(app.Accepts(id, debug: false));
            Assert.True(!app.Accepts(id + ".debug.guest", debug: false) && !app.Accepts(id + ".debug", debug: false));
            Assert.False(app.Accepts(null, debug: true));
        }
    }
}

/// <summary>Live against a running Jämmer with a document named "Episode 7" (GLIMMER_LIVE_SIBLINGS=1; never in CI). Changes that document: an echo, which one undo in Jämmer takes back. There is no Jämmer on Windows.</summary>
public class LiveSiblingTests
{
    [Fact]
    public async Task RealJammer()
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_LIVE_SIBLINGS") != "1") return;
        var client = new SiblingClient();
        Assert.Contains(SiblingApp.Jammer, await client.RunningAsync());
        var (tools, context) = await client.ToolboxAsync();
        Assert.True(tools.Any(t => t.ToolName == "jammer_echo") && context!.Contains("Episode 7"));
    }
}
