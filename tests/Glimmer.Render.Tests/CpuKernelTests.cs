// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/LayerTreeRenderTests.swift, FilterEngineTests.swift and
// GlimmerToolsTests/PaintingTests.swift: their cases run on the CPU kernels that replace the GPU and Metal Performance
// Shaders (Gaussian blur, distance transform, Lanczos, mip sampling, smart-object resampling, dab stamping, pixel ops,
// selection ops, layer styles, liquify, thumbnails), plus checks of those kernels against brute force.
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

public class CpuKernelTests
{
    // MARK: Gaussian (MPSImageGaussianBlur)

    [Theory]
    [InlineData(0.5f)]
    [InlineData(2f)]
    [InlineData(12.5f)]
    [InlineData(40f)]
    public void GaussianMatchesBruteForceWithClampedEdges(float sigma)
    {
        var rng = new Random(3);
        int w = 61, h = 47;
        var src = new FloatImage(w, h);
        for (int i = 0; i < src.Pixels.Length; i++) src.Pixels[i] = (float)rng.NextDouble();
        var dst = new FloatImage(w, h);
        GaussianBlur.Blur(src, dst, sigma);
        var k = GaussianBlur.Kernel(sigma);
        int r = k.Length - 1;
        foreach (var (x, y) in new[] { (0, 0), (30, 20), (60, 46), (5, 40) })
        {
            double sum = 0;
            for (int j = -r; j <= r; j++)
                for (int i = -r; i <= r; i++)
                    sum += k[Math.Abs(i)] * k[Math.Abs(j)] * src.Read(x + i, y + j);
            Assert.True(Math.Abs(sum - dst.Pixels[y * w + x]) < 1e-4, $"σ {sigma} at {x},{y}: {dst.Pixels[y * w + x]} vs {sum}");
        }
    }

    [Fact]
    public void GaussianKernelIsNormalizedAndWideEnough()
    {
        foreach (float sigma in new[] { 0.3f, 1f, 5f, 25f, 100f })
        {
            var k = GaussianBlur.Kernel(sigma);
            double total = k[0] + 2 * k.Skip(1).Sum(v => (double)v);
            Assert.True(Math.Abs(total - 1) < 1e-5, $"σ {sigma}: {total}");
            Assert.True(k.Length - 1 >= 3 * sigma);
        }
    }

    [Fact]
    public void GaussianOfRgbaEqualsChannelwise()
    {
        var rng = new Random(5);
        var src = new RegionImage(33, 29);
        for (int i = 0; i < src.Pixels.Length; i++)
            src.Pixels[i] = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
        var dst = new RegionImage(33, 29);
        GaussianBlur.Blur(src, dst, 3.3f);
        var channel = new FloatImage(33, 29);
        for (int i = 0; i < channel.Pixels.Length; i++) channel.Pixels[i] = src.Pixels[i].Z;
        var blurred = new FloatImage(33, 29);
        GaussianBlur.Blur(channel, blurred, 3.3f);
        for (int i = 0; i < channel.Pixels.Length; i++) Assert.Equal(blurred.Pixels[i], dst.Pixels[i].Z, 5);
    }

    // MARK: Distance transform (MPSImageEuclideanDistanceTransform)

    [Fact]
    public void DistanceTransformMatchesBruteForce()
    {
        var rng = new Random(11);
        int w = 40, h = 31;
        var input = new float[w * h];
        for (int i = 0; i < input.Length; i++) input[i] = rng.NextDouble() < 0.03 ? 1 : 0;
        var output = new float[w * h];
        DistanceTransform.Compute(input, w, h, output);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double best = double.MaxValue;
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                        if (input[j * w + i] != 0) best = Math.Min(best, Math.Sqrt((x - i) * (x - i) + (y - j) * (y - j)));
                Assert.True(Math.Abs(best - output[y * w + x]) < 1e-4, $"{x},{y}: {output[y * w + x]} vs {best}");
            }
    }

    [Fact]
    public void DistanceTransformWithoutFeatures()
    {
        var output = new float[16];
        DistanceTransform.Compute(new float[16], 4, 4, output);
        Assert.All(output, d => Assert.Equal(DistanceTransform.NoFeature, d));
    }

    // MARK: Selection ops (PaintingTests.growShrinkFeather, on the region)

    private static FloatImage Rectangle(int w, int h, int x0, int y0, int x1, int y1)
    {
        var img = new FloatImage(w, h);
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) img.Pixels[y * w + x] = 1;
        return img;
    }

    [Fact]
    public void GrowShrinkFeather()
    {
        var sel = Rectangle(512, 512, 100, 100, 200, 200);
        var grown = SelectionOps.GrowOrShrink(sel, 20, grow: true);
        Assert.True(grown.Pixels[150 * 512 + 85] > 0.99f);
        Assert.True(grown.Pixels[150 * 512 + 75] < 0.01f);
        var shrunk = SelectionOps.GrowOrShrink(sel, 20, grow: false);
        Assert.True(shrunk.Pixels[150 * 512 + 110] < 0.01f);
        Assert.True(shrunk.Pixels[150 * 512 + 130] > 0.99f);
        var feathered = SelectionOps.Feather(sel, 20);
        float edge = feathered.Pixels[150 * 512 + 100];
        Assert.True(edge > 0.3f && edge < 0.7f);
    }

    // MARK: Brush dabs and pixel ops (PaintingTests.StrokeEngineTests, at one tile)

    [Fact]
    public void FlowBuildsUp()
    {
        var wet = new float[256 * 256];
        var dab = new DabInstance(new Vector2(50, 50), 10, 1, flow: 0.2f);
        StrokeEngine.Stamp(wet, 256, Vector2.Zero, [dab]);
        Assert.True(MathF.Abs(wet[50 * 256 + 50] - 0.2f) < 0.01f);
        StrokeEngine.Stamp(wet, 256, Vector2.Zero, [dab]);
        Assert.True(MathF.Abs(wet[50 * 256 + 50] - 0.36f) < 0.01f);
    }

    [Fact]
    public void OpacityCapsTheStroke()
    {
        var u = new ApplyUniforms { Mode = 0, Opacity = 0.5f, Color = new Vector4(1, 0, 0, 1) };
        // However much coverage builds up, the result is capped at the opacity.
        var p = CompositeShaders.PixelApplyFragment(Vector4.Zero, wet: 1.7f, s: 1, Vector4.Zero, u);
        Assert.True(MathF.Abs(p.X - 0.5f) < 0.01f && MathF.Abs(p.W - 0.5f) < 0.01f);
    }

    [Fact]
    public void EraseAndSelectionLimit()
    {
        var u = new ApplyUniforms { Mode = 1, Opacity = 1 };
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 1, s: 1, Vector4.One, u).W < 0.01f);  // erased inside
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 1, s: 0, Vector4.One, u).W > 0.99f);  // protected outside
    }

    [Fact]
    public void PaintsMasksAndKeepsSelected()
    {
        var mask = new ApplyUniforms { Mode = 2, Opacity = 1, MaskTarget = 0 };
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 1, 1, Vector4.One, mask).W < 0.01f);  // painted black
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 0, 1, Vector4.One, mask).W > 0.99f);  // untouched stays white
        var keep = new ApplyUniforms { Mode = 3, Opacity = 1 };
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 0, 1, Vector4.One, keep).W > 0.99f);
        Assert.True(CompositeShaders.PixelApplyFragment(Vector4.One, 0, 0, Vector4.One, keep).W < 0.01f);
        var toward = new ApplyUniforms { Mode = 4, Opacity = 1 };
        Assert.Equal(new Vector4(0.25f), CompositeShaders.PixelApplyFragment(Vector4.Zero, 0.5f, 1, new Vector4(0.5f), toward));
    }

    [Fact]
    public void HardDabHasAOnePixelEdge()
    {
        var dab = new DabInstance(new Vector2(100.5f, 100.5f), 10, 1);
        Assert.Equal(1f, CompositeShaders.DabFragment(dab, new Vector2(100.5f, 100.5f)));
        Assert.Equal(0.5f, CompositeShaders.DabFragment(dab, new Vector2(110.5f, 100.5f)), 4);  // on the radius
        Assert.Equal(0f, CompositeShaders.DabFragment(dab, new Vector2(111.5f, 100.5f)));
        // Roundness squeezes along the dab's own y axis; an angle turns it.
        var flat = new DabInstance(new Vector2(0, 0), 10, 1, roundness: 0.5f);
        Assert.Equal(0f, CompositeShaders.DabFragment(flat, new Vector2(0, 6)));
        Assert.Equal(1f, CompositeShaders.DabFragment(flat, new Vector2(6, 0)));
        var turned = new DabInstance(new Vector2(0, 0), 10, 1, roundness: 0.5f, angle: MathF.PI / 2);
        Assert.Equal(1f, CompositeShaders.DabFragment(turned, new Vector2(0, 6)));
    }

    [Fact]
    public void StampingAcrossATileEdgeMatchesTheNeighbour()
    {
        var dab = new DabInstance(new Vector2(254, 40), 8, 0.5f);
        var left = new float[256 * 256];
        var right = new float[256 * 256];
        StrokeEngine.Stamp(left, 256, Vector2.Zero, [dab]);
        StrokeEngine.Stamp(right, 256, new Vector2(256, 0), [dab]);
        Assert.True(left[40 * 256 + 255] > 0);
        Assert.True(right[40 * 256 + 0] > 0);
        Assert.Equal(CompositeShaders.DabFragment(dab, new Vector2(257.5f, 40.5f)), right[40 * 256 + 1]);
    }

    [Fact]
    public void DabBoundsCoverEveryTouchedPixel()
    {
        var dab = new DabInstance(new Vector2(300.3f, 77.8f), 25.5f, 0.3f);
        var b = dab.Bounds;
        for (int y = 40; y < 120; y++)
            for (int x = 260; x < 340; x++)
                if (CompositeShaders.DabFragment(dab, new Vector2(x + 0.5f, y + 0.5f)) > 0) Assert.True(b.Contains(x, y), $"{x},{y}");
    }

    // MARK: Layer styles (LayerTreeRenderTests.LayerStyleRenderTests, on the region passes)

    private static readonly PixelRect StyleRegion = new(0, 0, 512, 512);

    private static (FloatImage alpha, FloatImage mask) Square()
    {
        // A 100 × 100 opaque square at (100, 100) in a 512 × 512 region.
        var alpha = Rectangle(512, 512, 100, 100, 200, 200);
        var mask = new FloatImage(512, 512);
        Array.Fill(mask.Pixels, 1f);
        return (alpha, mask);
    }

    private static Vector4 At(TileGrid g, int x, int y) => g[new TileCoord(x / 256, y / 256)]?.PixelAt(x % 256, y % 256) ?? Vector4.Zero;

    [Fact]
    public void DropShadowFallsBelowAndOffset()
    {
        var (alpha, mask) = Square();
        double r = 135 * Math.PI / 180;  // light from the top-left → shadow to the bottom-right
        var offset = new Vector2((float)(-Math.Cos(r) * 20), (float)(Math.Sin(r) * 20));
        var field = LayerStyleRenderer.Blurred(LayerStyleRenderer.Alpha(alpha, mask, offset, 0, false), 0);
        var shadow = LayerStyleRenderer.ColorizeTiles(field, null, alpha, mask, new EffectColorUniforms { Color = new Vector4(0, 0, 0, 1) }, StyleRegion);
        Assert.True(At(shadow, 210, 210).W > 0.99f);  // bottom-right, outside the square: shadow
        Assert.True(At(shadow, 95, 95).W < 0.01f);    // top-left: none
    }

    [Fact]
    public void OutsideCentreAndInsideStrokes()
    {
        var (alpha, mask) = Square();
        var outside = LayerStyleRenderer.Distance(alpha, mask, false);
        var inside = LayerStyleRenderer.Distance(alpha, mask, true);
        TileGrid Stroke(float position) => LayerStyleRenderer.ColorizeTiles(outside, inside, alpha, mask,
            new EffectColorUniforms { Color = new Vector4(1, 0, 0, 1), Stroke = new Vector4(6, position, 1, 0) }, StyleRegion);
        var stroke = Stroke(0);
        Assert.True(At(stroke, 203, 150).X > 0.95f);   // in the ring
        Assert.True(At(stroke, 215, 150).W < 0.01f);   // beyond the stroke
        Assert.True(At(stroke, 150, 150).W < 0.01f);   // inside: no outside stroke
        // Half the width (3) each side; the pixel 3 away is the anti-aliased half step.
        var centred = Stroke(2);
        Assert.True(At(centred, 198, 150).W > 0.99f && At(centred, 201, 150).W > 0.99f && At(centred, 205, 150).W < 0.01f);
        Assert.Equal(0.5f, At(centred, 202, 150).W, 3);
        Assert.Equal(0.5f, At(centred, 197, 150).W, 3);
        var inner = Stroke(1);
        Assert.True(At(inner, 197, 150).W > 0.99f && At(inner, 203, 150).W < 0.01f && At(inner, 190, 150).W < 0.01f);
    }

    [Fact]
    public void InnerShadowStaysInsideTheShape()
    {
        var (alpha, mask) = Square();
        var field = LayerStyleRenderer.Blurred(LayerStyleRenderer.Alpha(alpha, mask, new Vector2(5, 9), 0, true), 10);
        var shadow = LayerStyleRenderer.ColorizeTiles(field, null, alpha, mask,
            new EffectColorUniforms { Color = new Vector4(0, 0, 0, 0.75f), Stroke = new Vector4(0, 0, 2, 0) }, StyleRegion);
        Assert.True(At(shadow, 103, 103).W > 0.3f);   // near the top-left edge, inside
        Assert.True(At(shadow, 190, 190).W < 0.05f);  // away from the shadowed edges: lit
        Assert.Equal(0f, At(shadow, 90, 90).W);       // nothing outside the shape
    }

    [Fact]
    public void SpreadThickensBeforeBlurring()
    {
        var a = new float[] { 0.25f };
        var m = new float[] { 1 };
        var o = new float[1];
        CompositeShaders.EffectAlpha(a, m, 1, 1, new Vector4(0, 0, 0.5f, 0), o);
        Assert.Equal(0.5f, o[0], 5);
        CompositeShaders.EffectAlpha(a, m, 1, 1, new Vector4(0, 0, 0.5f, 1), o);  // inverted, then spread
        Assert.Equal(1f, o[0], 5);
    }

    // MARK: Mip chains and smart objects

    [Fact]
    public void MipLevelsAreBoxFilteredAndSampledByLevelOfDetail()
    {
        var level0 = new Vector4[256 * 256];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++) level0[y * 256 + x] = (x + y) % 2 == 0 ? Vector4.One : Vector4.Zero;
        var chain = MipChain.Build(level0, 256, 256);
        Assert.Equal(9, chain.LevelCount);
        Assert.All(chain.Levels[1], p => Assert.Equal(0.5f, p.X, 5));
        Assert.Equal(1, chain.Widths[8]);
        // Magnified (lod ≤ 0) with nearest: exact texels. At 25 %: lod 2, all grey.
        Assert.Equal(1f, chain.Sample(new Vector2(0.5f / 256, 0.5f / 256), -1, magNearest: true, zeroOutside: false).X);
        float lod = chain.LevelOfDetail(new Vector2(4f / 256, 0), new Vector2(0, 4f / 256));
        Assert.Equal(2f, lod, 4);
        Assert.Equal(0.5f, chain.Sample(new Vector2(0.3f, 0.7f), lod, magNearest: true, zeroOutside: false).X, 4);
    }

    [Fact]
    public void SmartObjectSampling()
    {
        // A green 256 × 256 source at half size, top-left at (100, 100).
        var green = new Vector4[256 * 256];
        Array.Fill(green, new Vector4(0, 1, 0, 1));
        var chain = MipChain.Build(green, 256, 256);
        var t = new Projective(0.5, 0, 0, 0.5, 100, 100, 0, 0).Inverse();
        Assert.True(SmartSampling.Sample(chain, t, 160.5, 160.5).Y > 0.95f);
        Assert.True(SmartSampling.Sample(chain, t, 90.5, 90.5).W < 0.01f);
        Assert.True(SmartSampling.Sample(chain, t, 240.5, 240.5).W < 0.01f);
    }

    [Fact]
    public void AnIdentityPlacementReproducesTheSourceExactly()
    {
        var rng = new Random(9);
        var src = new Vector4[64 * 48];
        for (int i = 0; i < src.Length; i++) src[i] = new Vector4((float)rng.NextDouble());
        var chain = MipChain.Build(src, 64, 48);
        var t = new Projective(1, 0, 0, 1, 30, 20, 0, 0).Inverse();
        for (int y = 0; y < 48; y += 7)
            for (int x = 0; x < 64; x += 5)
                Assert.Equal(src[y * 64 + x], SmartSampling.Sample(chain, t, x + 30.5, y + 20.5));
        Assert.Equal(Vector4.Zero, SmartSampling.Sample(chain, t, 29.5, 25.5));
        Assert.Equal(Vector4.Zero, SmartSampling.Sample(chain, t, 94.5, 25.5));
    }

    [Fact]
    public void PerspectiveInverseRoundTrips()
    {
        var p = new Projective(1.2, 0.1, -0.3, 0.9, 40, 12, 0.0007, -0.0004);
        var inv = p.Inverse();
        foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, 50.0), (37.5, 210.25) })
        {
            var (X, Y, W) = p.ApplyHomogeneous(x, y);
            var (bx, by, bw) = inv.ApplyHomogeneous(X / W, Y / W);
            Assert.True(Math.Abs(bx / bw - x) < 1e-9 && Math.Abs(by / bw - y) < 1e-9);
        }
    }

    [Fact]
    public void ProjectiveMatchesTheCoreTransform()
    {
        var t = AffineTransform.Make(40, -12, 1.5, 0.7, 0.4, new Vec2(300, 200)) with { G = 0.0005, H = -0.0003 };
        var p = Projective.From(t);
        foreach (var (x, y) in new[] { (0.0, 0.0), (300.0, 0.0), (120.0, 170.0) })
        {
            var want = t.Apply(new Vec2(x, y));
            var (X, Y, W) = p.ApplyHomogeneous(x, y);
            Assert.True(Math.Abs(X / W - want.X) < 1e-9 && Math.Abs(Y / W - want.Y) < 1e-9);
        }
    }

    // MARK: Filters (on regions)

    [Fact]
    public void ClaritySigmaFollowsTheFrame()
    {
        Assert.Equal(5.6f, FilterEngine.ClaritySigma(new PixelRect(0, 0, 96, 96)), 4);      // max(4, round(1.92)) × 1.4
        Assert.Equal(7f, FilterEngine.ClaritySigma(new PixelRect(0, 0, 512, 256)), 4);      // round(5.12) = 5
        Assert.Equal(56f, FilterEngine.ClaritySigma(new PixelRect(0, 0, 8000, 8000)), 4);   // capped at 40
    }

    [Fact]
    public void NoiseIsAnchoredToTheDocument()
    {
        // The same document pixel gets the same noise whatever region it is filtered in.
        var src = new RegionImage(512, 256);
        Array.Fill(src.Pixels, new Vector4(0.5f, 0.5f, 0.5f, 1));
        var frame = new Vector2(512, 256);
        var whole = FilterEngine.Filter(src, 6, new Vector4(0.3f, 0, 0, 0), 0, Vector2.Zero, Vector2.Zero, frame);
        var part = new RegionImage(100, 50);
        for (int y = 0; y < 50; y++) src.Row(y + 100).Slice(300, 100).CopyTo(part.Row(y));
        var partial = FilterEngine.Filter(part, 6, new Vector4(0.3f, 0, 0, 0), 0, new Vector2(300, 100), Vector2.Zero, frame);
        Assert.Equal(whole.Read(320, 110), partial.Read(20, 10));
    }

    [Fact]
    public void LanczosKeepsAConstantImage()
    {
        var flat = new RegionImage(37, 23);
        Array.Fill(flat.Pixels, new Vector4(0.25f, 0.5f, 0.75f, 1));
        foreach (var (w, h) in new[] { (74, 46), (111, 69), (18, 11) })
        {
            var s = LanczosScale.Scale(flat, w, h);
            foreach (var p in s.Pixels) Assert.True(Vector4.Distance(p, flat.Pixels[0]) < 1e-5f);
        }
    }

    // MARK: Liquify

    [Fact]
    public void LiquifyComposesInsteadOfAdding()
    {
        // Two forward dabs of 4 px each move content 8 px, not more: D'(p) = D(p − o) − o.
        var engine = new LiquifyEngine(TileGrid.Empty, new PixelRect(0, 0, 256, 256));
        engine.Dab(LiquifyEngine.Mode.Forward, new Vec2(128, 128), new Vec2(4, 0), 200, 1);
        engine.Dab(LiquifyEngine.Mode.Forward, new Vec2(128, 128), new Vec2(4, 0), 200, 1);
        var d = engine.Field.Read(128, 128);
        Assert.True(MathF.Abs(d.X + 8) < 0.2f, $"displacement {d}");
    }

    [Fact]
    public void LargeRegionsUseACoarserField()
    {
        var engine = new LiquifyEngine(TileGrid.Empty, new PixelRect(0, 0, 8192, 1024));
        Assert.Equal(2f, engine.FieldScale);
        Assert.Equal(4096, engine.Field.Width);
    }

    // MARK: Thumbnails

    [Fact]
    public void ThumbnailAveragesSixteenSamples()
    {
        var px = new float[TileGeometry.FloatCount];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 128; x++)
                for (int c = 0; c < 4; c++) px[(y * 256 + x) * 4 + c] = 1;
        var tile = Tile.Make(px)!;
        // One thumbnail pixel spanning the whole tile: half white.
        Assert.Equal(0.5f, ThumbnailRenderer.Supersample(tile, new Vector2(0.5f, 0.5f), new Vector2(1f)).X, 3);
        // A pixel over the left quarter only: white.
        Assert.Equal(1f, ThumbnailRenderer.Supersample(tile, new Vector2(0.125f, 0.5f), new Vector2(0.25f)).X, 3);
    }

    [Fact]
    public void ThumbnailOfALayer()
    {
        var grid = TileGrid.Solid(new Vector4(0, 0, 1, 1), new PixelRect(0, 0, 1024, 512));
        using var thumb = new ThumbnailRenderer().Thumbnail(grid, new PixelRect(0, 0, 1000, 500), maxPixels: 64);
        Assert.NotNull(thumb);
        Assert.Equal(64, thumb.Width);
        Assert.Equal(32, thumb.Height);
        Assert.Equal(SkiaSharp.SKColorType.RgbaF16, thumb.ColorType);
        Assert.Equal(SkiaSharp.SKAlphaType.Premul, thumb.AlphaType);
        var bytes = new byte[64 * 32 * 8];
        System.Runtime.InteropServices.Marshal.Copy(thumb.GetPixels(), bytes, 0, bytes.Length);
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        int i = (16 * 64 + 30) * 4;
        Assert.Equal(new Vector4(0, 0, 1, 1), new Vector4((float)halves[i], (float)halves[i + 1], (float)halves[i + 2], (float)halves[i + 3]));
        Assert.Null(new ThumbnailRenderer().Thumbnail(grid, new PixelRect(0, 0, 0, 0), maxPixels: 64));
    }

    [Fact]
    public void PngHelperWritesStraightEightBitSrgb()
    {
        // Half-transparent linear 0.214 grey (sRGB 0.5) premultiplied: 0.107.
        var px = new Half[2 * 1 * 4];
        px[0] = (Half)0.107f; px[1] = (Half)0.107f; px[2] = (Half)0.107f; px[3] = (Half)0.5f;
        px[4] = (Half)0; px[5] = (Half)0; px[6] = (Half)1; px[7] = (Half)1;
        byte[] png = ThumbnailRenderer.PngData(px, 2, 1);
        using var decoded = SkiaSharp.SKBitmap.Decode(png, new SkiaSharp.SKImageInfo(2, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul));
        var bytes = decoded.GetPixelSpan().ToArray();
        Assert.InRange(bytes[0], 126, 129);
        Assert.Equal(128, bytes[3]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, bytes[4..8]);
    }
}
