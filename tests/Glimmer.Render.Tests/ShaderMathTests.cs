// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift (BlendModeTests) and
// LayerTreeRenderTests.swift: their cases on the shader functions of Composite.metal directly, plus the per-mode row
// loops against the per-pixel formula.
using System.Numerics;
using Glimmer.Core;
using static Glimmer.Render.Tests.TestSupport;

namespace Glimmer.Render.Tests;

public class ShaderMathTests
{
    private static Vector4 Grey(float v) => new(v, v, v, 1);

    private static Vector4 Layer(Vector4 dst, Vector4 src, int mode, float opacity = 1, bool gamma = false) =>
        CompositeShaders.CompositeLayerFragment(dst, src, new LayerUniforms { Mode = mode, Opacity = opacity, GammaSpace = gamma ? 1 : 0 }, 1, 1, 1);

    public static TheoryData<BlendMode> PixelModes => [.. BlendMode.PixelModes];

    /// <summary>The shader's blending against the W3C reference, without the compositor around it.</summary>
    [Theory]
    [MemberData(nameof(PixelModes))]
    public void BlendFormulaMatchesTheReference(BlendMode mode)
    {
        (Vector3 cb, Vector3 cs)[] cases =
        [
            (new(0.2f, 0.5f, 0.8f), new(0.7f, 0.3f, 0.1f)),
            (new(0.9f, 0.1f, 0.4f), new(0.25f, 0.6f, 0.95f)),
        ];
        foreach (var (cb, cs) in cases)
        {
            const float ab = 0.8f, a = 0.6f, opacity = 0.75f;
            Vector4 got = Layer(new Vector4(cb * ab, ab), new Vector4(cs * a, a), mode.ShaderIndex(), opacity);
            Vector4 want = Reference.Composite(cb, ab, cs, a, opacity, mode);
            Assert.True(Close(got, want), $"{mode}: got {got}, want {want}");
        }
    }

    /// <summary>The per-mode row loops give what the per-pixel formula gives, in both blend spaces.</summary>
    [Fact]
    public void RowLoopsEqualThePerPixelFormula()
    {
        var rng = new Random(7);
        var src = new Vector4[256];
        var dst0 = new Vector4[256];
        var k = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float a = i % 17 == 0 ? 0 : (float)rng.NextDouble(), b = i % 13 == 0 ? 0 : (float)rng.NextDouble();
            src[i] = new Vector4((float)rng.NextDouble() * a, (float)rng.NextDouble() * a, (float)rng.NextDouble() * a, a);
            dst0[i] = new Vector4((float)rng.NextDouble() * b, (float)rng.NextDouble() * b, (float)rng.NextDouble() * b, b);
            k[i] = i % 11 == 0 ? 0 : (float)rng.NextDouble();
        }
        foreach (bool gamma in new[] { false, true })
        {
            for (int mode = 0; mode <= 25; mode++)
            {
                var rows = (Vector4[])dst0.Clone();
                BlendRows.Blend(rows, src, k, mode, gamma);
                for (int x = 0; x < 256; x++)
                {
                    var want = CompositeShaders.BlendOver(dst0[x], src[x] * k[x], mode, gamma);
                    Assert.True(Vector4.Distance(want, rows[x]) < 2e-5f, $"mode {mode} gamma {gamma} at {x}: {rows[x]} vs {want}");
                }
            }
        }
    }

    [Fact]
    public void NormalFastPathEqualsTheGeneralFormula()
    {
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            float ab = (float)rng.NextDouble(), @as = (float)rng.NextDouble();
            var dst = new Vector4(new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * ab, ab);
            var src = new Vector4(new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * @as, @as);
            Vector4 fast = CompositeShaders.BlendOver(dst, src, 0, false);
            Vector4 want = Reference.Composite(ab > 0 ? dst.Xyz() / ab : Vector3.Zero, ab, src.Xyz() / @as, @as, 1, BlendMode.Normal);
            Assert.True(Close(fast, want, 1e-5f), $"{dst} {src}");
        }
    }

    [Fact]
    public void LutSamplingIsLinearBetweenEntriesAndClamped()
    {
        Vector4[] lut = [new(0), new(1), new(4)];
        Assert.Equal(0f, CompositeShaders.SampleLut(lut, -1).X);
        Assert.Equal(4f, CompositeShaders.SampleLut(lut, 2).X);
        Assert.Equal(0.5f, CompositeShaders.SampleLut(lut, 0.25f).X, 5);
        Assert.Equal(2.5f, CompositeShaders.SampleLut(lut, 0.75f).X, 5);
    }

    [Fact]
    public void PassThroughMixPullsTowardTheBackdrop()
    {
        var red = new Vector4(0.8f, 0.2f, 0.2f, 1);
        var backdrop = Grey(0.5f);
        var children = Layer(backdrop, red, 1);  // multiply straight into the backdrop
        Assert.True(Close(children, new Vector4(0.4f, 0.1f, 0.1f, 1)));
        var half = CompositeShaders.PassthroughMixFragment(children, backdrop, new LayerUniforms { Opacity = 0.5f }, 1, 1, 1);
        Assert.True(Close(half, new Vector4(0.45f, 0.3f, 0.3f, 1)));
    }

    [Fact]
    public void MaskDensity()
    {
        var u = new LayerUniforms { MaskDensity = 1 };
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), Grey(1), u, srcMask: 0, 1, 1), Grey(0)));
        u.MaskDensity = 0.5f;
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), Grey(1), u, srcMask: 0, 1, 1), Grey(0.5f)));
        u.MaskDensity = 0;  // a disabled mask resolves to none: density 0
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), Grey(1), u, srcMask: 0, 1, 1), Grey(1)));
    }

    [Fact]
    public void ClipUsesTheBaseAlphaAndItsMask()
    {
        var u = new LayerUniforms { ClipDensity = 1 };
        var red = new Vector4(1, 0, 0, 1);
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), red, u, 1, clip: 1, clipMask: 1), red));
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), red, u, 1, clip: 0, clipMask: 1), Grey(0)));
        Assert.True(Close(CompositeShaders.CompositeLayerFragment(Grey(0), red, u, 1, clip: 1, clipMask: 0), Grey(0)));
    }

    [Fact]
    public void ColorOverlayReplacesColourKeepingAlpha()
    {
        var u = new LayerUniforms { Overlay = new Vector4(0, 1, 0, 1) };
        var got = CompositeShaders.CompositeLayerFragment(Grey(1), new Vector4(0, 0, 1, 1), u, 1, 1, 1);
        Assert.True(got.Y > 0.99f && got.Z < 0.01f);
        u.Overlay = new Vector4(0, 1, 0, 0.5f);
        var mixed = CompositeShaders.CompositeLayerFragment(Grey(1), new Vector4(0, 0, 1, 1), u, 1, 1, 1);
        Assert.True(mixed.Y > 0.2f && mixed.Z > 0.2f && mixed.X < 0.05f);
        // A transparent pixel stays transparent.
        Assert.Equal(Vector4.Zero, CompositeShaders.ApplyOverlay(Vector4.Zero, new Vector4(1, 0, 0, 1), 0));
    }

    [Fact]
    public void PatternsAndGradients()
    {
        var checker = new LayerUniforms { SourceKind = 3, PatternKind = 0, PatternScale = 10, PatternForeground = new Vector4(0, 0, 0, 1), PatternBackground = Vector4.One };
        var a = CompositeShaders.ProceduralSource(new Vector2(5.5f, 5.5f), checker, []);
        var b = CompositeShaders.ProceduralSource(new Vector2(15.5f, 5.5f), checker, []);
        Assert.NotEqual(a.X, b.X);
        Assert.Equal(1f, a.W);

        // Interpolated in sRGB (like Photoshop): halfway is sRGB 0.5. The table holds straight sRGB with opacity.
        var lut = DocumentCompositor.GradientTable(GradientStop.BlackToWhite);
        var linear = new LayerUniforms { SourceKind = 1, GradientStart = Vector2.Zero, GradientEnd = new Vector2(256, 0) };
        var mid = CompositeShaders.ProceduralSource(new Vector2(128.5f, 0.5f), linear, lut);
        Assert.True(MathF.Abs(SrgbEncode(mid.X) - 0.5f) < 0.01f);
        Assert.True(CompositeShaders.ProceduralSource(new Vector2(400.5f, 0.5f), linear, lut).X > 0.99f);
        linear.GradientReverse = 1;
        Assert.True(CompositeShaders.ProceduralSource(new Vector2(400.5f, 0.5f), linear, lut).X < 0.01f);

        var radial = new LayerUniforms { SourceKind = 2, GradientStart = new Vector2(100, 100), GradientEnd = new Vector2(200, 100) };
        Assert.True(CompositeShaders.ProceduralSource(new Vector2(100, 100), radial, lut).X < 0.01f);
        Assert.True(CompositeShaders.ProceduralSource(new Vector2(100, 250), radial, lut).X > 0.99f);

        // Dots, stripes, grid, noise: opaque, two colours.
        foreach (int kind in new[] { 1, 2, 3, 4 })
        {
            var u = checker;
            u.PatternKind = kind;
            for (float x = 0.5f; x < 64; x += 3.7f)
            {
                var c = CompositeShaders.ProceduralSource(new Vector2(x, x * 0.6f), u, []);
                Assert.Equal(1f, c.W);
                Assert.True(c.X is >= 0 and <= 1);
            }
        }
    }

    /// <summary>The cache key of an adjustment covers its curve points; the record's own hash only counts curves.</summary>
    [Fact]
    public void AdjustmentContentHashSeesCurvePoints()
    {
        var a = new Adjustment(AdjustmentKind.Curves);
        var b = a with { Curves = a.Curves.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, 0.6), new CurvePoint(1, 1)]) };
        var c = a with { Curves = a.Curves.SetItem(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(0.5, 0.7), new CurvePoint(1, 1)]) };
        Assert.NotEqual(ContentHash.Of(b), ContentHash.Of(c));
        Assert.Equal(ContentHash.Of(b), ContentHash.Of(b with { }));
        Assert.NotEqual(TilePipelines.Lut(b)[700].X, TilePipelines.Lut(c)[700].X);
    }
}
