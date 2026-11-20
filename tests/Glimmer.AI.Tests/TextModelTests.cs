// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/TextModelTests.swift
// (the planner and review tests go through AgentPlanner and come with the Agent port; the source-scanning guard
// tests -- "only the adapter calls the client's text methods" -- are replaced by the type system: the client's
// text methods are `OpenRouterClient` members that only `OpenRouterTextModel` calls in this library, which
// the first test below checks by reading the library's source files)
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

/// A text model that answers from a script and records what it was asked: what tests use
/// instead of a provider (no network, no key, no money).
internal sealed class ScriptedTextModel : ITextModel
{
    public string ProviderName => "Scripted";
    public string ModelID => "scripted-1";
    private readonly object gate = new();
    private readonly List<(string Kind, double Temperature, bool Json, int Images, string[] Tools, bool Streamed)> asked = new();
    public IReadOnlyList<(string Kind, double Temperature, bool Json, int Images, string[] Tools, bool Streamed)> Asked { get { lock (gate) return asked.ToList(); } }
    public string Text = "";
    public List<TextToolCall> Calls = new();
    /// What arrives before the final text, piece by piece (each is everything so far).
    public List<string> Streamed = new();

    public Task<TextReply> ReplyAsync(string? system, string user, IReadOnlyList<byte[]> images, string imageType, bool json, double temperature, CancellationToken ct = default)
    {
        lock (gate) asked.Add(("reply", temperature, json, images.Count, Array.Empty<string>(), false));
        return Task.FromResult(new TextReply(Text, null));
    }

    public Task<TextToolReply> CallToolsAsync(IReadOnlyList<JsonObject> messages, IReadOnlyList<JsonObject> tools, double temperature, Action<string>? onText, CancellationToken ct = default)
    {
        var names = tools.Select(t => (string?)t["function"]?["name"]).Where(n => n is not null).Select(n => n!).ToArray();
        lock (gate) asked.Add(("tools", temperature, false, 0, names, onText is not null));
        foreach (var partial in Streamed) onText?.Invoke(partial);
        onText?.Invoke(Text);
        return Task.FromResult(new TextToolReply(Text, Calls, null));
    }
}

public class TextModelTests
{
    private const string Answer = @"{""choices"":[{""message"":{""role"":""assistant"",""content"":""Black and white."",""tool_calls"":[{""id"":""c"",""type"":""function"",""function"":{""name"":""add_adjustment"",""arguments"":""{}""}}]}}],""usage"":{""cost"":0.01}}";
    private static readonly JsonObject User = (JsonObject)JsonNode.Parse(@"{""role"":""user"",""content"":""x""}")!;
    private static readonly JsonObject Tool = (JsonObject)JsonNode.Parse(@"{""type"":""function"",""function"":{""name"":""add_adjustment"",""parameters"":{""type"":""object""}}}")!;

    [Fact]
    public async Task TheOpenRouterModelGoesThroughTheClientAndReportsTheCost()
    {
        var fake = new FakeProvider((200, Answer), (200, Answer));
        var model = new OpenRouterTextModel(new OpenRouterClient("sk", handler: fake), AIDefaults.TextModel);
        Assert.Equal("OpenRouter", model.ProviderName);
        Assert.Equal("google/gemini-3.5-flash", model.ModelID);
        Assert.False(((ITextModel)model).PrefersShortToolList);
        var reply = await model.CallToolsAsync(new[] { User }, new[] { Tool }, 0.2, null);
        Assert.Equal("Black and white.", reply.Text);
        Assert.Equal(0.01, reply.Cost);
        Assert.Null(fake.First.Json["stream"]);
        var text = await model.ReplyAsync("s", "u", Array.Empty<byte[]>(), "image/png", json: false, 0.7);
        Assert.Equal("Black and white.", text.Text);
        Assert.Equal("google/gemini-3.5-flash", (string)fake.Requests[1].Json["model"]!);
        Assert.Equal(0.7, (double)fake.Requests[1].Json["temperature"]!);
    }

    [Fact]
    public async Task WithAListenerTheOpenRouterModelStreams()
    {
        var fake = new FakeProvider((200, "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n"));
        var model = new OpenRouterTextModel(new OpenRouterClient("sk", handler: fake), "m", "OpenRouter");
        var heard = new List<string>();
        var reply = await model.CallToolsAsync(new[] { User }, new[] { Tool }, 0.2, heard.Add);
        Assert.Equal(new[] { "Hi" }, heard);
        Assert.Equal("Hi", reply.Text);
        Assert.True((bool)fake.First.Json["stream"]!);
    }

    [Fact]
    public async Task AScriptedModelRecordsWhatItWasAskedLikeTheOriginalsTestDouble()
    {
        ITextModel model = new ScriptedTextModel { Text = "Done.", Streamed = { "Do", "Done" } };
        var heard = new List<string>();
        var reply = await model.CallToolsAsync(new[] { User }, new[] { Tool }, 0.2, heard.Add);
        Assert.Equal(new[] { "Do", "Done", "Done." }, heard);
        Assert.Equal("Done.", reply.Text);
        var scripted = (ScriptedTextModel)model;
        Assert.Equal("tools", scripted.Asked[0].Kind);
        Assert.Equal(new[] { "add_adjustment" }, scripted.Asked[0].Tools);
        Assert.True(scripted.Asked[0].Streamed);
    }

    /// No text request goes around the one door: the library's code calls the client's text methods only from the
    /// adapter (the original scanned its Swift sources the same way). The sources are found from this file's own
    /// path, so the test fails, rather than passes empty, if they are not there.
    [Fact]
    public void OnlyTheAdapterCallsTheClientsTextMethods() => ScanSources();

    private static void ScanSources([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Glimmer.AI"));
        Assert.True(Directory.Exists(folder), $"the library's sources were not found at {folder}");
        var files = Directory.GetFiles(folder, "*.cs");
        Assert.True(files.Length > 10);
        foreach (var file in files.Where(f => !f.EndsWith("OpenRouterClient.cs") && !f.EndsWith("TextModel.cs")))
        {
            var text = File.ReadAllText(file);
            foreach (var call in new[] { ".TextCompletionAsync(", ".ToolCompletionAsync(", ".ToolCompletionStreamingAsync(" })
                Assert.False(text.Contains(call), $"{Path.GetFileName(file)} calls {call} itself; go through a TextModel");
        }
        // The check can fail: the adapter file does contain the calls it is the only one allowed to make.
        Assert.Contains(".ToolCompletionStreamingAsync(", File.ReadAllText(Path.Combine(folder, "TextModel.cs")));
    }
}
