// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/PipelineTests.swift (AIPipelineTests) and MorePipelineTests.swift (MorePipelineTests).
// Not ported here: ResponseParsingTests (OpenRouterClient.extractImage: service layer), SegmentationTests (Vision: on-device analysis, later),
// PresetTests (Preset is Glimmer.Core's), the AIResultCache is covered below (the original had no test for it).
using System.Numerics;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

/// <summary>Returns a fixed image (optionally a function of the inputs) and records calls.</summary>
sealed class FakeModel(Func<IReadOnlyList<RGBAImage>, RGBAImage> make) : IImageModel
{
    readonly object _lock = new();
    public List<string> Prompts { get; } = [];
    public List<int> InputCounts { get; } = [];

    public Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        lock (_lock) { Prompts.Add(prompt); InputCounts.Add(images.Count); }
        return Task.FromResult(new AIImageResult(make(images), 0.01));
    }
}

public class AIPipelineTests
{
    static RGBAImage Solid(int w, int h, Vector4 c) => new(w, h, c);

    [Fact]
    public async Task InpaintLimitsTheResultToTheMask()
    {
        var image = Solid(200, 100, new Vector4(50, 100, 150, 255));
        var mask = new GrayImage(200, 100, 0f);
        for (int y = 40; y < 60; y++) for (int x = 90; x < 110; x++) mask.Values[y * 200 + x] = 255;
        // The model returns red everywhere, at twice the size.
        var model = new FakeModel(inputs => Solid(inputs[0].Width * 2, inputs[0].Height * 2, new Vector4(255, 0, 0, 255)));
        var result = await AIPipelines.InpaintAsync(image, mask, new PixelRect(90, 40, 20, 20), AIPipelines.InpaintMode.Fill, "a red ball", model);
        Assert.Equal((84, 34), result.Origin);  // 30 % padding
        Assert.True(result.Image.Width == 32 && result.Image.Height == 32);
        int centre = (16 * 32 + 16) * 4;
        Assert.True(result.Image.Pixels[centre + 3] > 250);  // inside the mask: visible
        Assert.True(result.Image.Pixels[3] < 3);             // corner: (almost) transparent
        Assert.Contains("replace ONLY the white region with: a red ball.", model.Prompts[0]);
        Assert.Equal([2], model.InputCounts);
    }

    [Fact]
    public async Task RemoveAndReferencePrompts()
    {
        var image = Solid(50, 50, new Vector4(1, 2, 3, 255));
        var mask = new GrayImage(50, 50, 255f);
        var model = new FakeModel(i => i[0]);
        await AIPipelines.InpaintAsync(image, mask, new PixelRect(0, 0, 50, 50), AIPipelines.InpaintMode.Remove, "ignored", model);
        await AIPipelines.InpaintAsync(image, mask, new PixelRect(0, 0, 50, 50), AIPipelines.InpaintMode.Fill, "", model, reference: Solid(8, 8, Vector4.Zero));
        Assert.Contains("REMOVE whatever object", model.Prompts[0]);
        Assert.Contains("THIRD image is a REFERENCE", model.Prompts[1]);
        Assert.Equal([2, 3], model.InputCounts);
    }

    [Fact]
    public async Task ExpandKeepsTheOriginalInset()
    {
        var image = Solid(40, 30, new Vector4(10, 20, 30, 255));
        var model = new FakeModel(inputs => Solid(inputs[0].Width, inputs[0].Height, new Vector4(200, 200, 200, 255)));
        var margins = new AIPipelines.Margins(5, 10, 5, 10);
        var result = await AIPipelines.ExpandAsync(image, margins, "beach", model);
        Assert.True(result.Image.Width == 60 && result.Image.Height == 40);
        int inset = ((5 + 15) * 60 + 10 + 20) * 4;
        Assert.True(result.Image.Pixels[inset] == 10 && result.Image.Pixels[inset + 2] == 30);
        Assert.True(result.Image.Pixels[0] > 100);  // painted border
        Assert.EndsWith("The newly revealed area should depict: beach.", model.Prompts[0]);
    }

    [Fact]
    public async Task EditRealignsToSourceSize()
    {
        var model = new FakeModel(_ => Solid(512, 512, new Vector4(0, 0, 0, 255)));
        var result = await AIPipelines.EditAsync(Solid(300, 200, Vector4.Zero), "x", model);
        Assert.True(result.Image.Width == 300 && result.Image.Height == 200);
    }

    [Fact]
    public async Task GeneratePrompt()
    {
        var model = new FakeModel(_ => Solid(4, 4, Vector4.Zero));
        await AIPipelines.GenerateAsync("a cat", "dogs", 1024, 768, model);
        Assert.Equal("a cat\n\nAvoid: dogs\n\nTarget dimensions: 1024x768 pixels.", model.Prompts[0]);
    }

    [Fact]
    public void MarginsAreNeverNegativeAndScaleSymmetrically()
    {
        Assert.Equal(new AIPipelines.Margins(0, 0, 3, 0), new AIPipelines.Margins(-4, -1, 3, -9));
        var m = AIPipelines.Margins.Scaled(1000, 600, 1.5);
        Assert.Equal(new AIPipelines.Margins(150, 250, 150, 250), m);
        Assert.True(AIPipelines.Margins.Scaled(1000, 600, 1).IsEmpty);
        // half away from zero, as Swift's .rounded(): 10 px * 0.25 = 2.5 -> 3
        Assert.Equal(new AIPipelines.Margins(0, 3, 0, 3), AIPipelines.Margins.Scaled(10, 1, 1.5));
    }

    [Fact]
    public void ResultCacheKeysAreStableAndSensitive()
    {
        byte[][] imgs = [[1, 2, 3], [4]];
        var k = AIResultCache.Key("m", "p", imgs, null);
        Assert.Equal(64, k.Length);
        Assert.Equal(k, AIResultCache.Key("m", "p", imgs, null));
        Assert.NotEqual(k, AIResultCache.Key("m", "p", imgs, 0));
        Assert.NotEqual(k, AIResultCache.Key("m", "p2", imgs, null));
        Assert.NotEqual(k, AIResultCache.Key("m", "p", [imgs[1], imgs[0]], null));
        var dir = Path.Combine(Path.GetTempPath(), "aipipe-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AIResultCache(dir);
            Assert.Null(cache.Load(k));
            cache.Store([9, 8, 7], k);
            Assert.Equal([9, 8, 7], cache.Load(k));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class MorePipelineTests
{
    [Fact]
    public void RelightPromptMatchesWeb()
    {
        var p = AIPipelines.RelightInstruction(AIPipelines.LightDirection.Left, new SRGBColor(1, 0.9, 0.75), 0.8, "a neon alley");
        Assert.StartsWith("Relight this image. Add a dominant key light coming from the LEFT side", p);
        Assert.Contains("warm (golden/amber) light, color hex #ffe6bf", p);
        Assert.Contains("DRAMATIC", p);
        Assert.Contains("\"a neon alley\"", p);
        Assert.EndsWith("do not crop, rotate or resize the image.", p);
    }

    [Fact]
    public void EnhanceTilesCoverWithOverlap()
    {
        var tiles = AIPipelines.EnhanceTiles(2500, 900);
        Assert.Equal(3, tiles.Count);
        Assert.Equal(2500, tiles[^1].MaxX);
        Assert.Equal([new PixelRect(0, 0, 800, 600)], AIPipelines.EnhanceTiles(800, 600));
    }

    [Fact]
    public async Task HarmonizeKeepsSubjectAlpha()
    {
        var fg = new RGBAImage(10, 10, new Vector4(255, 0, 0, 0));
        for (int i = 0; i < 50; i++) fg.Pixels[i * 4 + 3] = 255;
        var bg = new RGBAImage(10, 10, new Vector4(0, 0, 255, 255));
        var model = new FakeModel(_ => new RGBAImage(20, 20, new Vector4(0, 255, 0, 255)));
        var r = await AIPipelines.HarmonizeAsync(fg, bg, 0.5, model);
        Assert.True(r.Image.Pixels[3] == 255 && r.Image.Pixels[99 * 4 + 3] == 0);
        Assert.True(MathF.Abs(r.Image.Pixels[1] - 127.5f) < 1);  // halfway toward the model's green
        Assert.Contains("Noticeably relight", model.Prompts[0]);
    }

    [Fact]
    public async Task ReflectionsConfinedToPaddedROI()
    {
        var img = new RGBAImage(200, 200, new Vector4(100, 100, 100, 255));
        var model = new FakeModel(i => i[0]);
        var r = await AIPipelines.RemoveReflectionsAsync(img, new PixelRect(50, 50, 50, 50), 0.9, model);
        Assert.True(r.Origin == (35, 35) && r.Image.Width == 80);
        Assert.StartsWith("Work ONLY on the reflective surface in this crop.", model.Prompts[0]);
        Assert.Contains("COMPLETELY", model.Prompts[0]);
    }

    [Fact]
    public async Task ReflectionsOnTheWholeImageKeepAlpha()
    {
        var img = new RGBAImage(40, 30, new Vector4(100, 100, 100, 200));
        var model = new FakeModel(i => i[0]);
        var r = await AIPipelines.RemoveReflectionsAsync(img, null, 0.1, model);
        Assert.True(r.Origin == (0, 0) && r.Image.Width == 40);
        Assert.StartsWith("Work across the whole image.", model.Prompts[0]);
        Assert.Contains("Gently knock back", model.Prompts[0]);
        Assert.Equal(200f, r.Image.Pixels[3]);
    }

    [Fact]
    public async Task EnhanceBlendsTilesAndReportsProgress()
    {
        var img = new RGBAImage(1500, 300, new Vector4(80, 80, 80, 255));
        var model = new FakeModel(i => new RGBAImage(i[0].Width, i[0].Height, new Vector4(90, 90, 90, 255)));
        var seen = new List<double>();
        var r = await AIPipelines.EnhanceAsync(img, 0.5, model, seen.Add);
        Assert.Equal(2, model.Prompts.Count);
        Assert.Equal([0.5, 1.0], seen);
        Assert.Equal(0.02, r.Cost!.Value, 6);
        Assert.Equal((1500, 300), (r.Image.Width, r.Image.Height));
        // each tile was colour-matched back to the base (gain/offset fitted on a constant: offset only), so the picture stays 80
        Assert.All(r.Image.Pixels.Where((_, i) => i % 4 == 0), v => Assert.InRange(v, 79f, 91f));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AIPipelines.EnhanceAsync(img, 0.5, model, null, new CancellationToken(true)));
    }

    [Fact]
    public void ParsesAndSanitizesDistractions()
    {
        var reply = """
            ```json
            {"distractions": [
              {"id": "d1", "label": "power line", "severity": "high", "box": {"x": 0.1, "y": 0.2, "width": 0.5, "height": 0.1}},
              {"label": "edge", "box": {"x": 0.9, "y": 0.9, "width": 0.5, "height": 0.5}},
              {"label": "empty", "box": {"x": 0.5, "y": 0.5, "width": 0, "height": 0.2}}
            ], "message": "Found 2"}
            ```
            """;
        var (items, message) = AIPipelines.ParseDistractions(reply);
        Assert.Equal(2, items.Count);
        Assert.Equal("power line", items[0].Label);
        Assert.True(Math.Abs(items[1].Box.Width - 0.1) < 1e-9);  // clamped to stay inside
        Assert.Equal("d2", items[1].Id);
        Assert.Equal("Found 2", message);
        Assert.Equal(new PixelRect(100, 100, 500, 50), items[0].PixelRect(1000, 500));
        // Not JSON at all, and a list with one element that is not an object (all or nothing, as `as? [[String: Any]]`).
        Assert.Equal("The model's reply couldn't be read.", AIPipelines.ParseDistractions("sorry").Message);
        Assert.Empty(AIPipelines.ParseDistractions("""{"distractions": [{"box": {"x":0,"y":0,"width":1,"height":1}}, 5]}""").Items);
    }

    [Fact]
    public void TheDistractionPromptIsTheWebPrompt()
    {
        Assert.StartsWith("You are a photo-retouching assistant for an AI image editor.", AIPipelines.DistractionSystemPrompt);
        Assert.EndsWith("""If the image has no distracting elements, return {"distractions": [], "message": "No obvious distractions found."}.""", AIPipelines.DistractionSystemPrompt);
        Assert.Contains("\n  \"distractions\": [\n    {\n      \"id\": \"d1\",", AIPipelines.DistractionSystemPrompt);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "pipelines_dump.txt"), string.Join("\n=====\n", new[]
        {
            AIPipelines.DistractionSystemPrompt,
            AIPipelines.InpaintInstruction(AIPipelines.InpaintMode.Remove, "X"), AIPipelines.InpaintInstruction(AIPipelines.InpaintMode.Fill, ""),
            AIPipelines.ReferenceInstruction("X"), AIPipelines.OutpaintInstruction("X"),
            AIPipelines.ReflectionsInstruction(0.9, true), AIPipelines.ReflectionsInstruction(0.5, false), AIPipelines.ReflectionsInstruction(0.1, false),
            AIPipelines.HarmonizeInstruction(0.9), AIPipelines.EnhanceInstruction(0.7), AIPipelines.EnhanceInstruction(0.4), AIPipelines.EnhanceInstruction(0.1),
            AIPipelines.RelightInstruction(AIPipelines.LightDirection.Behind, new SRGBColor(0.5, 0.5, 1), 0.5, ""),
            AIPipelines.RelightInstruction(AIPipelines.LightDirection.Bottom, SRGBColor.White, 0.1, "E"),
        }));
    }

    [Fact]
    public void EveryLightDirectionHasItsPhrases()
    {
        foreach (var d in LightDirections.AllCases)
        {
            var p = AIPipelines.RelightInstruction(d, SRGBColor.White, 0.5, "");
            Assert.Contains(d.Phrase(), p);
            Assert.Contains(d.Shadows(), p);
        }
        Assert.Equal("left", AIPipelines.LightDirection.Left.RawValue());
        Assert.Equal("Behind", AIPipelines.LightDirection.Behind.Title());
        // intensity thresholds and colour temperature
        Assert.Contains("SUBTLE", AIPipelines.RelightInstruction(AIPipelines.LightDirection.Top, SRGBColor.White, 0.39, ""));
        Assert.Contains("clearly visible", AIPipelines.RelightInstruction(AIPipelines.LightDirection.Top, SRGBColor.White, 0.4, ""));
        Assert.Contains("cool (blue/daylight) light, color hex #8080ff", AIPipelines.RelightInstruction(AIPipelines.LightDirection.Top, new SRGBColor(0.5, 0.5, 1), 0.5, ""));
        Assert.Contains("neutral white light, color hex #ffffff", AIPipelines.RelightInstruction(AIPipelines.LightDirection.Top, SRGBColor.White, 0.5, ""));
    }
}
