// New in the port (no Swift counterpart): the idle timeout of streamed answers (URLSession's timeoutInterval counted from the last byte; the
// first port counted nothing), the saved actions' storage, the operation catalog the agent API is built from, the Windows wording of the guide,
// and the two tests of CanvasStepsTests/AgentAPITests that need the real operation list.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

/// <summary>A response body that hands out its lines on a schedule and then either ends or stalls for ever.</summary>
sealed class ScheduledStream(IReadOnlyList<(TimeSpan Wait, string Text)> pieces, bool stallAtEnd) : Stream
{
    int next;
    byte[] pending = [];
    int offset;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (offset >= pending.Length)
        {
            if (next >= pieces.Count)
            {
                if (stallAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var (wait, text) = pieces[next++];
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            pending = Encoding.UTF8.GetBytes(text);
            offset = 0;
        }
        int n = Math.Min(buffer.Length, pending.Length - offset);
        pending.AsMemory(offset, n).CopyTo(buffer);
        offset += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int o, int count) => throw new NotSupportedException();
}

/// <summary>Answers each request with the next scripted response (a streamed body, or an ordinary one).</summary>
sealed class StreamingHandler(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
{
    int next;
    public int Calls => next;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var answer = answers[Math.Min(next++, answers.Length - 1)];
        return Task.FromResult(answer());
    }

    public static Func<HttpResponseMessage> Stream(Stream body) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        return response;
    };

    public static Func<HttpResponseMessage> Json(string body) => () =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public class IdleTimeoutTests
{
    static string Chunk(string text) => "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }.ToJsonString() + "\n\n";
    static readonly JsonObject User = new() { ["role"] = "user", ["content"] = "x" };
    static readonly JsonObject Tool = TJson.Obj("""{"type":"function","function":{"name":"add_adjustment","parameters":{"type":"object"}}}""");
    const string Whole = """{"choices":[{"message":{"role":"assistant","content":"Whole answer."}}]}""";

    static OpenRouterClient Client(StreamingHandler handler, double idleSeconds) =>
        new("sk", handler: handler) { RequestTimeout = TimeSpan.FromSeconds(idleSeconds) };

    [Fact]
    public async Task AStreamThatStallsAfterSomeTextEndsWithANetworkErrorAtTheIdleTimeout()
    {
        var body = new ScheduledStream([(TimeSpan.Zero, Chunk("Black "))], stallAtEnd: true);
        var heard = new List<string>();
        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<OpenRouterError>(() =>
            Client(new StreamingHandler(StreamingHandler.Stream(body)), 0.25).ToolCompletionStreamingAsync("m", [User], [Tool], heard.Add));
        Assert.Equal(OpenRouterErrorKind.Network, error.Kind);
        Assert.Equal(["Black "], heard);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.2, 5);
    }

    [Fact]
    public async Task AStreamThatSendsNothingFallsBackToTheOrdinaryCall()
    {
        var stalled = new ScheduledStream([], stallAtEnd: true);
        var handler = new StreamingHandler(StreamingHandler.Stream(stalled), StreamingHandler.Json(Whole));
        var reply = await Client(handler, 0.25).ToolCompletionStreamingAsync("m", [User], [Tool], _ => { });
        Assert.Equal("Whole answer.", reply.Text);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>The timeout counts from the last byte, not from the start: a slow answer that keeps arriving is not cut off.</summary>
    [Fact]
    public async Task ASlowStreamThatKeepsArrivingIsNotCutOff()
    {
        var pieces = Enumerable.Range(0, 6).Select(i => (TimeSpan.FromMilliseconds(90), Chunk($"{i} "))).Append((TimeSpan.Zero, "data: [DONE]\n\n")).ToList();
        var reply = await Client(new StreamingHandler(StreamingHandler.Stream(new ScheduledStream(pieces, stallAtEnd: false))), 0.3)
            .ToolCompletionStreamingAsync("m", [User], [Tool], _ => { });
        Assert.Equal("0 1 2 3 4 5 ", reply.Text);   // 540 ms in all, more than the 300 ms timeout
    }

    [Fact]
    public async Task TheLineReaderGivesUpOnAStalledStreamWithATimeoutAndLeavesACallersCancelAlone()
    {
        var stalled = new ScheduledStream([(TimeSpan.Zero, "data: one\n")], stallAtEnd: true);
        var lines = new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var line in Http.LinesAsync(stalled, CancellationToken.None, TimeSpan.FromMilliseconds(150))) lines.Add(line);
        });
        Assert.Equal(["data: one"], lines);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Http.LinesAsync(new ScheduledStream([], stallAtEnd: true), cts.Token, TimeSpan.FromSeconds(30))) { }
        });
    }

    [Fact]
    public async Task ServerSentEventsReadersAreCutOffToo()
    {
        var body = new ScheduledStream([(TimeSpan.Zero, "data: {\"type\":\"x\"}\n\n")], stallAtEnd: true);
        var http = KeyStaysHome.CreateClient(new StreamingHandler(StreamingHandler.Stream(body)));
        var request = new RequestSpec(new Uri("https://api.example/v1/messages"), "POST") { Timeout = TimeSpan.FromMilliseconds(200), Body = [123, 125] };
        var events = new List<JsonObject>();
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => ServerSentEvents.ReadAsync(http, request, "Anthropic", events.Add, CancellationToken.None));
        Assert.Equal(OpenRouterErrorKind.Network, error.Kind);
        Assert.Single(events);
    }
}

public class SavedActionTests
{
    [Fact]
    public void SavedActionsRoundTripThroughTheStoreAsTheMacWroteThem()
    {
        var store = new InMemorySavedActionStore();
        Assert.Empty(SavedActions.Load(store));
        var action = new SavedAction("Warm look", [new AgentStep("add_adjustment", ("type", "photo_filter"), ("params", AgentValue.Object(("density", 0.3)))) { }, new AgentStep("crop", ("aspect", "4:5"))]);
        SavedActions.Store(new[] { action }, store);
        var json = JsonNode.Parse(store.Load()!)!.AsArray()[0]!;
        Assert.Equal(action.Id.ToString("D").ToUpperInvariant(), (string?)json["id"]);   // a UUID is written in capitals
        Assert.Equal("Warm look", (string?)json["name"]);
        Assert.Equal("crop", (string?)json["steps"]![1]!["op"]);
        var back = SavedActions.Load(store);
        Assert.True(back.Count == 1 && back[0].Id == action.Id && back[0].Name == "Warm look");
        Assert.Equal(["add_adjustment", "crop"], back[0].Steps.Select(s => s.Op));
        Assert.Equal(0.3, back[0].Steps[0].Object("params")["density"].AsNumber);
    }

    [Fact]
    public void ABrokenStoreIsAnEmptyList()
    {
        var store = new InMemorySavedActionStore();
        store.Store("not json"u8.ToArray());
        Assert.Empty(SavedActions.Load(store));
        store.Store("""[{"name":"x"}]"""u8.ToArray());
        Assert.Empty(SavedActions.Load(store));
    }
}

public class OperationCatalogTests
{
    static IReadOnlyList<JsonObject> Operations => AgentAPI.Operations(AgentOperationCatalog.Instance);

    [Fact]
    public void TheCatalogIsTheAssistantsRegister()
    {
        var catalog = AgentOperationCatalog.Instance;
        Assert.Equal(AgentPlanner.Ops.Select(o => o.Name), catalog.Operations.Select(o => o.Name));
        Assert.Equal(AgentPlanner.Ops.Where(o => o.IsAIJob).Select(o => o.Name).Order(), AgentAPI.PaidOps(catalog).Order());
        Assert.True(catalog.Operations.Where(o => o.IsAIJob).All(o => o.StartsJob));
        Assert.Equal(new[] { "none", "contain", "cover" }.Order(), catalog.FitValues.Order());
        Assert.Equal("crop has no parameter named widht. It has: x, y, width, height, toSelection, aspect, deletePixels.", catalog.StrayExample);
        Assert.StartsWith("add_adjustment has no parameter named brightness in params. It has: exposure, offset, gamma", catalog.NestedStrayExample);
        Assert.Equal(AgentPlanner.RunnableCommands.Count, catalog.RunnableCommands.Count);
        Assert.DoesNotContain(catalog.RunnableCommands, c => c.Id == "layer.style" || c.Id.StartsWith("layer.adjustment."));
        Assert.Contains(catalog.RunnableCommands, c => c.Id == "layer.duplicate" || c.Id.StartsWith("select."));
    }

    [Fact]
    public void TheGuideAndTheOperationListComeFromIt()
    {
        var guide = AgentAPI.Guide(9001, "1", AgentOperationCatalog.Instance);
        foreach (var op in AgentPlanner.Ops.Where(o => o.Name != "develop_raw" || AgentAPI.HasRAW).Where(o => o.Name != "heal_selection" || AgentAPI.HasHeal))
            Assert.Contains($"### `{op.Name}`", guide);
        Assert.Contains("crop has no parameter named widht.", guide);
        Assert.Contains(AgentPlanner.RunnableCommands[0].Id.RawValue, guide);
    }

    [Fact]
    public void TheOperationsListHasTheNewOnes()
    {
        var ops = Operations;
        JsonObject Op(string name) => ops.First(o => (string?)o["name"] == name);
        foreach (var name in new[] { "crop", "resize_image", "set_layer", "add_image" }) Assert.Equal("core", (string?)Op(name)["tier"]);
        Assert.Equal("advanced", (string?)Op("select_rect")["tier"]);
        var add = Op("add_image");
        Assert.True((string?)add["call"]!["method"] == "POST" && (string?)add["call"]!["path"] == "/api/v1/documents/{id}/layers");
        Assert.True(add["undoable"]!.GetValue<bool>() && !add["startsJob"]!.GetValue<bool>());
        var props = (JsonObject)add["params"]!["properties"]!;
        foreach (var q in new[] { "name", "fit", "x", "y", "smartObject" }) Assert.Equal("query", (string?)props[q]!["x-in"]);
        Assert.Empty((JsonArray)Op("crop")["params"]!["required"]!);
        Assert.True(((JsonArray)Op("select_rect")["params"]!["required"]!).Select(n => (string?)n).ToHashSet().SetEquals(new[] { "x", "y", "width", "height" }));
    }

    [Fact]
    public void TheSchemaTakesCornersAndAPermission()
    {
        var op = Operations.First(o => (string?)o["name"] == "transform_layer");
        Assert.True(!op["paid"]!.GetValue<bool>() && !op["startsJob"]!.GetValue<bool>() && (string?)op["tier"] == "advanced");
        var props = (JsonObject)op["params"]!["properties"]!;
        Assert.Equal("array", (string?)props["corners"]!["type"]);
        Assert.Equal("array", (string?)props["corners"]!["items"]!["type"]);
        Assert.Equal("boolean", (string?)props["allowRasterize"]!["type"]);
        Assert.Empty((JsonArray)op["params"]!["required"]!);   // every parameter is optional
    }

    /// <summary>What states a Mac fact in a text the person or an agent reads is a bug on Windows (2026-11-19, the Windows port).</summary>
    [Fact]
    public void NoTextTheGuideOrTheOperationsStatesAMacFact()
    {
        var texts = new List<string> { AgentAPI.Guide(9001, "1", AgentOperationCatalog.Instance, heal: true, psd: true, raw: true), AgentPlanner.SystemPrompt(), AgentPlanner.ToolPreamble, AgentPlanner.ReviewPreamble };
        texts.AddRange(Operations.Select(o => o.ToJsonString()));
        foreach (var text in texts)
            Assert.DoesNotMatch(@"\bMacs?\b|macOS|~/Library|/Applications|⌘|Finder|Keychain", text);
        Assert.All(AIProvider.All, p => Assert.DoesNotMatch(@"\bMacs?\b", (p.TermsNote ?? "") + (p.DataNote ?? "")));
        Assert.DoesNotMatch(@"\bMacs?\b|Library/", AIProvider.ManualTable());
    }

    [Fact]
    public void TheModelsFolderIsWhereWindowsKeepsGlimmersData()
    {
        Assert.Equal(@"%LOCALAPPDATA%\Glimmer\Models", Requirement.ModelsFolder);
        Assert.StartsWith(Requirement.ModelsFolder, Requirement.DepthAnything.Location);
        Assert.Contains(@"%LOCALAPPDATA%\Programs\Ollama", Requirement.Ollama.Location);
        Assert.DoesNotContain("/Applications", Requirement.Ollama.Removal);
    }
}
