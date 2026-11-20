// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/LiveOpenRouterTests.swift
// Real OpenRouter calls. Opt in with GLIMMER_LIVE_AI=1 and OPENROUTER_API_KEY; each run costs a few cents.
// Without GLIMMER_LIVE_AI=1 these are skipped and nothing leaves this machine.
// The image generation test of the original is a live call through the image model too; the Mac's JPEG helper is SkiaSharp's here.
using Glimmer.AI;
using SkiaSharp;

namespace Glimmer.AI.Tests;

public class LiveOpenRouterTests
{
    private static string Key => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";

    [LiveFact]
    public async Task KeyIsValid()
    {
        var info = await new OpenRouterClient(Key).KeyInfoAsync();
        Console.WriteLine($"OpenRouter key OK, usage: {info.Usage ?? -1}");
    }

    [LiveFact]
    public async Task BadKeyIsFatalNotRetried()
    {
        var client = new OpenRouterClient("sk-or-v1-invalid");
        var error = await Assert.ThrowsAsync<OpenRouterError>(() => client.KeyInfoAsync());
        Assert.Equal(OpenRouterError.InvalidAPIKey(), error);
    }

    [LiveFact]
    public async Task ATextReplyComesBackWithItsCost()
    {
        var model = new OpenRouterTextModel(new OpenRouterClient(Key), AIDefaults.TextModel);
        var reply = await model.ReplyAsync(null, "Answer with the single word: ready", Array.Empty<byte[]>(), "image/png", json: false, 0.2);
        Console.WriteLine($"Reply: {reply.Text}, cost {reply.Cost ?? -1}");
        Assert.False(string.IsNullOrWhiteSpace(reply.Text));
    }

    /// <summary>A flat JPEG of one colour.</summary>
    private static byte[] Jpeg(float r, float g, float b)
    {
        using var bitmap = new RGBAImage(64, 64, new System.Numerics.Vector4(r, g, b, 255)).ToSKBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static OpenRouterTextModel Text => new(new OpenRouterClient(Key), AIDefaults.TextModel);

    [LiveFact]
    public async Task GeneratesAnImage()
    {
        var model = new OpenRouterImageModel(new OpenRouterClient(Key), AIDefaults.ImageModel);
        var result = await AIPipelines.GenerateAsync("A small red balloon on a white background, no text", null, 512, 512, model);
        Console.WriteLine($"Image {result.Image.Width}x{result.Image.Height}, cost {result.Cost ?? -1}");
        Assert.True(result.Image.Width > 0 && result.Image.Height > 0);
    }

    [LiveFact]
    public async Task PlansWithToolCalls()
    {
        var context = new AgentContext(1600, 1200, 1, false, "raster", "Photo");
        var (plan, cost) = await AgentPlanner.PlanAsync("Make it black and white and add a vignette", context, [], Text);
        Console.WriteLine($"Plan: {string.Join(", ", plan.Steps.Select(s => s.Op))}, message: {plan.Message ?? "-"}, cost {cost ?? -1}");
        Assert.Contains(plan.Steps, s => s.Op == "add_adjustment" && s.String("type") == "black_white");
        Assert.Contains(plan.Steps, s => (s.Op == "apply_filter" && s.String("type") == "vignette") || s.Op == "apply_preset");
        Assert.Empty(plan.DroppedOps);
    }

    [LiveFact]
    public async Task StreamsAPlanAndSeesTheImage()
    {
        var context = new AgentContext(64, 64, 1, false, "raster", "Photo");
        var seen = 0;
        var (plan, _) = await AgentPlanner.PlanAsync("What single colour fills this image? Answer with the colour name only, call no tools.", context, [], Text,
                                                     image: Jpeg(230, 20, 20), onText: _ => Interlocked.Increment(ref seen));
        Console.WriteLine($"Vision answer: {plan.Message ?? "-"}, streamed updates: {seen}");
        Assert.Empty(plan.Steps);
        Assert.Contains("red", plan.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(seen >= 1);
    }

    [LiveFact]
    public async Task SuggestsEditsForAnImage()
    {
        var s = await AgentPlanner.SuggestionsAsync(Jpeg(40, 40, 60), "sv", Text);
        Console.WriteLine("Suggestions: " + string.Join("; ", s.Select(x => $"{x.Title}: {x.Goal}")));
        Assert.Equal(3, s.Count);
    }

    [LiveFact]
    public async Task ReviewCorrectsAResultThatMissed()
    {
        // Asked for much darker; "after" is brighter than "before".
        var steps = new[] { new AgentStep("add_adjustment", ("type", "exposure"), ("params", AgentValue.Object(("exposure", 1)))) };
        var plan = await AgentPlanner.ReviewAsync("Make the image much darker", steps, Jpeg(120, 120, 120), Jpeg(220, 220, 220), Text);
        Console.WriteLine($"Review: {string.Join(", ", plan.Steps.Select(s => s.Op))} — {plan.Message ?? "-"}");
        Assert.NotEmpty(plan.Steps);
        var ok = await AgentPlanner.ReviewAsync("Make the image much darker", steps, Jpeg(220, 220, 220), Jpeg(60, 60, 60), Text);
        Console.WriteLine($"Review of a good result: {ok.Steps.Length} steps — {ok.Message ?? "-"}");
        Assert.Empty(ok.Steps);
    }

    /// <summary>
    /// The Assistant's evaluation (LocalModelEvaluation, the set every Assistant model is judged by) against OpenRouter models:
    /// GLIMMER_OPENROUTER_EVAL=&lt;id&gt;[,&lt;id&gt;…], with GLIMMER_LIVE_AI=1. Paid: only with Anders's yes. Prints each case and the score
    /// as the model planned and as the person gets it.
    /// </summary>
    [LiveFact]
    public async Task EvaluateOpenRouterModels()
    {
        var ids = Environment.GetEnvironmentVariable("GLIMMER_OPENROUTER_EVAL");
        if (string.IsNullOrEmpty(ids)) return;
        var client = new OpenRouterClient(Key);
        foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var before = (await client.KeyInfoAsync()).Usage ?? 0;
            var report = await LocalModelEvaluation.RunAsync(new OpenRouterTextModel(client, id), progress: o =>
                Console.WriteLine($"EVAL {id} | {(o.KnownGap ? (o.Passed ? "gap+" : "gap-") : o.Passed ? "ok  " : "FAIL")} | {o.Seconds,5:F1} s | {o.Language} | {o.Goal} -> {o.Got}"));
            await Task.Delay(TimeSpan.FromSeconds(5));   // OpenRouter's usage figure catches up
            var after = (await client.KeyInfoAsync()).Usage ?? 0;
            var (en, sv) = (report.PassedIn("en"), report.PassedIn("sv"));
            Console.WriteLine($"EVAL {id}: {report.Passed} of {report.Scored.Count} (English {en.Passed}/{en.Total}, Swedish {sv.Passed}/{sv.Total}), median {report.Median:F1} s, slowest {report.Slowest:F1} s: {(report.Passes ? "PASSES" : "does not pass")}");
            Console.WriteLine($"EVAL {id}: as the model planned: {report.PassedAsWritten} of {report.Scored.Count}; as the person gets it: {report.Passed} of {report.Scored.Count}");
            Console.WriteLine($"EVAL {id}: cost USD {after - before:F4} (OpenRouter's usage for the key, before {before}, after {after})");
        }
    }

    /// <summary>The same evaluation against Ollama on this computer: GLIMMER_LOCAL_EVAL=&lt;model&gt;[,&lt;model&gt;…]. Free; nothing leaves the computer.</summary>
    [Fact]
    public async Task EvaluateInstalledModels()
    {
        var names = Environment.GetEnvironmentVariable("GLIMMER_LOCAL_EVAL");
        if (string.IsNullOrEmpty(names)) return;
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var abilities = await OllamaTextModel.AbilitiesAsync(name);
            Console.WriteLine($"EVAL {name}: tools {abilities.Tools}, vision {abilities.Vision}, context length {abilities.ContextLength?.ToString() ?? "?"}");
            if (!abilities.Tools) { Console.WriteLine($"EVAL {name}: not evaluated, Ollama lists it without tools"); continue; }
            var report = await LocalModelEvaluation.RunAsync(new OllamaTextModel(name), progress: o =>
                Console.WriteLine($"EVAL {name} | {(o.KnownGap ? (o.Passed ? "gap+" : "gap-") : o.Passed ? "ok  " : "FAIL")} | {o.Seconds,5:F1} s | {o.Language} | {o.Goal} -> {o.Got}"));
            var (en, sv) = (report.PassedIn("en"), report.PassedIn("sv"));
            Console.WriteLine($"EVAL {name}: {report.Passed} of {report.Scored.Count} (English {en.Passed}/{en.Total}, Swedish {sv.Passed}/{sv.Total}), median {report.Median:F1} s, slowest {report.Slowest:F1} s: {(report.Passes ? "PASSES" : "does not pass")}{report.BothWaysLine}");
            if (report.GapLine.Length > 0) Console.WriteLine($"EVAL {name}:{report.GapLine}");
            // Ollama keeps the model loaded for minutes: unload it.
            await OllamaTextModel.UnloadAsync(name);
        }
    }

    /// Without the opt-in the attribute skips the test: this checks the attribute itself, which needs no network.
    [Fact]
    public void LiveTestsAreSkippedUnlessAskedFor()
    {
        var attribute = new LiveFactAttribute();
        if (Environment.GetEnvironmentVariable("GLIMMER_LIVE_AI") == "1") Assert.Null(attribute.Skip);
        else Assert.Contains("GLIMMER_LIVE_AI=1", attribute.Skip);
    }
}
