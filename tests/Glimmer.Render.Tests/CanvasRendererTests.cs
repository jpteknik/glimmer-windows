// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift
// (ExportTests there exercise ImageExporter, which belongs to Glimmer.Formats.)
using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using Glimmer.Core;
using Xunit.Abstractions;
using static Glimmer.Render.Tests.TestSupport;

namespace Glimmer.Render.Tests;

public class BlendModeTests
{
    private static readonly (Vector3 cb, Vector3 cs)[] Cases =
    [
        (new(0.2f, 0.5f, 0.8f), new(0.7f, 0.3f, 0.1f)),
        (new(0.9f, 0.1f, 0.4f), new(0.25f, 0.6f, 0.95f)),
    ];

    public static TheoryData<BlendMode> PixelModes => [.. BlendMode.PixelModes];

    [Theory]
    [MemberData(nameof(PixelModes))]
    public void MatchesReference(BlendMode mode)
    {
        foreach (var (cb, cs) in Cases)
        {
            float ab = 0.8f, a = 0.6f, opacity = 0.75f;
            var source = Grid((new TileCoord(0, 0), TextureTile(new Vector4(cs * a, a))));
            var state = new DocumentState(256, 256, layers:
            [
                SolidLayer(new Vector4(cb * ab, ab)),
                new Layer("src", new RasterContent(source), opacity: opacity, blendMode: mode),
            ]);
            var got = RenderPixel(state, 10, 10);
            var want = Reference.Composite(cb, ab, cs, a, opacity, mode);
            Assert.True((got - want).Length() < 0.004f, $"{mode}: got {got}, want {want}");
        }
    }

    [Fact]
    public void KnownValues()
    {
        // Opaque 0.5 grey multiplied by itself is 0.25 in linear space.
        var state = new DocumentState(256, 256, layers:
            [SolidLayer(new Vector4(0.5f, 0.5f, 0.5f, 1)), SolidLayer(new Vector4(0.5f, 0.5f, 0.5f, 1), mode: BlendMode.Multiply)]);
        var got = RenderPixel(state, 0, 0);
        Assert.True(MathF.Abs(got.X - 0.25f) < 0.001f && MathF.Abs(got.W - 1) < 0.001f);

        // A fully transparent source leaves the backdrop untouched in every mode.
        foreach (var mode in BlendMode.PixelModes)
        {
            var s = new DocumentState(256, 256, layers:
                [SolidLayer(new Vector4(0.3f, 0.2f, 0.1f, 1)), SolidLayer(new Vector4(0.9f, 0.9f, 0.9f, 1), opacity: 0, mode: mode)]);
            Assert.True((RenderPixel(s, 5, 5) - new Vector4(0.3f, 0.2f, 0.1f, 1)).Length() < 0.001f, mode.ToString());
        }
    }

    [Fact]
    public void GammaBlendSpaceDiffersFromLinear()
    {
        var state = new DocumentState(256, 256, blendSpace: BlendSpace.Gamma, layers:
            [SolidLayer(new Vector4(0.5f, 0.5f, 0.5f, 1)), SolidLayer(new Vector4(0.5f, 0.5f, 0.5f, 1), mode: BlendMode.Multiply)]);
        // In gamma space: encode(0.5) = 0.7354; squared = 0.5408; decode → 0.2541
        var got = RenderPixel(state, 0, 0);
        Assert.True(MathF.Abs(got.X - 0.2541f) < 0.002f);
    }
}

public class CompositorTests
{
    [Fact]
    public void CachesUntilInputsChange()
    {
        var compositor = new DocumentCompositor();
        var state = new DocumentState(512, 256, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 512)]);
        var coords = state.Bounds.TileCoords;

        bool Run() => compositor.Update(state, null, coords, budget: 1);
        Assert.True(Run());   // budget 1 of 2 stale tiles
        Assert.False(Run());  // second tile
        var first = compositor.Texture(new TileCoord(0, 0));
        Assert.NotNull(first);
        Assert.False(Run());  // nothing stale
        Assert.Same(first, compositor.Texture(new TileCoord(0, 0)));

        state = state with { Layers = [state.Layers[0] with { Opacity = 0.5 }] };
        Assert.True(Run());
        state = state with { Layers = [state.Layers[0] with { IsVisible = false }] };
        Run(); Run();
        Assert.Null(compositor.Texture(new TileCoord(0, 0)));  // nothing visible → no texture
    }

    /// <summary>Regression: tiles rendered in a budget-limited pass must still get mipmaps, or zoomed-out views show stale content.</summary>
    [Fact]
    public void BudgetedPassStillGeneratesMipmaps()
    {
        var compositor = new DocumentCompositor();
        var coords = new PixelRect(0, 0, 512, 256).TileCoords;
        void Pass(DocumentState state) => compositor.Update(state, null, coords, budget: 1);
        Pass(new DocumentState(512, 256, layers: [SolidLayer(new Vector4(1, 1, 1, 1), size: 512)]));
        Pass(new DocumentState(512, 256, layers: [SolidLayer(new Vector4(1, 1, 1, 1), size: 512)]));
        Pass(new DocumentState(512, 256, layers: [SolidLayer(new Vector4(1, 0, 0, 1), size: 512)]));  // re-renders tile 0 only

        var texture = compositor.Texture(coords[0]);
        Assert.NotNull(texture);
        var px = texture.Read(0, 0, mip: 4);
        Assert.True(px.Y < 0.01f, $"mip level 4 is stale: {px}");
    }

    [Fact]
    public void OverridesOffsetALayer()
    {
        var layer = new Layer("g", new RasterContent(Grid((new TileCoord(0, 0), TextureTile(new Vector4(0, 1, 0, 1))))));
        var state = new DocumentState(512, 512, layers: [layer]);
        var moved = new LayerOverrides(offsets: new Dictionary<Guid, (int X, int Y)> { [layer.Id] = (300, 10) }.ToImmutableDictionary());
        Assert.True(RenderPixel(state, 310, 20, moved).Y > 0.99f);
        Assert.Equal(0, RenderPixel(state, 299, 20, moved).W);
        Assert.Equal(0, RenderPixel(state, 310, 20).W);
    }

    [Fact]
    public void DetailLevelFollowsZoom()
    {
        Assert.Equal(0, DocumentCompositor.DetailLevel(1));
        Assert.Equal(0, DocumentCompositor.DetailLevel(0.5));   // below one half only, as on the Mac
        Assert.Equal(1, DocumentCompositor.DetailLevel(0.49));
        Assert.Equal(2, DocumentCompositor.DetailLevel(0.25));
        Assert.Equal(5, DocumentCompositor.DetailLevel(0.001));
        Assert.Equal(0, DocumentCompositor.DetailLevel(0));
    }

    [Fact]
    public void ZoomedOutUpdateKeepsOnlyTheNeededLevel()
    {
        var compositor = new DocumentCompositor();
        var state = new DocumentState(256, 256, layers: [new Layer("t", new RasterContent(Grid((new TileCoord(0, 0), TextureTile(new Vector4(0.25f, 0.5f, 0.75f, 1))))))]);
        compositor.Update(state, null, [new TileCoord(0, 0)], detailLevel: 2);
        var t = compositor.Texture(new TileCoord(0, 0));
        Assert.NotNull(t);
        Assert.Equal(2, t.Level);
        Assert.Equal(64, t.Width);
        Assert.True((t.Read(10, 10) - new Vector4(0.25f, 0.5f, 0.75f, 1)).Length() < 0.002f);
        // Zooming back in renders the full tile again.
        compositor.Update(state, null, [new TileCoord(0, 0)], detailLevel: 0);
        Assert.Equal(0, compositor.Texture(new TileCoord(0, 0))!.Level);
    }
}

public class TileOperationsTests
{
    public static TheoryData<int, int> Offsets => new() { { 300, -20 }, { 256, 512 }, { -1, 1 } };

    [Theory]
    [MemberData(nameof(Offsets))]
    public void TranslateMatchesOffsetRendering(int ox, int oy)
    {
        var grid = Grid((new TileCoord(0, 0), TextureTile(new Vector4(0.2f, 0.4f, 0.6f, 1))),
                        (new TileCoord(1, 0), Tile.Solid(new Vector4(0, 0, 0.5f, 0.5f))));
        var moved = TileOperations.Translate(grid, (ox, oy));

        var layer = new Layer("a", new RasterContent(grid));
        var bounds = new PixelRect(-512, -512, 2048, 2048);
        foreach (var (x, y) in new[] { (5, 5), (300, 100), (600, 0), (ox + 1, oy + 1), (ox + 400, oy + 30) })
        {
            var viaOverride = RenderPixel(new DocumentState(4096, 4096, layers: [layer]), x - bounds.X, y - bounds.Y,
                new LayerOverrides(offsets: new Dictionary<Guid, (int X, int Y)> { [layer.Id] = (ox - bounds.X, oy - bounds.Y) }.ToImmutableDictionary()));
            var translated = TileOperations.Translate(moved, (-bounds.X, -bounds.Y));
            var viaTranslate = RenderPixel(new DocumentState(4096, 4096, layers: [new Layer("b", new RasterContent(translated))]),
                                           x - bounds.X, y - bounds.Y);
            Assert.True(viaOverride == viaTranslate, $"at {x},{y}: {viaOverride} vs {viaTranslate}");
        }
    }

    [Fact]
    public void WholeTileMoveKeepsSolids()
    {
        var grid = TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 512, 256));
        var moved = TileOperations.Translate(grid, (256, 0));
        Assert.Equal(new HashSet<TileCoord> { new(1, 0), new(2, 0) }, moved.Coords.ToHashSet());
        var partly = TileOperations.Translate(grid, (10, 0));
        Assert.True(partly[new TileCoord(1, 0)]!.IsSolid, "fully covered tile should stay solid");
        Assert.False(partly[new TileCoord(0, 0)]!.IsSolid, "partial tile should be a pixel tile");
    }
}

public class CanvasRendererTests
{
    [Fact]
    public void DrawsCheckerTilesAndPasteboard()
    {
        var renderer = new CanvasRenderer();
        var compositor = new DocumentCompositor();
        const int width = 64;
        // 32×32 document; left half covered by an opaque blue layer.
        var state = new DocumentState(32, 32, layers: [new Layer("b", new RasterContent(Grid((new TileCoord(0, 0), Tile.Solid(new Vector4(0, 0, 1, 1))))))]);
        var frame = new CanvasFrame(new ViewTransform(), new Vec2(64, 64), 1, state.Size, new Vector4(0.1f, 0.2f, 0.3f, 1), CheckerSize: 4);
        compositor.Update(state, null, [new TileCoord(0, 0)]);
        var px = renderer.RenderLinear(frame, width, width, [new TileCoord(0, 0)], compositor.Texture);
        Vector4 At(int x, int y) => px[y * width + x];
        Assert.True(MathF.Abs(At(10, 10).Z - 1) < 0.002f && At(10, 10).X < 0.002f);  // layer
        Assert.True(MathF.Abs(At(50, 50).X - 0.1f) < 0.002f);                         // pasteboard (outside doc)

        // The same frame as 8-bit premultiplied BGRA.
        var bgra = new uint[width * width];
        renderer.Encode(frame, new CanvasTarget(bgra, width, width), [new TileCoord(0, 0)], compositor.Texture);
        Assert.Equal(0xFF0000FFu, bgra[10 * width + 10]);                                  // blue, opaque
        uint pasteboard = bgra[50 * width + 50];
        Assert.InRange((int)((pasteboard >> 16) & 0xFF), 88, 90);                          // sRGB of linear 0.1 ≈ 89
        Assert.Equal(0xFFu, pasteboard >> 24);
    }

    [Fact]
    public void Linearization()
    {
        Assert.Equal(0, LinearColor.Linearize(0));
        Assert.True(Math.Abs(LinearColor.Linearize(1) - 1) < 1e-12);
        Assert.True(Math.Abs(LinearColor.Linearize(0.5) - 0.214) < 0.001);
    }
}

[Collection("Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    /// <summary>8k × 8k, 20 layers: full composite must stay well under budget.</summary>
    [PerformanceFact]
    public void CompositesLargeDocument()
    {
        const int size = 8192;
        var bounds = new PixelRect(0, 0, size, size);
        var tiles = Enumerable.Range(0, 4).Select(i => TextureTile(new Vector4(i * 0.1f, 0.2f, 0.3f, 0.5f))).ToArray();
        var layers = new List<Layer>();
        for (int i = 0; i < 20; i++)
        {
            var b = new TileGrid.Builder();
            var coordsI = bounds.TileCoords;
            for (int n = 0; n < coordsI.Length; n++) b[coordsI[n]] = tiles[(n + i) % 4];
            layers.Add(new Layer($"L{i}", new RasterContent(b.ToImmutable()), blendMode: BlendMode.PixelModes[i % 18]));
        }
        var state = new DocumentState(size, size, layers: layers);
        var compositor = new DocumentCompositor();
        var coords = bounds.TileCoords;

        var sw = Stopwatch.StartNew();
        compositor.Update(state, null, coords);
        var elapsed = sw.Elapsed;
        output.WriteLine($"Composited 8k × 8k × 20 layers ({coords.Length} tiles) in {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed.TotalMilliseconds:F0} ms");

        // A second pass with no changes only computes signatures.
        sw.Restart();
        compositor.Update(state, null, coords);
        var idle = sw.Elapsed;
        output.WriteLine($"Unchanged re-check took {idle.TotalMilliseconds:F1} ms");
        Assert.True(idle < TimeSpan.FromMilliseconds(100), $"took {idle.TotalMilliseconds:F1} ms");
    }
}

public class SelectionOutlineTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.29)]
    public void AntsAppearOnTheSelectionEdge(double zoom)
    {
        var renderer = new CanvasRenderer();
        const int width = 128;
        double k = 1 / zoom;
        var selection = Selection.Rectangle(new CGRectLike(20 * k, 20 * k, 80 * k, 80 * k));
        var frame = new CanvasFrame(new ViewTransform(scale: zoom), new Vec2(128, 128), 1, new Vec2(128 * k, 128 * k),
                                    new Vector4(0.1f, 0.2f, 0.3f, 1), CheckerSize: 4);
        var px = renderer.RenderLinear(frame, width, width, new PixelRect(0, 0, (int)(128 * k), (int)(128 * k)).TileCoords,
                                       _ => null, selection, time: 312_345.678);
        // Along the left edge (x = 20) some pixels are pure black or white ants.
        int antPixels = 0;
        for (int y = 22; y < 78; y++)
        {
            for (int x = 18; x <= 21; x++)
            {
                var c = px[y * width + x];
                if ((c.X < 0.05f && c.Y < 0.05f) || (c.X > 0.95f && c.Y > 0.95f && c.Z > 0.95f && (x + y) % 2 == 0)) antPixels++;
            }
        }
        Assert.True(antPixels > 20, $"found {antPixels} ant pixels");
    }
}

public class SelectionOutlineAppFrameTests
{
    [Fact]
    public void AntsVisibleInAZoomedOutRetinaFrame()
    {
        var renderer = new CanvasRenderer();
        var compositor = new DocumentCompositor();
        var state = new DocumentState(3840, 2160, layers: [SolidLayer(new Vector4(0.5f, 0.4f, 0.3f, 1), size: 3840)]);
        var viewSize = new Vec2(500, 300);
        double bs = 2;
        var transform = ViewTransform.Fitting(state.Size, viewSize);
        int w = (int)(viewSize.X * bs), h = (int)(viewSize.Y * bs);
        var rect = new CGRectLike(1500.3, 1000.7, 2200.2, 1400.9);
        var selection = Selection.Rectangle(rect);
        var coords = state.Bounds.TileCoords;
        var frame = new CanvasFrame(transform, viewSize, bs, state.Size, new Vector4(0.1f, 0.2f, 0.3f, 1));
        compositor.Update(state, null, coords);
        var bgra = new uint[w * h];
        renderer.Encode(frame, new CanvasTarget(bgra, w, h), coords, compositor.Texture, selection, time: 1234.5);
        // Left edge of the selection in device pixels.
        var edge = transform.ViewPoint(new Vec2(rect.MinX, (rect.MinY + rect.MaxY) / 2)) * bs;
        int extremes = 0;
        for (int y = (int)edge.Y - 40; y < (int)edge.Y + 40; y++)
        {
            for (int x = (int)edge.X - 3; x <= (int)edge.X + 3; x++)
            {
                uint r = (bgra[y * w + x] >> 16) & 0xFF;
                if (r < 26 || r > 229) extremes++;
            }
        }
        Assert.True(extremes > 20, $"found {extremes} ant pixels near x={edge.X}");
    }
}
