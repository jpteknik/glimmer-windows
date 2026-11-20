// Test doubles for the AI side: a picture service and a text service that answer from memory (no test reaches a real
// service), and the model store every on-device feature consults, pointed at an empty folder for the test.
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.Editor.Tests;

/// <summary>Tests that read or replace ModelStore.Shared (one store for the whole process) run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedModelStoreCollection
{
    public const string Name = "Shared model store";
}

/// <summary>ModelStore.Shared pointed at an empty folder of the harness (nothing installed) until disposed.</summary>
public sealed class SharedModelStore : IDisposable
{
    private readonly ModelStore previous = ModelStore.Shared;

    public SharedModelStore(Harness h)
    {
        Store = new ModelStore(h.PathIn("models-" + Guid.NewGuid().ToString("N")[..8]));
        ModelStore.Shared = Store;
    }

    public ModelStore Store { get; }

    public void Dispose() => ModelStore.Shared = previous;
}

/// <summary>A picture service that answers every request with one solid picture (or fails as told).</summary>
public sealed class FakeImageModel : IImageModel
{
    public int Width { get; init; } = 64;
    public int Height { get; init; } = 48;
    public double? Cost { get; init; } = 0.04;
    /// <summary>Thrown instead of answering.</summary>
    public Exception? Failure { get; init; }
    public List<string> Prompts { get; } = [];

    public Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        lock (Prompts) Prompts.Add(prompt);
        if (Failure is not null) throw Failure;
        cancellationToken.ThrowIfCancellationRequested();
        int w = images.Count > 0 ? images[0].Width : Width, h = images.Count > 0 ? images[0].Height : Height;
        var pixels = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4] = 200;
            pixels[i * 4 + 1] = 30;
            pixels[i * 4 + 2] = 30;
            pixels[i * 4 + 3] = 255;
        }
        return Task.FromResult(new AIImageResult(new RGBAImage(w, h, pixels), Cost));
    }
}

/// <summary>A text service that answers from a script: the next reply each time (tool calls or text).</summary>
public sealed class FakeTextModel : ITextModel
{
    public string ProviderName => "Fake";
    public string ModelID => "fake/model";
    public Queue<TextToolReply> ToolReplies { get; } = new();
    public Queue<string> TextReplies { get; } = new();
    public int Calls { get; private set; }

    public Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature,
                                      CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new TextReply(TextReplies.Count > 0 ? TextReplies.Dequeue() : "{}", 0.001));
    }

    public Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature,
                                              Action<string>? onText, CancellationToken ct = default)
    {
        Calls++;
        var reply = ToolReplies.Count > 0 ? ToolReplies.Dequeue() : new TextToolReply("Nothing to do.", [], 0.001);
        if (reply.Text is { } text) onText?.Invoke(text);
        return Task.FromResult(reply);
    }
}
