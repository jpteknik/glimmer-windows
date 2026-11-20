// Ported from Packages/GlimmerKit/Sources/GlimmerAI/OpenRouterClient.swift
using System.Globalization;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

public static class AIDefaults
{
    /// The defaults since 0.3.1. Until then they were google/gemini-3-pro-image-preview and
    /// google/gemini-2.5-flash, which Google has retired (see `Retirement`). The text model
    /// is the one that passed the Assistant's evaluation as 2.5-flash did (18 of 18, 2026-11-19);
    /// gemini-3.5-flash-lite, where Google sends 2.5-flash, got 16 of 18.
    public const string ImageModel = "google/gemini-3-pro-image";
    public const string TextModel = "google/gemini-3.5-flash";

    /// What Google says about a model id it has retired, for Settings: someone who chose it keeps it,
    /// and is told. Source: https://ai.google.dev/gemini-api/docs/deprecations, read 2026-11-19.
    public static string? Retirement(string id)
    {
        switch (id.Trim())
        {
            case "google/gemini-2.5-flash":
                return "Google sends requests for gemini-2.5-flash to gemini-3.5-flash-lite from December 1, 2026, at that model's prices (Google's deprecations page, read November 19, 2026). Restore Defaults switches to Glimmer's current default.";
            case "google/gemini-3-pro-image-preview":
                return "Google shut gemini-3-pro-image-preview down on June 25, 2026; its replacement is gemini-3-pro-image (Google's deprecations page, read November 19, 2026). Restore Defaults switches to it.";
            default:
                return null;
        }
    }

    public const string ImageModelKey = "ai.imageModel";
    public const string TextModelKey = "ai.textModel";

    /// The OpenRouter-compatible API Glimmer talks to. Configurable in Settings › AI,
    /// so nothing depends on a server Glimmer's makers run.
    public const string DefaultEndpoint = "https://openrouter.ai/api/v1";
    public const string EndpointKey = "ai.endpoint";

    /// OpenRouter's address.
    public static Uri DefaultEndpointUri { get; } = new(DefaultEndpoint);

    /// The configured endpoint (the app reads its setting `EndpointKey` and passes the text here),
    /// or OpenRouter when the setting is empty or not an http(s) URL.
    public static Uri EndpointFrom(string? text)
    {
        var trimmed = (text ?? "").Trim(' ', '\t');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
            && (url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && url.Host.Length > 0)
            return url;
        return DefaultEndpointUri;
    }
}

public enum OpenRouterErrorKind
{
    MissingApiKey,
    /// 401 – fatal, never retried.
    InvalidApiKey,
    /// 402 – fatal, never retried.
    InsufficientCredits,
    RateLimited,
    ContentPolicy,
    BadRequest,
    Unavailable,
    Network,
    UnexpectedResponse,
    /// The model answered without an image (often a refusal or a clarifying question).
    NoImageInResponse,
}

/// OpenRouter's errors (Swift: `enum OpenRouterError`). `Message` is the English `errorDescription`.
public sealed class OpenRouterError : Exception, IEquatable<OpenRouterError>
{
    public OpenRouterErrorKind Kind { get; }
    /// For `Unavailable` and `UnexpectedResponse`.
    public int Status { get; }
    /// For `RateLimited`: seconds, when the service said.
    public double? RetryAfter { get; }
    /// The service's message (`ContentPolicy`, `BadRequest`, `UnexpectedResponse`, `NoImageInResponse`), or the system's (`Network`).
    public string? Detail { get; }

    private OpenRouterError(OpenRouterErrorKind kind, int status = 0, double? retryAfter = null, string? detail = null)
    {
        Kind = kind;
        Status = status;
        RetryAfter = retryAfter;
        Detail = detail;
    }

    public static OpenRouterError MissingAPIKey() => new(OpenRouterErrorKind.MissingApiKey);
    public static OpenRouterError InvalidAPIKey() => new(OpenRouterErrorKind.InvalidApiKey);
    public static OpenRouterError InsufficientCredits() => new(OpenRouterErrorKind.InsufficientCredits);
    public static OpenRouterError RateLimited(double? retryAfter) => new(OpenRouterErrorKind.RateLimited, retryAfter: retryAfter);
    public static OpenRouterError ContentPolicy(string? message) => new(OpenRouterErrorKind.ContentPolicy, detail: message);
    public static OpenRouterError BadRequest(string? message) => new(OpenRouterErrorKind.BadRequest, detail: message);
    public static OpenRouterError Unavailable(int status) => new(OpenRouterErrorKind.Unavailable, status: status);
    public static OpenRouterError Network(string message) => new(OpenRouterErrorKind.Network, detail: message);
    public static OpenRouterError UnexpectedResponse(int status, string? message) => new(OpenRouterErrorKind.UnexpectedResponse, status: status, detail: message);
    public static OpenRouterError NoImageInResponse(string? message) => new(OpenRouterErrorKind.NoImageInResponse, detail: message);

    public bool IsRetryable => Kind is OpenRouterErrorKind.RateLimited or OpenRouterErrorKind.Unavailable or OpenRouterErrorKind.Network;

    public override string Message
    {
        get
        {
            switch (Kind)
            {
                case OpenRouterErrorKind.MissingApiKey: return "Add your OpenRouter API key in Settings → AI.";
                case OpenRouterErrorKind.InvalidApiKey: return "OpenRouter rejected the API key.";
                case OpenRouterErrorKind.InsufficientCredits: return "Your OpenRouter account is out of credits.";
                case OpenRouterErrorKind.RateLimited: return "OpenRouter is rate limiting requests. Try again shortly.";
                case OpenRouterErrorKind.ContentPolicy: return "The request was blocked by content policy." + (Detail is { } c ? $" {c}" : "");
                case OpenRouterErrorKind.BadRequest: return "OpenRouter could not process the request." + (Detail is { } b ? $" {b}" : "");
                case OpenRouterErrorKind.Unavailable: return $"OpenRouter is unavailable (HTTP {Status}).";
                case OpenRouterErrorKind.Network:
                    // What the system says ("The Internet connection appears to be offline"), then what to do.
                    return $"Glimmer couldn't reach the AI service: {(Detail ?? "").Trim('.', ' ')}."
                        + " Check your internet connection and try again. The tools that run on this computer keep working.";
                case OpenRouterErrorKind.UnexpectedResponse: return $"Unexpected response (HTTP {Status})." + (Detail is { } u ? $" {u}" : "");
                case OpenRouterErrorKind.NoImageInResponse:
                    return "The model didn't return an image." + (Detail is { } n ? $" It said: {(n.Length > 300 ? n[..300] : n)}" : "");
                default: return "OpenRouter failed.";
            }
        }
    }

    /// Maps a non-2xx HTTP response. Returns null for success codes.
    public static OpenRouterError? From(int status, byte[] body, string? retryAfterHeader)
    {
        if (status is >= 200 and < 300) return null;
        var message = ExtractMessage(body);
        switch (status)
        {
            case 401: return InvalidAPIKey();
            case 402: return InsufficientCredits();
            case 403: return ContentPolicy(message);
            case 400:
            case 422: return BadRequest(message);
            case 429:
                return RateLimited(double.TryParse(retryAfterHeader, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var seconds) ? seconds : null);
            case >= 500 and <= 599: return Unavailable(status);
            default: return UnexpectedResponse(status, message);
        }
    }

    /// OpenRouter errors look like `{"error": {"message": "...", "code": 401}}`.
    internal static string? ExtractMessage(byte[] body)
    {
        var json = Json.ParseObject(body);
        if (json is null || !json.TryGetPropertyValue("error", out var error) || error is null) return null;
        if (error is not JsonObject inner) return null;
        if (!inner.TryGetPropertyValue("message", out var message) || message is null) return null;
        return Json.Str(message);
    }

    public bool Equals(OpenRouterError? other) =>
        other is not null && Kind == other.Kind && Status == other.Status && RetryAfter == other.RetryAfter && Detail == other.Detail;

    public override bool Equals(object? obj) => Equals(obj as OpenRouterError);
    public override int GetHashCode() => HashCode.Combine(Kind, Status, RetryAfter, Detail);
}

public sealed record OpenRouterKeyInfo
{
    public string? Label { get; init; }
    /// Credits used, in USD.
    public double? Usage { get; init; }
    /// Credit limit in USD, null when unlimited.
    public double? Limit { get; init; }
    public double? LimitRemaining { get; init; }
    public bool? IsFreeTier { get; init; }

    /// The decoded `data` object of `GET /key` (`label`, `usage`, `limit`, `limit_remaining`,
    /// `is_free_tier`); null when a field has the wrong type, as the Swift decoder failed.
    public static OpenRouterKeyInfo? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        bool Ok(string key, Func<JsonNode?, bool> isType) => !o.TryGetPropertyValue(key, out var v) || v is null || isType(v);
        if (!Ok("label", v => Json.Str(v) is not null) || !Ok("usage", v => Json.Num(v) is not null)
            || !Ok("limit", v => Json.Num(v) is not null) || !Ok("limit_remaining", v => Json.Num(v) is not null)
            || !Ok("is_free_tier", v => Json.Bool(v) is not null))
            return null;
        return new OpenRouterKeyInfo
        {
            Label = Json.Str(o["label"]),
            Usage = Json.Num(o["usage"]),
            Limit = Json.Num(o["limit"]),
            LimitRemaining = Json.Num(o["limit_remaining"]),
            IsFreeTier = Json.Bool(o["is_free_tier"]),
        };
    }
}

public sealed record OpenRouterConfiguration
{
    public Uri BaseUrl { get; init; } = AIDefaults.DefaultEndpointUri;
    /// Shown in the OpenRouter dashboard; identifies the app, not the user.
    public string AppTitle { get; init; } = "Glimmer";

    /// What this service's chat completions accept, where it differs from OpenRouter's.
    public ChatDialect Dialect { get; init; } = new();
    /// Set for services other than OpenRouter: errors then carry the service's name, the HTTP
    /// status and its own message, word for word.
    public string? ProviderName { get; init; }
    /// False for a local server: no key is needed or sent.
    public bool NeedsKey { get; init; } = true;

    public OpenRouterConfiguration() { }

    public OpenRouterConfiguration(Uri baseUrl, string appTitle = "Glimmer")
    {
        BaseUrl = baseUrl;
        AppTitle = appTitle;
    }
}

/// The request fields a chat-completions service takes. The adapter, not the caller, leaves
/// out what a service refuses.
public sealed record ChatDialect
{
    /// `tool_choice: "auto"` (Ollama's compatibility layer doesn't list it).
    public bool ToolChoice { get; init; } = true;
    public bool ParallelToolCalls { get; init; } = true;
    /// OpenRouter's `usage: {include: true}`, which makes it report the cost.
    public bool UsageInclude { get; init; } = true;
    public bool Temperature { get; init; } = true;
    /// `response_format: {"type": "json_object"}`.
    public bool JsonObject { get; init; } = true;
    /// `reasoning_effort`, for services that reason at full depth unless told otherwise.
    public string? ReasoningEffort { get; init; }

    /// The common OpenAI-style form without OpenRouter's own fields.
    public static ChatDialect Plain => new() { UsageInclude = false };

    internal void Apply(System.Text.Json.Nodes.JsonObject body)
    {
        if (!ToolChoice) body.Remove("tool_choice");
        if (!ParallelToolCalls) body.Remove("parallel_tool_calls");
        if (!UsageInclude) body.Remove("usage");
        if (!Temperature) body.Remove("temperature");
        if (!JsonObject) body.Remove("response_format");
        if (ReasoningEffort is { } effort) body["reasoning_effort"] = effort;
    }
}

/// A service other than OpenRouter refused or failed: its name, the status and what it said.
public sealed class ProviderHttpError : Exception, IEquatable<ProviderHttpError>
{
    public string Provider { get; }
    public int Status { get; }
    public string Detail { get; }

    public ProviderHttpError(string provider, int status, string message) : base(message)
    {
        Provider = provider;
        Status = status;
        Detail = message;
    }

    public override string Message => Detail.Length == 0 ? $"{Provider} answered HTTP {Status}." : $"{Provider} answered HTTP {Status}: {Detail}";

    /// The service's own message if the body has one in a known place, otherwise the body as it came (shortened).
    internal static string MessageFrom(byte[] body)
    {
        if (OpenRouterError.ExtractMessage(body) is { } m) return m;
        if (Json.ParseObject(body) is { } json)
        {
            foreach (var key in new[] { "error", "message", "detail" })
                if (json.TryGetPropertyValue(key, out var value) && Json.Str(value) is { } s) return s;
        }
        var text = System.Text.Encoding.UTF8.GetString(body).Trim();
        return text.Length > 600 ? text[..600] : text;
    }

    public bool Equals(ProviderHttpError? other) => other is not null && Provider == other.Provider && Status == other.Status && Detail == other.Detail;
    public override bool Equals(object? obj) => Equals(obj as ProviderHttpError);
    public override int GetHashCode() => HashCode.Combine(Provider, Status, Detail);
}

/// Talks to OpenRouter with the user's own API key.
public sealed class OpenRouterClient
{
    private readonly string apiKey;
    private readonly OpenRouterConfiguration configuration;
    private readonly HttpClient http;

    /// Waits between retries; tests replace it so they don't.
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (span, ct) => Task.Delay(span, ct);
    /// 0.5…1, the share of the backoff that is used.
    internal Func<double> Jitter { get; set; } = () => 0.5 + Random.Shared.NextDouble() * 0.5;

    /// `handler` is the transport (tests pass a fake one); without it the shared socket handler is used.
    public OpenRouterClient(string apiKey, OpenRouterConfiguration? configuration = null, HttpMessageHandler? handler = null)
    {
        this.apiKey = apiKey;
        this.configuration = configuration ?? new OpenRouterConfiguration();
        http = KeyStaysHome.CreateClient(handler);
    }

    /// Validates the key and returns its usage/limits (`GET /key`).
    public async Task<OpenRouterKeyInfo> KeyInfoAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(Request("key"), ct).ConfigureAwait(false);
        var info = OpenRouterKeyInfo.FromJson(Json.ParseObject(data.Body)?["data"]);
        return info ?? throw OpenRouterError.UnexpectedResponse(200, "unreadable key info");
    }

    // MARK: Chat completions

    public sealed record ImageResponse(byte[] ImageData, double? Cost, string? Model);

    /// One user message with text plus images (PNG), asking for an image back.
    public async Task<ImageResponse> ImageCompletionAsync(string model, string text, IReadOnlyList<byte[]> images, int? seed = null, CancellationToken ct = default)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var png in images)
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } });
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } },
            ["modalities"] = new JsonArray { "image", "text" },
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (seed is { } s) body["seed"] = s;
        var data = await SendWithRetryAsync(Request("chat/completions", "POST", Json.Bytes(body)), ct).ConfigureAwait(false);
        var json = Json.ParseObject(data.Body) ?? throw OpenRouterError.UnexpectedResponse(200, "not JSON");
        var image = ExtractImage(json) ?? throw OpenRouterError.NoImageInResponse(ExtractText(json));
        return new ImageResponse(image, Cost(json), Json.Str(json["model"]));
    }

    /// Image generation through OpenRouter's Image API, `POST /images`: the route its guide
    /// documents ("Send a POST request to /api/v1/images with the model and prompt"). Input
    /// pictures travel as data URLs in `input_references`; the answer is base64 in
    /// `data[0].b64_json` with the charge in `usage.cost`.
    public async Task<ImageResponse> ImageGenerationAsync(string model, string text, IReadOnlyList<byte[]> images, int? seed = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = text, ["n"] = 1 };
        if (images.Count > 0)
        {
            var references = new JsonArray();
            foreach (var png in images)
                references.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(png) } });
            body["input_references"] = references;
        }
        if (seed is { } s) body["seed"] = s;
        var data = await SendWithRetryAsync(Request("images", "POST", Json.Bytes(body)), ct).ConfigureAwait(false);
        var json = Json.ParseObject(data.Body) ?? throw OpenRouterError.UnexpectedResponse(200, "not JSON");
        var first = Json.Objects(json["data"])?.FirstOrDefault();
        byte[]? image = null;
        if (first is not null && Json.Str(first["b64_json"]) is { } encoded)
            image = DecodeBase64(encoded);
        if (image is null || image.Length == 0)
            throw OpenRouterError.NoImageInResponse(OpenRouterError.ExtractMessage(data.Body));
        return new ImageResponse(image, Json.Num(Json.Obj(json["usage"])?["cost"]), Json.Str(json["model"]) ?? model);
    }

    /// Text-only (optionally multimodal) completion. `jsonMode` requests a JSON object.
    public async Task<TextReply> TextCompletionAsync(string model, string? system, string user, IReadOnlyList<byte[]>? images = null,
                                                     string imageType = "image/png", bool jsonMode = false, double temperature = 0.2,
                                                     CancellationToken ct = default)
    {
        images ??= Array.Empty<byte[]>();
        var messages = new JsonArray();
        if (system is not null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        if (images.Count == 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
        }
        else
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = user } };
            foreach (var png in images)
                content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{imageType};base64," + Convert.ToBase64String(png) } });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (jsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        configuration.Dialect.Apply(body);
        var data = await SendWithRetryAsync(Request("chat/completions", "POST", Json.Bytes(body)), ct).ConfigureAwait(false);
        var json = Json.ParseObject(data.Body);
        var text = json is null ? null : ExtractText(json);
        if (json is null || text is null) throw OpenRouterError.UnexpectedResponse(200, "no text in response");
        return new TextReply(text, Cost(json));
    }

    /// Chat completion with function tools. `messages` are OpenAI-style
    /// `{"role", "content"}` objects; `tools` are `{"type": "function", …}` schemas.
    public async Task<TextToolReply> ToolCompletionAsync(string model, IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools,
                                                         double temperature = 0.2, CancellationToken ct = default)
    {
        var body = ToolBody(model, messages, tools, temperature, stream: false);
        var data = await SendWithRetryAsync(Request("chat/completions", "POST", Json.Bytes(body)), ct).ConfigureAwait(false);
        var json = Json.ParseObject(data.Body);
        if (json is null || Message(json) is null) throw OpenRouterError.UnexpectedResponse(200, "no message in response");
        return new TextToolReply(ExtractText(json), ExtractToolCalls(json), Cost(json));
    }

    private JsonObject ToolBody(string model, IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = Json.Array(messages),
            ["tools"] = Json.Array(tools),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = true,
            ["temperature"] = temperature,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (stream) body["stream"] = true;
        configuration.Dialect.Apply(body);
        return body;
    }

    /// Accumulates a streamed chat completion (SSE `data:` chunks): text deltas
    /// are joined, and tool-call deltas are merged by their index.
    internal sealed class StreamAccumulator
    {
        public string Text { get; private set; } = "";
        private readonly SortedDictionary<int, (string Name, string Arguments)> calls = new();
        public double? Cost { get; private set; }
        public bool HasCalls => calls.Count > 0;

        /// Returns the text delta in this chunk, if any.
        public string? Consume(JsonObject json)
        {
            if (OpenRouterClient.Cost(json) is { } c) Cost = c;
            var delta = Json.Obj(Json.Objects(json["choices"])?.FirstOrDefault()?["delta"]);
            if (delta is null) return null;
            foreach (var call in Json.Objects(delta["tool_calls"]) ?? new List<JsonObject>())
            {
                var index = Json.Int(call["index"]) ?? calls.Count;
                var function = Json.Obj(call["function"]) ?? new JsonObject();
                (string Name, string Arguments) current = calls.TryGetValue(index, out var existing) ? existing : ("", "");
                if (Json.Str(function["name"]) is { } name) current.Name += name;
                if (Json.Str(function["arguments"]) is { } args) current.Arguments += args;
                calls[index] = current;
            }
            if (Json.Str(delta["content"]) is not { Length: > 0 } piece) return null;
            Text += piece;
            return piece;
        }

        public TextToolReply Response
        {
            get
            {
                var ordered = calls.Values.Where(c => c.Name.Length > 0)
                    .Select(c => new TextToolCall(c.Name, c.Arguments.Length == 0 ? "{}" : c.Arguments)).ToList();
                return new TextToolReply(Text.Length == 0 ? null : Text, ordered, Cost);
            }
        }
    }

    /// Like `ToolCompletionAsync`, but streams: `onText` gets the reply text so far as
    /// it grows. Falls back to the plain (retrying) call if the stream can't start.
    public async Task<TextToolReply> ToolCompletionStreamingAsync(string model, IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools,
                                                                  Action<string> onText, double temperature = 0.2, CancellationToken ct = default)
    {
        var body = ToolBody(model, messages, tools, temperature, stream: true);
        var request = Request("chat/completions", "POST", Json.Bytes(body));
        var accumulator = new StreamAccumulator();
        try
        {
            using var response = await Http.StreamAsync(http, request, ct).ConfigureAwait(false);
            if ((int)response.StatusCode != 200) throw OpenRouterError.Unavailable((int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var line in Http.LinesAsync(stream, ct, request.Timeout).ConfigureAwait(false))
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;
                if (Json.ParseObject(payload) is not { } json) continue;
                if (accumulator.Consume(json) is not null) onText(accumulator.Text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            // Nothing usable arrived: use the ordinary call, which reports errors properly and retries.
            if (accumulator.Text.Length == 0 && !accumulator.HasCalls)
                return await ToolCompletionAsync(model, messages, tools, temperature, ct).ConfigureAwait(false);
            throw OpenRouterError.Network(Http.Describe(error));
        }
        return accumulator.Response;
    }

    internal static List<TextToolCall> ExtractToolCalls(JsonObject json)
    {
        var result = new List<TextToolCall>();
        foreach (var call in Json.Objects(Message(json)?["tool_calls"]) ?? new List<JsonObject>())
        {
            if (Json.Obj(call["function"]) is not { } function || Json.Str(function["name"]) is not { } name) continue;
            string args;
            if (Json.Str(function["arguments"]) is { } s) args = s;
            else if (function["arguments"] is { } o && (o is JsonObject || o is JsonArray)) args = Json.Text(o);
            else args = "{}";
            result.Add(new TextToolCall(name, args));
        }
        return result;
    }

    // MARK: Response parsing (mirrors workers/aips/providers/openrouter.py)

    internal static double? Cost(JsonObject json) => Json.Num(Json.Obj(json["usage"])?["cost"]);

    internal static JsonObject? Message(JsonObject json) => Json.Obj(Json.Objects(json["choices"])?.FirstOrDefault()?["message"]);

    internal static string? ExtractText(JsonObject json)
    {
        if (Message(json) is not { } message) return null;
        if (Json.Str(message["content"]) is { } s) return s;
        if (Json.Objects(message["content"]) is { } parts)
        {
            var texts = parts.Select(p => Json.Str(p["type"]) == "text" ? Json.Str(p["text"]) : null).Where(t => t is not null).ToList();
            return texts.Count == 0 ? null : string.Join("\n", texts);
        }
        return null;
    }

    /// Finds image bytes in the three shapes OpenRouter image models return.
    internal static byte[]? ExtractImage(JsonObject json)
    {
        if (Message(json) is not { } message) return null;
        // 1. message.images[*].image_url.url
        foreach (var img in Json.Objects(message["images"]) ?? new List<JsonObject>())
        {
            if (Json.Str(Json.Obj(img["image_url"])?["url"]) is { } url && DecodeDataUrl(url) is { } d) return d;
        }
        // 2. content parts
        if (Json.Objects(message["content"]) is { } parts)
        {
            foreach (var part in parts)
            {
                var type = Json.Str(part["type"]);
                if (type is "image_url" or "output_image" or "image")
                {
                    var url = Json.Str(Json.Obj(part["image_url"])?["url"]) ?? Json.Str(part["url"]);
                    if (url is not null && DecodeDataUrl(url) is { } d) return d;
                }
                if (type == "text" && Json.Str(part["text"]) is { } t && ScanForDataUrl(t) is { } scanned) return scanned;
            }
        }
        // 3. a data URL embedded in text
        if (Json.Str(message["content"]) is { } content) return ScanForDataUrl(content);
        return null;
    }

    internal static byte[]? DecodeDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal)) return null;
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        return DecodeBase64(url[(comma + 1)..]);
    }

    internal static byte[]? ScanForDataUrl(string text)
    {
        var start = text.IndexOf("data:image/", StringComparison.Ordinal);
        if (start < 0) return null;
        var tail = text[start..];
        var end = tail.IndexOfAny(new[] { ')', '"', ' ', '\n' });
        return DecodeDataUrl(end < 0 ? tail : tail[..end]);
    }

    /// `Data(base64Encoded:options: .ignoreUnknownCharacters)`: characters that aren't base64 are skipped.
    internal static byte[]? DecodeBase64(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/') clean.Append(c);
        }
        var padded = clean.ToString();
        if (padded.Length % 4 == 1) return null;
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(padded); } catch (FormatException) { return null; }
    }

    // MARK: Transport

    /// Retries rate limits, 5xx and network errors with exponential backoff
    /// (as the web workers do). 401/402 are fatal and surface immediately.
    internal async Task<HttpData> SendWithRetryAsync(RequestSpec request, CancellationToken ct, int maxRetries = 4)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OpenRouterError error) when (error.IsRetryable && attempt < maxRetries)
            {
                var delay = Math.Min(15, 0.5 * Math.Pow(2, attempt)) * Jitter();
                if (error.Kind == OpenRouterErrorKind.RateLimited && error.RetryAfter is { } after) delay = Math.Min(after, 60);
                attempt++;
                await Delay(TimeSpan.FromSeconds(Math.Max(0, delay)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The request's `timeoutInterval`: how long the wait for an answer, and for more bytes of a streamed one, may take. Tests shorten it.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(240);

    internal RequestSpec Request(string path, string method = "GET", byte[]? body = null)
    {
        if (apiKey.Length == 0 && configuration.NeedsKey) throw OpenRouterError.MissingAPIKey();
        var request = new RequestSpec(Http.Append(configuration.BaseUrl, path), method);
        if (apiKey.Length > 0) request.SetHeader("Authorization", $"Bearer {apiKey}");
        request.SetHeader("X-Title", configuration.AppTitle);
        request.Timeout = RequestTimeout;
        if (body is not null) request.Body = body;
        return request;
    }

    internal async Task<HttpData> SendAsync(RequestSpec request, CancellationToken ct)
    {
        HttpData data;
        try
        {
            data = await Http.DataAsync(http, request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct))
        {
            throw OpenRouterError.Network(Http.Describe(error));
        }
        if (OpenRouterError.From(data.Status, data.Body, data.Header("Retry-After")) is { } error2)
        {
            // Another service's failure is shown as that service gave it; only what is worth
            // retrying keeps its kind, so the retry loop still works.
            if (configuration.ProviderName is { } provider && !error2.IsRetryable)
                throw new ProviderHttpError(provider, data.Status, ProviderHttpError.MessageFrom(data.Body));
            throw error2;
        }
        return data;
    }
}
