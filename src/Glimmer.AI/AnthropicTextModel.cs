// Ported from Packages/GlimmerKit/Sources/GlimmerAI/AnthropicTextModel.swift
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// Text from Claude through Anthropic's Messages API (`POST /v1/messages`), in its own form:
/// there is no official SDK in use here, so this is the documented HTTP request.
///
/// What the current models refuse is not sent: no temperature (an error on Claude 4.7 and
/// later), no forced tool choice, no thinking setting (the model's default applies). JSON
/// answers are asked for in the prompt and parsed, as with the other services. The reply
/// arrives whole; `onText` gets it once.
public sealed class AnthropicTextModel : ITextModel
{
    public const string ApiVersion = "2026-10-21";

    public string ModelID { get; }
    public string ProviderName => "Anthropic";
    private readonly Uri baseUrl;
    private readonly string key;
    private readonly HttpClient http;

    public AnthropicTextModel(AIProvider provider, string key, string model, HttpMessageHandler? handler = null)
    {
        baseUrl = new Uri(provider.BaseUrl);
        this.key = key;
        ModelID = model;
        http = KeyStaysHome.CreateClient(handler);
    }

    /// A declined request: Claude answers 200 with `stop_reason: "refusal"`.
    public sealed class Refusal : Exception, IEquatable<Refusal>
    {
        public string? Category { get; }
        public Refusal(string? category) { Category = category; }

        public override string Message => "Claude declined this request" + (Category is { } c ? $" ({c})" : "") + ". Nothing was changed.";

        public bool Equals(Refusal? other) => other is not null && Category == other.Category;
        public override bool Equals(object? obj) => Equals(obj as Refusal);
        public override int GetHashCode() => Category?.GetHashCode() ?? 0;
    }

    public async Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                                            CancellationToken ct = default)
    {
        var content = new JsonArray();
        foreach (var image in images) content.Add(ImageBlock(imageType, Convert.ToBase64String(image)));
        content.Add(new JsonObject { ["type"] = "text", ["text"] = user });
        var body = new JsonObject
        {
            ["model"] = ModelID,
            ["max_tokens"] = 16_000,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
        };
        if (system is not null) body["system"] = system;
        return new TextReply(TextIn(await SendAsync(body, ct).ConfigureAwait(false)), null);
    }

    public async Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                                    Action<string>? onText, CancellationToken ct = default)
    {
        var (system, turns) = Conversation(messages);
        var body = new JsonObject
        {
            ["model"] = ModelID,
            ["max_tokens"] = 16_000,
            ["messages"] = Json.Array(turns),
            ["tools"] = Json.Array(Tools(tools)),
        };
        if (system is not null) body["system"] = system;
        List<JsonObject> blocks;
        if (onText is not null)
        {
            // The Assistant shows the text as it comes: the same request with `stream`, put together from its events.
            body["stream"] = true;
            var stream = new AnthropicStream();
            var name = ProviderName;
            await ServerSentEvents.ReadAsync(http, Request(body), name, @event =>
            {
                if (stream.Consume(@event, name)) onText(stream.Text);
            }, ct).ConfigureAwait(false);
            if (stream.StopReason == "refusal") throw new Refusal(stream.StopCategory);
            blocks = stream.Content;
        }
        else
        {
            blocks = await SendAsync(body, ct).ConfigureAwait(false);
        }
        var text = TextIn(blocks);
        var calls = new List<TextToolCall>();
        foreach (var block in blocks)
        {
            if (Json.Str(block["type"]) != "tool_use" || Json.Str(block["name"]) is not { } name) continue;
            // The input is an object; the rest of Glimmer reads arguments as a JSON string.
            var input = block["input"];
            var arguments = input is JsonObject or JsonArray ? Json.SerializeSorted(input) : "{}";
            if (input is null) arguments = "{}";
            calls.Add(new TextToolCall(name, arguments));
        }
        return new TextToolReply(text.Length == 0 ? null : text, calls, null);
    }

    internal static JsonObject ImageBlock(string mediaType, string base64) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 },
    };

    /// Chat-form tools to Anthropic's: `name`, `description`, `input_schema`.
    internal static List<JsonObject> Tools(IReadOnlyList<JsonObject> chat)
    {
        var result = new List<JsonObject>();
        foreach (var tool in chat)
        {
            if (Json.Obj(tool["function"]) is not { } f || Json.Str(f["name"]) is not { } name) continue;
            var schema = f["parameters"]?.DeepClone()
                ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            var output = new JsonObject { ["name"] = name, ["input_schema"] = schema };
            if (f["description"] is { } d) output["description"] = d.DeepClone();
            result.Add(output);
        }
        return result;
    }

    /// Chat-form messages to a top-level system prompt and user/assistant turns; a picture's
    /// data URL becomes a base64 image block.
    internal static (string? System, List<JsonObject> Turns) Conversation(IReadOnlyList<JsonObject> messages)
    {
        var system = new List<string>();
        var turns = new List<JsonObject>();
        foreach (var message in messages)
        {
            var role = Json.Str(message["role"]) ?? "user";
            if (role == "system")
            {
                if (Json.Str(message["content"]) is { } text) system.Add(text);
                continue;
            }
            if (Json.Objects(message["content"]) is not { } parts)
            {
                turns.Add(new JsonObject { ["role"] = role, ["content"] = message["content"]?.DeepClone() ?? "" });
                continue;
            }
            var blocks = new JsonArray();
            foreach (var part in parts)
            {
                if (Json.Str(part["text"]) is { } t)
                {
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = t });
                }
                else if (Json.Str(Json.Obj(part["image_url"])?["url"]) is { } url && url.StartsWith("data:", StringComparison.Ordinal))
                {
                    var semicolon = url.IndexOf(';');
                    var comma = url.IndexOf(',');
                    if (semicolon >= 5 && comma > semicolon)
                        blocks.Add(ImageBlock(url[5..semicolon], url[(comma + 1)..]));
                }
            }
            turns.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }
        return (system.Count == 0 ? null : string.Join("\n\n", system), turns);
    }

    internal static string TextIn(IEnumerable<JsonObject> blocks) =>
        string.Concat(blocks.Where(b => Json.Str(b["type"]) == "text").Select(b => Json.Str(b["text"])).Where(t => t is not null));

    internal static List<KeyValuePair<string, string>> Headers(string key) => new()
    {
        new("x-api-key", key),
        new("anthropic-version", ApiVersion),
        new("Content-Type", "application/json"),
    };

    private RequestSpec Request(JsonObject body)
    {
        if (key.Length == 0) throw OpenRouterError.MissingAPIKey();
        var request = new RequestSpec(Http.Append(baseUrl, "messages"), "POST");
        foreach (var header in Headers(key))
        {
            if (header.Key == "Content-Type") request.ContentType = header.Value;
            else request.SetHeader(header.Key, header.Value);
        }
        request.Timeout = TimeSpan.FromSeconds(300);
        request.Body = Json.Bytes(body);
        return request;
    }

    /// The `content` blocks of the answer.
    private async Task<List<JsonObject>> SendAsync(JsonObject body, CancellationToken ct)
    {
        var request = Request(body);
        HttpData data;
        try
        {
            data = await Http.DataAsync(http, request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct))
        {
            throw OpenRouterError.Network(Http.Describe(error));
        }
        if (data.Status is < 200 or >= 300)
            throw new ProviderHttpError(ProviderName, data.Status, ProviderHttpError.MessageFrom(data.Body));
        var json = Json.ParseObject(data.Body)
            ?? throw new ProviderHttpError(ProviderName, 200, "The answer wasn't in the form Glimmer expects.");
        // Always look at the stop reason before the content.
        if (Json.Str(json["stop_reason"]) == "refusal")
            throw new Refusal(Json.Str(Json.Obj(json["stop_details"])?["category"]));
        return Json.Objects(json["content"])
            ?? throw new ProviderHttpError(ProviderName, 200, "The answer wasn't in the form Glimmer expects.");
    }
}
