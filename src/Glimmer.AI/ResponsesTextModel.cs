// Ported from Packages/GlimmerKit/Sources/GlimmerAI/ResponsesTextModel.swift
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// Text through the Responses API (`POST /responses`): OpenAI and xAI. OpenAI's newest models
/// take tool calls only this way ("GPT-6 Astra and GPT-6.1 Sol require the Responses API for
/// tool calling"), and xAI's function-calling page shows only this form.
///
/// Built from the two services' documentation. What their pages don't show for this API is
/// not sent: no temperature, no JSON mode (the prompt asks for JSON and the answer is parsed).
/// OpenAI's answer is streamed when the Assistant wants the text as it comes (its guides
/// document the events). xAI's pages name no events for this API, so its reply arrives
/// whole and `onText` gets it once.
public sealed class ResponsesTextModel : ITextModel
{
    public string ProviderName { get; }
    public string ModelID { get; }
    private readonly Uri baseUrl;
    private readonly string key;
    private readonly HttpClient http;
    /// Whether the service documents this API's stream events.
    private readonly bool streams;

    public ResponsesTextModel(AIProvider provider, string key, string model, HttpMessageHandler? handler = null)
    {
        ProviderName = provider.Name;
        streams = provider.Slug == "openai";
        baseUrl = new Uri(provider.BaseUrl);
        this.key = key;
        ModelID = model;
        http = KeyStaysHome.CreateClient(handler);
    }

    public async Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                                            CancellationToken ct = default)
    {
        var input = new JsonArray();
        if (system is not null) input.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        var content = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = user } };
        foreach (var image in images)
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{imageType};base64," + Convert.ToBase64String(image) });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        var output = await SendAsync(new JsonObject { ["model"] = ModelID, ["input"] = input }, ct).ConfigureAwait(false);
        return new TextReply(TextIn(output), null);
    }

    public async Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                                    Action<string>? onText, CancellationToken ct = default)
    {
        var body = new JsonObject { ["model"] = ModelID, ["input"] = Json.Array(Input(messages)), ["tools"] = Json.Array(Tools(tools)) };
        if (streams && onText is not null)
        {
            var stream = new ResponsesStream();
            var name = ProviderName;
            body["stream"] = true;
            await ServerSentEvents.ReadAsync(http, Request(body), name, @event =>
            {
                if (stream.Consume(@event, name)) onText(stream.Text);
            }, ct).ConfigureAwait(false);
            // Text that came only inside a finished item (no deltas) still counts.
            var streamed = stream.Text.Length == 0 ? TextIn(stream.Items) : stream.Text;
            var streamedCalls = Calls(stream.Items);
            if (streamed.Length == 0 && streamedCalls.Count == 0)
                throw new ProviderHttpError(name, 200, "The answer wasn't in the form Glimmer expects.");
            return new TextToolReply(streamed.Length == 0 ? null : streamed, streamedCalls, null);
        }
        var output = await SendAsync(body, ct).ConfigureAwait(false);
        var text = TextIn(output);
        if (text.Length > 0) onText?.Invoke(text);
        return new TextToolReply(text.Length == 0 ? null : text, Calls(output), null);
    }

    internal static List<TextToolCall> Calls(IEnumerable<JsonObject> output)
    {
        var calls = new List<TextToolCall>();
        foreach (var item in output)
        {
            if (Json.Str(item["type"]) != "function_call" || Json.Str(item["name"]) is not { } name) continue;
            calls.Add(new TextToolCall(name, Json.Str(item["arguments"]) ?? "{}"));
        }
        return calls;
    }

    /// Chat-form tools (`{"type": "function", "function": {name, description, parameters}}`) to the flat form this API takes.
    internal static List<JsonObject> Tools(IReadOnlyList<JsonObject> chat)
    {
        var result = new List<JsonObject>();
        foreach (var tool in chat)
        {
            if (Json.Obj(tool["function"]) is not { } f || Json.Str(f["name"]) is not { } name) continue;
            var output = new JsonObject { ["type"] = "function", ["name"] = name };
            if (f["description"] is { } d) output["description"] = d.DeepClone();
            if (f["parameters"] is { } p) output["parameters"] = p.DeepClone();
            result.Add(output);
        }
        return result;
    }

    /// Chat-form messages to input items: text parts become `input_text`, pictures `input_image` with their data URL.
    internal static List<JsonObject> Input(IReadOnlyList<JsonObject> messages)
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
            var content = new JsonArray();
            foreach (var part in parts)
            {
                if (Json.Str(part["text"]) is { } text)
                    content.Add(new JsonObject { ["type"] = "input_text", ["text"] = text });
                else if (Json.Str(Json.Obj(part["image_url"])?["url"]) is { } url)
                    content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
            }
            output["content"] = content;
            result.Add(output);
        }
        return result;
    }

    /// The text of the answer: every `output_text` of every `message` item.
    internal static string TextIn(IEnumerable<JsonObject> output) =>
        string.Concat(output.Where(i => Json.Str(i["type"]) == "message")
            .SelectMany(i => Json.Objects(i["content"]) ?? new List<JsonObject>())
            .Where(c => Json.Str(c["type"]) == "output_text")
            .Select(c => Json.Str(c["text"]))
            .Where(t => t is not null));

    private RequestSpec Request(JsonObject body)
    {
        if (key.Length == 0) throw OpenRouterError.MissingAPIKey();
        var request = new RequestSpec(Http.Append(baseUrl, "responses"), "POST");
        request.SetHeader("Authorization", $"Bearer {key}");
        request.Timeout = TimeSpan.FromSeconds(240);
        request.Body = Json.Bytes(body);
        return request;
    }

    /// The `output` array of the answer.
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
        if (Json.ParseObject(data.Body) is not { } json || Json.Objects(json["output"]) is not { } output)
            throw new ProviderHttpError(ProviderName, 200, "The answer wasn't in the form Glimmer expects.");
        return output;
    }
}
