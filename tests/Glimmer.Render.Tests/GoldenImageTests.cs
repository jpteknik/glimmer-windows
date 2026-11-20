// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/GoldenImageTests.swift
using System.Collections.Immutable;
using Glimmer.Core;
using Xunit.Abstractions;

namespace Glimmer.Render.Tests;

/// <summary>
/// Golden-image tests: every blend mode, adjustment, filter and layer effect is rendered on a fixed scene and compared
/// with a checked-in reference in CIELAB. A render passes when the mean ΔE (CIE76) is below 0.5 and 99 % of pixels are
/// below 2.
/// </summary>
/// <remarks>
/// The references in <c>Golden/</c> were recorded on the Mac; they are never re-recorded here. Each scene goes through
/// the real document model and compositor.
/// </remarks>
public class GoldenImageTests(ITestOutputHelper output)
{
    private void Check(string name, DocumentState state)
    {
        var (mean, p99, max) = GoldenHarness.Compare(name, GoldenHarness.Render(state));
        output.WriteLine($"{name}: mean ΔE {mean:F3}, p99 {p99:F3}, max {max:F3}");
        Assert.True(mean < 0.5 && p99 < 2, $"{name}: mean ΔE {mean:F3}, p99 {p99:F3}");
    }

    // MARK: Cases

    public static TheoryData<BlendMode> BlendModes => [.. BlendMode.AllCases.Where(m => m != BlendMode.PassThrough)];

    [Theory]
    [MemberData(nameof(BlendModes))]
    public void BlendModeCase(BlendMode mode) =>
        Check("blend-" + mode.RawValue(), GoldenHarness.State(GoldenHarness.BaseLayer(), GoldenHarness.ShapeLayer(mode)));

    /// <summary>Hand-picked, clearly visible but unsaturated settings, so every reference keeps detail a regression would change.</summary>
    private static readonly Dictionary<AdjustmentKind, Dictionary<string, double>> Settings = new()
    {
        [AdjustmentKind.BrightnessContrast] = new() { ["brightness"] = 0.15, ["contrast"] = 0.3 },
        [AdjustmentKind.Exposure] = new() { ["exposure"] = 0.6, ["gamma"] = 1.2 },
        [AdjustmentKind.HueSaturation] = new() { ["hue"] = 60, ["saturation"] = 0.2, ["lightness"] = -0.1 },
        [AdjustmentKind.Vibrance] = new() { ["vibrance"] = 0.5, ["saturation"] = -0.2 },
        [AdjustmentKind.ColorBalance] = new() { ["midtonesR"] = 0.4, ["shadowsB"] = 0.3, ["highlightsG"] = -0.2 },
        [AdjustmentKind.BlackWhite] = new() { ["red"] = 0.9, ["blue"] = -0.2 },
        [AdjustmentKind.PhotoFilter] = new() { ["density"] = 0.5 },
        [AdjustmentKind.ChannelMixer] = new() { ["rr"] = 0.8, ["rg"] = 0.3, ["bb"] = 0.7, ["gc"] = 0.05 },
        [AdjustmentKind.Posterize] = new() { ["levels"] = 6 },
        [AdjustmentKind.Threshold] = new() { ["level"] = 0.35 },
    };

    internal static Adjustment Adjusted(AdjustmentKind kind)
    {
        var a = new Adjustment(kind);
        if (Settings.TryGetValue(kind, out var numbers))
            a = a with { Numbers = a.Numbers.SetItems(numbers) };
        return kind switch
        {
            AdjustmentKind.Curves => a with
            {
                Curves = a.Curves.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0.05), new CurvePoint(0.3, 0.2), new CurvePoint(0.7, 0.85), new CurvePoint(1, 1)]),
            },
            AdjustmentKind.Levels => a with { Levels = a.Levels.SetItem(CurveChannel.Rgb, new LevelsChannel(InBlack: 0.1, InWhite: 0.85, Gamma: 1.4)) },
            _ => a,
        };
    }

    public static TheoryData<AdjustmentKind> AdjustmentKinds => [.. AdjustmentKind.AllCases];

    [Theory]
    [MemberData(nameof(AdjustmentKinds))]
    public void AdjustmentCase(AdjustmentKind kind)
    {
        var layer = new Layer(kind.Title(), new AdjustmentContent(Adjusted(kind)));
        Check("adjustment-" + kind.RawValue(), GoldenHarness.State(GoldenHarness.BaseLayer(), GoldenHarness.ShapeLayer(), layer));
    }

    public static TheoryData<FilterKind> FilterKinds => [.. FilterKind.AllCases];

    [Theory]
    [MemberData(nameof(FilterKinds))]
    public void FilterCase(FilterKind kind)
    {
        var baseLayer = GoldenHarness.BaseLayer();
        var frame = new PixelRect(0, 0, GoldenHarness.Size, GoldenHarness.Size);
        baseLayer = baseLayer with { Tiles = new FilterEngine().Apply(new FilterSettings(kind), baseLayer.Tiles, frame, null) };
        Check("filter-" + kind.RawValue(), GoldenHarness.State(baseLayer));
    }

    [Theory]
    [InlineData("dropShadow")]
    [InlineData("innerShadow")]
    [InlineData("outerGlow")]
    [InlineData("stroke")]
    [InlineData("colorOverlay")]
    public void EffectCase(string name)
    {
        var fx = name switch
        {
            "dropShadow" => new LayerEffects { DropShadow = new ShadowEffect() },
            "innerShadow" => new LayerEffects { InnerShadow = new ShadowEffect() },
            "outerGlow" => new LayerEffects { OuterGlow = new GlowEffect() },
            "stroke" => new LayerEffects { Stroke = new StrokeEffect() },
            _ => new LayerEffects { ColorOverlay = new ColorOverlayEffect() },
        };
        var shapes = GoldenHarness.ShapeLayer() with { Effects = fx };
        Check("effect-" + name, GoldenHarness.State(GoldenHarness.BaseLayer(), shapes));
    }

    [Fact]
    public void ComparisonCatchesDifferences()
    {
        var a = Enumerable.Repeat((byte)128, 16).ToArray();
        var b = a.Select(v => (byte)(v + 20)).ToArray();
        Assert.Equal(0, GoldenHarness.DeltaE(a, a).mean);
        Assert.True(GoldenHarness.DeltaE(a, b).mean > 2);
    }

    /// <summary>The criterion fails a render that is visibly wrong (a blend mode swapped for another), so passing means something.</summary>
    [Fact]
    public void WrongRenderFailsTheCriterion()
    {
        var (mean, p99, _) = GoldenHarness.Compare("blend-multiply",
            GoldenHarness.Render(GoldenHarness.State(GoldenHarness.BaseLayer(), GoldenHarness.ShapeLayer(BlendMode.Screen))));
        output.WriteLine($"screen against the multiply reference: mean {mean:F3}, p99 {p99:F3}");
        Assert.False(mean < 0.5 && p99 < 2);
    }
}
