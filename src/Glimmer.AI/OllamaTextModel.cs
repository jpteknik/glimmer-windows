// Ported from Packages/GlimmerKit/Sources/GlimmerAI/OllamaTextModel.swift
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// Text from a model in Ollama on this Mac, through Ollama's own chat API (`/api/chat`).
/// Its OpenAI-style route can't set the context size ("The OpenAI API does not have a way of
/// setting the context size for a model"), and with the default of a few thousand tokens
/// Glimmer's tool list doesn't fit, so the request states the context it needs.
///
/// Nothing is sent off the Mac and no key is involved. A local model is not offered until
/// one has passed `LocalModelEvaluation`.
public sealed class OllamaTextModel : ITextModel
{
    public static readonly Uri DefaultBaseUrl = new("http://127.0.0.1:11434");

    public Uri BaseUrl { get; }
    public string ModelID { get; }
    /// Tokens of context asked for: room for the tool list, a picture and the answer.
    public int ContextTokens { get; }
    /// Whether the model may reason before it answers (slower; off by default, as measured in docs/TESTING-NOTES.md).
    public bool Thinks { get; }
    private readonly HttpClient http;

    public string ProviderName => "Ollama";
    /// Small local models lose their way in a long tool list: they get the everyday operations only.
    public bool PrefersShortToolList => true;

    /// `baseUrl` is the server's address, with or without the `/v1` of its OpenAI-style route.
    public OllamaTextModel(string model, Uri? baseUrl = null, int contextTokens = 16_384, bool thinks = false, HttpMessageHandler? handler = null)
    {
        Thinks = thinks;
        BaseUrl = Root(baseUrl ?? DefaultBaseUrl);
        ModelID = model;
        ContextTokens = contextTokens;
        http = KeyStaysHome.CreateClient(handler);
    }

    private static Uri Root(Uri baseUrl) => Http.LastPathComponent(baseUrl) == "v1" ? Http.DeletingLastPathComponent(baseUrl) : baseUrl;

    private static string HostOf(Uri url) => url.Host.Length > 0 ? url.Host : "this computer";
    private static int PortOf(Uri url) => url.IsDefaultPort ? 11434 : url.Port;

    public async Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                                            CancellationToken ct = default)
    {
        var messages = new JsonArray();
        if (system is not null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        var message = new JsonObject { ["role"] = "user", ["content"] = user };
        if (images.Count > 0)
        {
            var encoded = new JsonArray();
            foreach (var image in images) encoded.Add(Convert.ToBase64String(image));
            message["images"] = encoded;
        }
        messages.Add(message);
        var body = Body(messages, temperature);
        if (json) body["format"] = "json";
        var answer = await SendAsync(body, ct).ConfigureAwait(false);
        return new TextReply(Json.Str(answer["content"]) ?? "", null);
    }

    public async Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                                    Action<string>? onText, CancellationToken ct = default)
    {
        var body = Body(Json.Array(Native(messages)), temperature);
        body["tools"] = Json.Array(tools);
        var answer = await SendAsync(body, ct).ConfigureAwait(false);
        var text = Json.Str(answer["content"]);
        if (!string.IsNullOrEmpty(text)) onText?.Invoke(text);
        // Ollama gives the arguments as an object; the rest of Glimmer reads them as a JSON string.
        var calls = new List<TextToolCall>();
        foreach (var call in Json.Objects(answer["tool_calls"]) ?? new List<JsonObject>())
        {
            if (Json.Obj(call["function"]) is not { } function || Json.Str(function["name"]) is not { } name) continue;
            var arguments = function["arguments"];
            string argumentText;
            if (Json.Str(arguments) is { } s) argumentText = s;
            else if (arguments is JsonObject or JsonArray) argumentText = Json.SerializeSorted(arguments);
            else argumentText = "{}";
            calls.Add(new TextToolCall(name, argumentText));
        }
        return new TextToolReply(string.IsNullOrEmpty(text) ? null : text, calls, null);
    }

    private JsonObject Body(JsonArray messages, double temperature) => new()
    {
        ["model"] = ModelID,
        ["messages"] = messages,
        ["stream"] = false,
        ["think"] = Thinks,
        ["options"] = new JsonObject { ["num_ctx"] = ContextTokens, ["temperature"] = temperature },
    };

    /// The common chat form to Ollama's: text in `content`, pictures as base64 in `images`.
    internal static List<JsonObject> Native(IReadOnlyList<JsonObject> messages)
    {
        var result = new List<JsonObject>();
        foreach (var message in messages)
        {
            if (Json.Objects(message["content"]) is not { } parts)
            {
                result.Add(message);
                continue;
            }
            var output = (JsonObject)message.DeepClone();
            var text = new List<string>();
            var images = new JsonArray();
            foreach (var part in parts)
            {
                if (Json.Str(part["text"]) is { } t) text.Add(t);
                if (Json.Str(Json.Obj(part["image_url"])?["url"]) is { } url && url.StartsWith("data:", StringComparison.Ordinal) && url.IndexOf(',') is var comma and >= 0)
                    images.Add(url[(comma + 1)..]);
            }
            output["content"] = string.Join("\n", text);
            if (images.Count > 0) output["images"] = images;
            result.Add(output);
        }
        return result;
    }

    /// The `message` of the answer.
    private async Task<JsonObject> SendAsync(JsonObject body, CancellationToken ct)
    {
        var request = new RequestSpec(Http.Append(BaseUrl, "api/chat"), "POST");
        // A local model can be slow; nothing is billed while it thinks.
        request.Timeout = TimeSpan.FromSeconds(600);
        request.Body = Json.Bytes(body);
        HttpData data;
        try
        {
            data = await Http.DataAsync(http, request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct))
        {
            throw new OllamaError($"Glimmer couldn't reach Ollama at {HostOf(BaseUrl)}:{PortOf(BaseUrl)}. Is Ollama running? ({Http.Describe(error)})");
        }
        if (data.Status is < 200 or >= 300)
            throw new ProviderHttpError("Ollama", data.Status, ProviderHttpError.MessageFrom(data.Body));
        if (Json.ParseObject(data.Body) is not { } json || Json.Obj(json["message"]) is not { } message)
            throw new OllamaError("Ollama's answer wasn't in the form Glimmer expects.");
        return message;
    }

    public sealed class OllamaError : Exception, IEquatable<OllamaError>
    {
        public string Detail { get; }
        public OllamaError(string message) : base(message) { Detail = message; }
        public override string Message => Detail;
        public bool Equals(OllamaError? other) => other is not null && Detail == other.Detail;
        public override bool Equals(object? obj) => Equals(obj as OllamaError);
        public override int GetHashCode() => Detail.GetHashCode();
    }

    /// Asks Ollama to let go of `model` now (`keep_alive: 0`), for tests and evaluations: left
    /// loaded it holds several GB for minutes. The app itself keeps Ollama's own timing.
    public static async Task UnloadAsync(string model, Uri? baseUrl = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var request = new RequestSpec(Http.Append(baseUrl ?? DefaultBaseUrl, "api/generate"), "POST")
        {
            Body = Json.Bytes(new JsonObject { ["model"] = model, ["keep_alive"] = 0 }),
            Timeout = TimeSpan.FromSeconds(20),
        };
        try { await Http.DataAsync(KeyStaysHome.CreateClient(handler), request, ct).ConfigureAwait(false); }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct)) { /* `try?` */ }
    }

    /// What Ollama says a model can do (`/api/show`): tools and pictures.
    public static async Task<(bool Tools, bool Vision, int? ContextLength)> AbilitiesAsync(string model, Uri? baseUrl = null,
                                                                                           HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var request = new RequestSpec(Http.Append(baseUrl ?? DefaultBaseUrl, "api/show"), "POST")
        {
            Body = Json.Bytes(new JsonObject { ["model"] = model }),
            Timeout = TimeSpan.FromSeconds(20),
        };
        var data = await Http.DataAsync(KeyStaysHome.CreateClient(handler), request, ct).ConfigureAwait(false);
        var json = Json.ParseObject(data.Body) ?? new JsonObject();
        var caps = Json.Strings(json["capabilities"]) ?? new List<string>();
        var info = Json.Obj(json["model_info"]) ?? new JsonObject();
        var context = info.FirstOrDefault(p => p.Key.EndsWith(".context_length", StringComparison.Ordinal)).Value;
        return (caps.Contains("tools"), caps.Contains("vision"), Json.Int(context));
    }

    /// A model installed in Ollama, with what Ollama says it can do.
    public sealed record Installed(string Name, bool Tools, bool Vision,
                                   /// Bytes on disk, roughly what it takes in memory while it runs.
                                   long? Size)
    {
        public string Id => Name;
    }

    /// The models installed in the Ollama at `baseUrl` (`/api/tags`, then `/api/show` for each).
    /// Throws when no server answers there: Ollama is then not offered at all.
    public static async Task<List<Installed>> InstalledAsync(Uri? baseUrl = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var root = Root(baseUrl ?? DefaultBaseUrl);
        var request = new RequestSpec(Http.Append(root, "api/tags")) { Timeout = TimeSpan.FromSeconds(3) };
        var data = await Http.DataAsync(KeyStaysHome.CreateClient(handler), request, ct).ConfigureAwait(false);
        var list = data.Status == 200 && Json.ParseObject(data.Body) is { } json ? Json.Objects(json["models"]) : null;
        if (list is null)
            throw new OllamaError($"No Ollama answered at {HostOf(root)}:{PortOf(root)}.");
        var models = new List<Installed>();
        foreach (var entry in list)
        {
            var name = Json.Str(entry["name"]) ?? Json.Str(entry["model"]);
            if (name is null) continue;
            (bool Tools, bool Vision, int? ContextLength)? abilities = null;
            try { abilities = await AbilitiesAsync(name, root, handler, ct).ConfigureAwait(false); }
            catch (Exception error) when (Http.IsConnectionFailure(error, ct)) { /* `try?` */ }
            long? size = entry["size"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
            models.Add(new Installed(name, abilities?.Tools ?? false, abilities?.Vision ?? false, size));
        }
        return models.OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }
}
