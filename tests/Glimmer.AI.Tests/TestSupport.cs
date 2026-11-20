// New in the port. The original's tests used a URLProtocol (`FakeProvider`) and a network guard;
// here every test passes a fake HttpMessageHandler to the code under test, so nothing can reach a
// real service, and the live tests are skipped unless GLIMMER_LIVE_AI=1.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

/// A made-up provider: answers in the form the service's documentation shows, and records what it was sent.
/// No sockets, no keys, no money.
internal sealed class FakeProvider : HttpMessageHandler
{
    public sealed record Answer(int Status, string Body, IReadOnlyDictionary<string, string>? Headers = null, Exception? Throw = null);

    public sealed record Seen(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, JsonObject Json, byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    private readonly object gate = new();
    private readonly Queue<Answer> answers = new();
    private readonly List<Seen> seen = new();

    public FakeProvider() { }

    public FakeProvider(params (int, string)[] answers) { Script(answers); }

    public FakeProvider(params Answer[] answers) { Script(answers); }

    public void Script(params (int, string)[] list)
    {
        lock (gate)
        {
            seen.Clear();
            answers.Clear();
            foreach (var (status, body) in list) answers.Enqueue(new Answer(status, body));
        }
    }

    public void Script(params Answer[] list)
    {
        lock (gate)
        {
            seen.Clear();
            answers.Clear();
            foreach (var answer in list) answers.Enqueue(answer);
        }
    }

    public IReadOnlyList<Seen> Requests { get { lock (gate) return seen.ToList(); } }
    public Seen First => Requests.First();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(", ", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        var json = body.Length > 0 && TJson.TryObject(body) is { } o ? o : new JsonObject();
        Answer answer;
        lock (gate)
        {
            seen.Add(new Seen(request.Method, request.RequestUri!, headers, json, body));
            answer = answers.Count == 0 ? new Answer(500, "{}") : answers.Dequeue();
        }
        if (answer.Throw is not null) throw answer.Throw;
        var response = new HttpResponseMessage((HttpStatusCode)answer.Status);
        // "base64:…" stands for binary content (a picture to download).
        var payload = answer.Body.StartsWith("base64:", StringComparison.Ordinal) ? Convert.FromBase64String(answer.Body[7..]) : Encoding.UTF8.GetBytes(answer.Body);
        response.Content = new ByteArrayContent(payload);
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (answer.Headers is not null)
            foreach (var h in answer.Headers) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return response;
    }
}

internal static class TJson
{
    public static JsonObject? TryObject(byte[] data)
    {
        try { return JsonNode.Parse(data) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
    }

    public static JsonObject Obj(string text) => (JsonObject)JsonNode.Parse(text)!;
}

/// A test that runs only when GLIMMER_LIVE_AI=1 (real OpenRouter calls; each run costs a few cents).
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_LIVE_AI") != "1")
            Skip = "Real OpenRouter calls: set GLIMMER_LIVE_AI=1 and OPENROUTER_API_KEY to run them.";
    }
}

/// A plan step for the plan checks: an operation with text parameters.
internal sealed class TestStep : IPlanStep
{
    public string Op { get; }
    private readonly Dictionary<string, string> parameters;

    public TestStep(string op, params (string, string)[] parameters)
    {
        Op = op;
        this.parameters = parameters.ToDictionary(p => p.Item1, p => p.Item2);
    }

    public string? StringParam(string key) => parameters.TryGetValue(key, out var v) ? v : null;
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glimmer-ai-test-" + Guid.NewGuid().ToString("N"));

    public TempFolder() { Directory.CreateDirectory(Path); }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// A few operations in the Assistant's form, for the agent API and the guide (the real list comes with the Agent port).
internal sealed class FakeCatalog : IAgentOperationCatalog
{
    private static JsonObject Schema(JsonObject properties, params string[] required)
    {
        properties["rationale"] = new JsonObject { ["type"] = "string", ["description"] = "Optional. Why." };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    public IReadOnlyList<AgentOperation> Operations { get; } = new List<AgentOperation>
    {
        new("add_adjustment", "Add a non-destructive adjustment layer.",
            Schema(new JsonObject { ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("black_white", "exposure") } }, "type"),
            new[] { new AgentOperationParam("type", "string", "The kind.", new[] { "black_white", "exposure" }) }),
        new("auto_enhance", "Automatic tone and color.", Schema(new JsonObject()), Array.Empty<AgentOperationParam>()),
        new("crop", "Crop the image.", Schema(new JsonObject { ["aspect"] = new JsonObject { ["type"] = "string" } }),
            new[] { new AgentOperationParam("aspect", "string", "Optional. The shape, like 16:9 | 4:3.") }),
        new("generate_layer", "Make a picture with AI.", Schema(new JsonObject { ["prompt"] = new JsonObject { ["type"] = "string" } }, "prompt"),
            new[] { new AgentOperationParam("prompt", "string", "What to make.") }, IsAIJob: true, StartsJob: true),
        new("run_command", "Run an editor command.", Schema(new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } }, "id"),
            new[] { new AgentOperationParam("id", "string", "The command id.") }),
        new("develop_raw", "Develop a RAW layer.", Schema(new JsonObject()), Array.Empty<AgentOperationParam>()),
        new("heal_selection", "Fill the selection from its surroundings.", Schema(new JsonObject()), Array.Empty<AgentOperationParam>()),
    };

    public IReadOnlyList<RunnableCommand> RunnableCommands { get; } = new[] { new RunnableCommand("layer.new", "New Layer…") };
    public JsonObject RationaleSchema { get; } = new() { ["type"] = "string", ["description"] = "Optional. Why." };
    public IReadOnlyList<string> FitValues { get; } = new[] { "contain", "cover", "none" };
    public string StrayExample { get; } = "crop has no parameter widht (did you mean width?).";
    public string NestedStrayExample { get; } = "add_adjustment exposure has no setting brightness.";
}
