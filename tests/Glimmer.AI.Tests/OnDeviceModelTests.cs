// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/OnDeviceModelTests.swift
// Runs against pre-downloaded models: set GLIMMER_MODELS_DIR to a folder laid out as the models folder
// (<register id>/onnx/…; ModelDownloadsTests.DownloadsEveryModelFromItsPinnedCommit makes one). Without it every test here
// is skipped and nothing is loaded. Ported: magicSelectFindsTheClickedObject, depthHasStructure, and noPeopleInALandscape
// from SmartToolsTests; the rest is new (the Mac's Vision features need no models there).
using System.Diagnostics;
using Glimmer.AI;
using Glimmer.Core;
using Xunit.Abstractions;

namespace Glimmer.AI.Tests;

[Collection("On-device models")]
public class OnDeviceModelTests(ITestOutputHelper output)
{
    private static ModelStore Store() => new(Path.Combine(Path.GetTempPath(), "glimmer-test-models-empty"));

    [ModelsFact]
    public async Task MagicSelectFindsTheClickedObject()
    {
        var selector = new MagicSelector(Store());
        var image = TestPictures.Mug();
        var clock = Stopwatch.StartNew();
        await selector.PrepareAsync(image, key: 1);
        var encode = clock.Elapsed;
        // The mug spans x 240..<400, y (top-down) 150..<360.
        var mask = await selector.SegmentAsync([new MagicSelector.Point(new Vec2(320, 250), positive: true)]);
        output.WriteLine($"SAM encode {encode.TotalSeconds:0.00} s; mask {mask.Width}×{mask.Height}");
        Assert.True(mask.Width == 640 && mask.Height == 480);
        Assert.True(TestPictures.At(mask, 320, 250) > 200);  // clicked point
        Assert.True(TestPictures.At(mask, 40, 40) < 50);     // sky corner
    }

    [ModelsFact]
    public async Task DepthHasStructure()
    {
        var depth = await DepthEstimator.DepthAsync(TestPictures.Mug(), Store());
        Assert.True(depth.Width == 640 && depth.Height == 480);
        float lo = depth.Values.Min(), hi = depth.Values.Max();
        Assert.True(lo < 5 && hi > 250);  // normalized to the full range
    }

    [ModelsFact]
    public async Task NoPeopleInALandscape()
    {
        await Assert.ThrowsAsync<SegmentationError>(() => SubjectSegmentation.PeopleMaskAsync(TestPictures.GreyScene(), Store()));
        await Assert.ThrowsAsync<SegmentationError>(() => SubjectSegmentation.PeopleMaskAsync(TestPictures.Mug(), Store()));
    }

    // MARK: New in the port

    [ModelsFact]
    public async Task TheSubjectIsTheMug()
    {
        var mask = await SubjectSegmentation.SubjectMaskAsync(TestPictures.Mug(), Store());
        Assert.True(mask.Width == 640 && mask.Height == 480);
        Assert.True(TestPictures.At(mask, 320, 250) > 200);
        Assert.True(TestPictures.At(mask, 40, 40) < 50);
        Assert.True(TestPictures.At(mask, 100, 420) < 50);  // the table
    }

    [ModelsFact]
    public async Task APlainWallHasNoSubject()
    {
        var wall = new RGBAImage(400, 300, new System.Numerics.Vector4(180, 180, 175, 255));
        await Assert.ThrowsAsync<SegmentationError>(() => SubjectSegmentation.SubjectMaskAsync(wall, Store()));
    }

    /// <summary>Select Sky as the Mac's window does it: depth and prompts on the picture, SAM draws the outline.</summary>
    [ModelsFact]
    public async Task SelectSkyFindsTheSky()
    {
        var store = Store();
        var image = TestPictures.Landscape(tilt: 0);
        var depth = await DepthEstimator.DepthAsync(image, store);
        var points = SkySelection.Prompts(image, depth);
        Assert.NotNull(points);
        var selector = new MagicSelector(store);
        await selector.PrepareAsync(image, key: 7);
        var mask = await selector.SegmentAsync(points!);
        Assert.True(TestPictures.At(mask, 400, 100) > 200, $"{TestPictures.At(mask, 400, 100)}");
        Assert.True(TestPictures.At(mask, 400, 500) < 50, $"{TestPictures.At(mask, 400, 500)}");
    }

    /// <summary>A name or none, never an error, and a name the way the Mac writes them (capitalised).</summary>
    [ModelsFact]
    public async Task ALayerNameIsCapitalisedOrAbsent()
    {
        var name = await ImageLabel.NameAsync(TestPictures.Mug(), Store());
        output.WriteLine($"name: {name ?? "(none)"}");
        Assert.True(name is null || (name.Length > 0 && char.IsUpper(name[0])));
    }
}

/// <summary>
/// Speed on this computer for a 12-megapixel photo (4000 × 3000), every step the app takes: the picture scaled to the
/// model's size, the run, the mask scaled back. GLIMMER_PERF=1 and GLIMMER_MODELS_DIR.
/// </summary>
[Collection("On-device models")]
public class OnDeviceModelPerformance(ITestOutputHelper output)
{
    [PerfFact]
    public async Task TwelveMegapixels()
    {
        var image = TestPictures.TwelveMegapixels();
        var store = new ModelStore(Path.Combine(Path.GetTempPath(), "glimmer-test-models-empty"));
        async Task<double> Time(string what, Func<Task> run)
        {
            await run();  // the first run loads the model
            var clock = Stopwatch.StartNew();
            await run();
            output.WriteLine($"{what}: {clock.Elapsed.TotalSeconds:0.00} s");
            return clock.Elapsed.TotalSeconds;
        }
        await Time("Insights (horizon, saliency, tone, Auto Enhance, colour cast)", () => SmartAnalysis.InsightsAsync(image));
        await Time("Rate (technical score, sharpness, utility)", () => Task.Run(() => PhotoQuality.Rate(image)));
        await Time("Document detection", () => Task.Run(() => DocumentDetection.QuadIn(image)));
        if (!store.IsInstalled(OnDeviceModel.Subject)) return;
        await Time("Subject (IS-Net)", () => SubjectSegmentation.SubjectMaskAsync(image, store));
        await Time("Depth (Depth Anything V2 Small)", () => DepthEstimator.DepthAsync(image, store));
        var selector = new MagicSelector(store);
        int key = 0;
        await Time("SAM 2.1 encode", () => selector.PrepareAsync(image, ++key));
        await Time("SAM 2.1 decode (one click)", () => selector.SegmentAsync([new MagicSelector.Point(new Vec2(2000, 1500), positive: true)]));
        await Time("Layer name (MobileNetV4)", () => ImageLabel.NameAsync(image, store));
        // No people in this picture: the detector runs and the matting doesn't.
        await Time("People detection (RT-DETRv2)", async () =>
        {
            try { await SubjectSegmentation.PeopleMaskAsync(image, store); } catch (SegmentationError) { }
        });
        var matting = await OnnxModels.SessionAsync(store, OnDeviceModel.PersonMatting, "onnx", "model_fp16.onnx", default);
        await Time("People matting (MODNet, whole picture)", () => Task.Run(() => SubjectSegmentation.Matte(matting, image)));
    }
}
