// Ported from Packages/GlimmerKit/Sources/GlimmerAI/TextModel.swift
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// What a text request answers: the text and what it cost, when the service says
/// (Swift: `OpenRouterClient.TextResponse`).
public sealed record TextReply(string Text, double? Cost);

/// One tool call. `Arguments` is the arguments as the model sent them (a JSON object string)
/// (Swift: `OpenRouterClient.ToolCall`).
public sealed record TextToolCall(string Name, string Arguments);

/// An answer that may call tools (Swift: `OpenRouterClient.ToolResponse`).
public sealed record TextToolReply(string? Text, IReadOnlyList<TextToolCall> ToolCalls, double? Cost);

/// A language model for text: the one door every text request goes through (the Assistant's
/// plan and review, suggestions, Find Distractions). What stands behind it is chosen in one
/// place; callers never talk to a provider's client themselves.
///
/// Messages and tools are in the common chat form (`{"role", "content"}` objects, with
/// pictures as `image_url` data URLs, and `{"type": "function", …}` tool schemas); an adapter
/// translates them to what its provider wants and leaves out what that provider refuses.
public interface ITextModel
{
    /// Shown in messages ("OpenRouter").
    string ProviderName { get; }
    /// The model's id there.
    string ModelID { get; }

    /// True for models that do better with the short list of everyday operations as tools.
    bool PrefersShortToolList => false;

    /// Text and pictures in, text out. `json` asks for a JSON object.
    Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                               CancellationToken ct = default);

    /// The model answers by calling tools. With `onText`, the text part streams as it is written.
    Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                       Action<string>? onText, CancellationToken ct = default);
}

/// Text through a chat-completions service: OpenRouter (or the compatible endpoint set in
/// Settings), Berget, Grunden, or a local server. The client's configuration carries what
/// that service accepts.
public sealed class OpenRouterTextModel : ITextModel
{
    public OpenRouterClient Client { get; }
    public string ModelID { get; }
    public string ProviderName { get; }

    public OpenRouterTextModel(OpenRouterClient client, string model, string providerName = "OpenRouter")
    {
        Client = client;
        ModelID = model;
        ProviderName = providerName;
    }

    public Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                                      CancellationToken ct = default) =>
        Client.TextCompletionAsync(ModelID, system, user, images, imageType, json, temperature, ct);

    public Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                              Action<string>? onText, CancellationToken ct = default)
    {
        if (onText is not null)
            return Client.ToolCompletionStreamingAsync(ModelID, messages, tools, onText, temperature, ct);
        return Client.ToolCompletionAsync(ModelID, messages, tools, temperature, ct);
    }
}
