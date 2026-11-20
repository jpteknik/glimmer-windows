// Ported from Packages/GlimmerKit/Sources/GlimmerAI/ServerSentEvents.swift
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// Reads an answer that arrives as server-sent events: each `data:` line holds one JSON
/// object. Used by the Anthropic and OpenAI adapters when the Assistant wants the text as
/// it comes. The request carries a key, so it goes through `KeyStaysHome`.
internal static class ServerSentEvents
{
    /// Calls `onEvent` for each JSON object, in order. A status other than 2xx is the
    /// service's own error, read from the body.
    public static async Task ReadAsync(HttpClient client, RequestSpec request, string provider, Action<JsonObject> onEvent, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.StreamAsync(client, request, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (Http.IsConnectionFailure(error, ct))
        {
            throw OpenRouterError.Network(Http.Describe(error));
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                var body = new MemoryStream();
                try
                {
                    await using var errorStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await errorStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        body.Write(buffer, 0, read);
                        if (body.Length > 65_536) break;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException)
                {
                    // What arrived is what the message is made of.
                }
                throw new ProviderHttpError(provider, status, ProviderHttpError.MessageFrom(body.ToArray()));
            }
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await foreach (var line in Http.LinesAsync(stream, ct, request.Timeout).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var payload = line[5..].Trim();
                    if (payload == "[DONE]") break;
                    if (Json.ParseObject(payload) is not { } json) continue;
                    onEvent(json);
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
            {
                // A stream that sends nothing for the request's timeout is a failed connection, as URLSession's `timeoutInterval` made it.
                throw OpenRouterError.Network(Http.Describe(error));
            }
        }
    }
}

/// Puts Anthropic's stream events together into the `content` blocks a whole answer has
/// (Messages API: `content_block_start`, `content_block_delta` with `text_delta` or
/// `input_json_delta`, `message_delta` with the stop reason; read on platform.claude.com 2026-11-16).
internal sealed class AnthropicStream
{
    private readonly SortedDictionary<int, JsonObject> blocks = new();
    private readonly Dictionary<int, string> partialInput = new();
    public string? StopReason { get; private set; }
    public string? StopCategory { get; private set; }

    /// Takes one event. True when the text grew.
    public bool Consume(JsonObject @event, string provider)
    {
        switch (Json.Str(@event["type"]))
        {
            case "content_block_start":
            {
                if (Json.Int(@event["index"]) is not { } index || Json.Obj(@event["content_block"]) is not { } block) return false;
                blocks[index] = (JsonObject)block.DeepClone();
                break;
            }
            case "content_block_delta":
            {
                if (Json.Int(@event["index"]) is not { } index || Json.Obj(@event["delta"]) is not { } delta) return false;
                if (Json.Str(delta["type"]) == "text_delta" && Json.Str(delta["text"]) is { } more)
                {
                    if (!blocks.TryGetValue(index, out var block)) blocks[index] = block = new JsonObject { ["type"] = "text" };
                    block["text"] = (Json.Str(block["text"]) ?? "") + more;
                    return true;
                }
                if (Json.Str(delta["type"]) == "input_json_delta" && Json.Str(delta["partial_json"]) is { } partial)
                    partialInput[index] = (partialInput.TryGetValue(index, out var so) ? so : "") + partial;
                break;
            }
            case "message_delta":
            {
                var delta = Json.Obj(@event["delta"]);
                StopReason = Json.Str(delta?["stop_reason"]) ?? StopReason;
                StopCategory = Json.Str(Json.Obj(delta?["stop_details"])?["category"]) ?? StopCategory;
                break;
            }
            case "error":
            {
                var message = Json.Str(Json.Obj(@event["error"])?["message"]);
                throw new ProviderHttpError(provider, 200, message ?? "The answer stopped with an error.");
            }
        }
        return false;
    }

    /// The blocks in order; a tool call's input is the JSON its pieces add up to.
    public List<JsonObject> Content
    {
        get
        {
            var list = new List<JsonObject>();
            foreach (var (index, stored) in blocks)
            {
                var block = (JsonObject)stored.DeepClone();
                if (Json.Str(block["type"]) == "tool_use" && partialInput.TryGetValue(index, out var text) && text.Length > 0
                    && Json.Parse(text) is { } input)
                    block["input"] = input;
                list.Add(block);
            }
            return list;
        }
    }

    public string Text => AnthropicTextModel.TextIn(Content);
}

/// Puts OpenAI's Responses stream together: text from `response.output_text.delta`, tool
/// calls from `response.output_item.done` (the item holds the whole call), an `error` event
/// with its message (OpenAI's streaming and function-calling guides, read 2026-11-16).
internal sealed class ResponsesStream
{
    public string Text { get; private set; } = "";
    public List<JsonObject> Items { get; } = new();

    public bool Consume(JsonObject @event, string provider)
    {
        switch (Json.Str(@event["type"]))
        {
            case "response.output_text.delta":
                if (Json.Str(@event["delta"]) is not { } more) return false;
                Text += more;
                return true;
            case "response.output_item.done":
                if (Json.Obj(@event["item"]) is { } item) Items.Add((JsonObject)item.DeepClone());
                break;
            case "error":
                throw new ProviderHttpError(provider, 200, Json.Str(@event["message"]) ?? "The answer stopped with an error.");
            case "response.failed":
            {
                var message = Json.Str(Json.Obj(Json.Obj(@event["response"])?["error"])?["message"]);
                throw new ProviderHttpError(provider, 200, message ?? "The request failed at the service.");
            }
        }
        return false;
    }
}
