// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift (CanvasRendererTests), extended:
// the checkerboard and document edge, zoomed sampling of the composited tiles, the Channels panel, and the Windows-only
// step that encodes the drawable to 8-bit BGRA.
using System.Numerics;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render.Tests;

public class CanvasPresentationTests
{
    private static readonly Vector4 Pasteboard = new(0.1f, 0.2f, 0.3f, 1);

    private static CompositeTile TileOf(Func<int, int, Vector4> color)
    {
        var px = new Half[TileGeometry.FloatCount];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                var c = color(x, y);
                int i = (y * 256 + x) * 4;
                px[i] = (Half)c.X; px[i + 1] = (Half)c.Y; px[i + 2] = (Half)c.Z; px[i + 3] = (Half)c.W;
            }
        return new CompositeTile(0, px);
    }

    private static CanvasFrame Frame(double zoom, double docSize, double rotation = 0, double checker = 4) =>
        new(new ViewTransform(scale: zoom, rotation: rotation), new Vec2(256, 256), 1, new Vec2(docSize, docSize), Pasteboard, checker);

    [Fact]
    public void DrawsTheCheckerboardAndAnAntiAliasedDocumentEdge()
    {
        var renderer = new CanvasRenderer();
        // Without a layer: the checkerboard, light and dark squares of 4 px.
        var px = renderer.RenderLinear(Frame(1, 32), 64, 64);
        Assert.Equal(1f, px[1 * 64 + 1].X, 5);
        Assert.Equal((float)LinearColor.Linearize(0.8), px[1 * 64 + 5].X, 5);
        // At 2 × zoom the document ends between drawable pixels 63 and 64.
        var zoomed = renderer.RenderLinear(Frame(2, 32), 128, 32);
        Assert.True(zoomed[10 * 128 + 63].X > 0.5f);
        Assert.True(MathF.Abs(zoomed[10 * 128 + 64].X - 0.1f) < 0.002f);
    }

    [Fact]
    public void ZoomedOutViewsSampleTheMipsAndZoomedInViewsShowSharpPixels()
    {
        // A checkerboard tile seen at 25 %: every drawable pixel is the average grey.
        var checker = TileOf((x, y) => (x + y) % 2 == 0 ? Vector4.One : new Vector4(0, 0, 0, 1));
        var renderer = new CanvasRenderer();
        var quarter = renderer.RenderLinear(Frame(0.25, 256), 64, 64, [new TileCoord(0, 0)], _ => checker);
        for (int x = 3; x < 60; x += 7) Assert.Equal(0.5f, quarter[17 * 64 + x].X, 3);
        // At 400 % each document pixel is a sharp 4 × 4 block (nearest).
        var zoomed = renderer.RenderLinear(Frame(4, 256), 16, 16, [new TileCoord(0, 0)], _ => checker);
        Assert.Equal(1f, zoomed[3 * 16 + 3].X);
        Assert.Equal(0f, zoomed[3 * 16 + 4].X);
    }

    [Fact]
    public void AZoomedOutTileAtItsOwnLevelIsReadPixelForPixel()
    {
        var compositor = new DocumentCompositor();
        var tile = TestSupport.TextureTile(new Vector4(0.2f, 0.4f, 0.6f, 1));
        var state = new DocumentState(256, 256, layers: [new Layer("t", new RasterContent(TestSupport.Grid((new TileCoord(0, 0), tile))))]);
        compositor.Update(state, null, [new TileCoord(0, 0)], detailLevel: DocumentCompositor.DetailLevel(0.25));
        var t = compositor.Texture(new TileCoord(0, 0))!;
        Assert.Equal(64, t.Width);
        var px = new CanvasRenderer().RenderLinear(Frame(0.25, 256), 64, 64, [new TileCoord(0, 0)], compositor.Texture);
        Assert.True((px[30 * 64 + 30] - new Vector4(0.2f, 0.4f, 0.6f, 1)).Length() < 0.002f);
    }

    [Fact]
    public void ARotatedViewShowsTheTileWhereTheDocumentIs()
    {
        var blue = TileOf((_, _) => new Vector4(0, 0, 1, 1));
        var renderer = new CanvasRenderer();
        // Rotated 45° about the origin: the document's top-left corner is at drawable (0, 0), its diagonal points down.
        var frame = new CanvasFrame(new ViewTransform(rotation: Math.PI / 4), new Vec2(256, 256), 1, new Vec2(256, 256), Pasteboard, 4);
        var px = renderer.RenderLinear(frame, 256, 256, [new TileCoord(0, 0)], _ => blue);
        Assert.True(px[150 * 256 + 0].Z > 0.99f && px[150 * 256 + 0].X < 0.01f, "straight below the corner is inside the document");
        Assert.True(MathF.Abs(px[10 * 256 + 150].X - 0.1f) < 0.002f, "up and to the right is outside: pasteboard");
    }

    [Fact]
    public void EncodeAgreesWithTheLinearDrawable()
    {
        // Opaque and transparent tile pixels, the checkerboard, the pasteboard, at three views.
        var tile = TileOf((x, y) => x < 128 ? new Vector4(x / 255f, y / 255f, 0.5f, 1) : new Vector4(0.3f, 0.2f, 0.1f, 1) * (y / 255f));
        var renderer = new CanvasRenderer();
        foreach (var (zoom, rotation) in new[] { (1.0, 0.0), (0.37, 0.0), (1.7, 0.6) })
        {
            var frame = new CanvasFrame(new ViewTransform(scale: zoom, rotation: rotation, translation: new Vec2(20, 15)), new Vec2(200, 160), 1,
                                        new Vec2(256, 256), Pasteboard, 6);
            var linear = renderer.RenderLinear(frame, 200, 160, [new TileCoord(0, 0)], _ => tile);
            var bgra = new uint[200 * 160];
            renderer.Encode(frame, new CanvasTarget(bgra, 200, 160), [new TileCoord(0, 0)], _ => tile);
            int same = 0;
            for (int y = 0; y < 160; y++)
                for (int x = 0; x < 200; x++)
                {
                    uint want = DisplayEncoding.EncodeBgra(linear[y * 200 + x], x, y), got = bgra[y * 200 + x];
                    for (int shift = 0; shift < 32; shift += 8)
                        Assert.True(Math.Abs((int)((want >> shift) & 0xFF) - (int)((got >> shift) & 0xFF)) <= 1, $"{zoom}/{rotation} at {x},{y}: {want:X8} vs {got:X8}");
                    if (want == got) same++;
                }
            Assert.True(same > 200 * 160 * 0.98, $"{zoom}/{rotation}: {same} identical");
        }
    }

    [Fact]
    public void EncodesIntoMemoryWithAStride()
    {
        var tile = TileOf((x, y) => new Vector4(x / 255f, 0.5f, y / 255f, 1));
        var renderer = new CanvasRenderer();
        var frame = Frame(1, 256);
        var array = new uint[100 * 80];
        renderer.Encode(frame, new CanvasTarget(array, 100, 80), [new TileCoord(0, 0)], _ => tile);
        const int stride = 100 * 4 + 64;
        nint memory = Marshal.AllocHGlobal(stride * 80);
        try
        {
            renderer.Encode(frame, new CanvasTarget(memory, 100, 80, stride), [new TileCoord(0, 0)], _ => tile);
            for (int y = 0; y < 80; y++)
                for (int x = 0; x < 100; x++)
                    Assert.Equal(array[y * 100 + x], (uint)Marshal.ReadInt32(memory, y * stride + x * 4));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void EncodesToBgraForTheWindowsCanvas()
    {
        uint blue = DisplayEncoding.EncodeBgra(new Vector4(0, 0, 1, 1), 3, 7);
        Assert.Equal(0xFF0000FFu, blue);  // B in the low byte, opaque
        // The pasteboard: sRGB 0.34919 × 255 = 89.04, dithered between 89 and 90, 89.04 on average.
        double sum = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                uint v = DisplayEncoding.EncodeBgra(Pasteboard, x, y);
                byte r = (byte)(v >> 16);
                Assert.True(r is 89 or 90);
                sum += r;
            }
        Assert.Equal(SrgbTransfer.Encode(0.1f) * 255, sum / 64, 1);
        // Without dither: rounded.
        Assert.Equal(89u, (DisplayEncoding.EncodeBgra(Pasteboard, 0, 0, dither: false) >> 16) & 0xFF);
        // Half transparent white over nothing: premultiplied in encoded space.
        uint half = DisplayEncoding.EncodeBgra(new Vector4(0.5f, 0.5f, 0.5f, 0.5f), 0, 0, dither: false);
        Assert.Equal(128u, half >> 24);
        Assert.Equal(128u, half & 0xFF);
    }

    [Fact]
    public void BayerThresholdsArePermutation()
    {
        var seen = new HashSet<float>();
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++) seen.Add(DisplayEncoding.Threshold(x, y));
        Assert.Equal(64, seen.Count);
        Assert.Equal(0.5f / 64, seen.Min());
        Assert.Equal(63.5f / 64, seen.Max());
    }

    [Fact]
    public void ChannelsPanelViews()
    {
        var c = new Vector4(0.2f, 0.4f, 0.6f, 0.5f);  // premultiplied: straight (0.4, 0.8, 1.2)
        Assert.Equal(c, CompositeShaders.PresentChannels(c, 15));
        Assert.Equal(new Vector4(0.8f, 0.8f, 0.8f, 1), CompositeShaders.PresentChannels(c, 2));  // green as grey
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1), CompositeShaders.PresentChannels(c, 8));  // alpha as grey
        Assert.Equal(new Vector4(0, 0, 0, 1), CompositeShaders.PresentChannels(c, 0));
        Assert.Equal(new Vector4(0.4f, 0.8f, 0, 1), CompositeShaders.PresentChannels(c, 3));    // red + green, no alpha: opaque
        Assert.Equal(new Vector4(0.2f, 0.4f, 0, 0.5f), CompositeShaders.PresentChannels(c, 11));
        // Through the canvas: a red-channel view of an opaque tile is grey.
        var tile = TileOf((_, _) => new Vector4(0.25f, 0.5f, 0.75f, 1));
        var px = new CanvasRenderer().RenderLinear(Frame(1, 256), 8, 8, [new TileCoord(0, 0)], _ => tile, channelMask: 1);
        Assert.Equal(new Vector4(0.25f, 0.25f, 0.25f, 1), px[3 * 8 + 3]);
    }
}

public class SrgbTransferTests
{
    [Fact]
    public void FastTablesMatchTheFormulaOverZeroToOne()
    {
        double worstEncode = 0, worstDecode = 0;
        for (int i = 0; i <= 2_000_000; i++)
        {
            float x = i / 2_000_000f;
            double e = x <= 0.0031308 ? x * 12.92 : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
            double d = x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            worstEncode = Math.Max(worstEncode, Math.Abs(SrgbTransfer.EncodeFast(x) - e));
            worstDecode = Math.Max(worstDecode, Math.Abs(SrgbTransfer.DecodeFast(x) - d));
        }
        Assert.True(worstEncode < 1.5e-6, $"encode error {worstEncode}");
        Assert.True(worstDecode < 1e-5, $"decode error {worstDecode}");
    }

    [Fact]
    public void ExtendedAndNegativeValuesMirror()
    {
        foreach (float x in new[] { -2f, -0.5f, -0.001f, 1.5f, 3f, 0f, 1f })
        {
            Assert.Equal(SrgbTransfer.Encode(x), SrgbTransfer.EncodeFast(x), 6);
            Assert.Equal(SrgbTransfer.Decode(x), SrgbTransfer.DecodeFast(x), 6);
            Assert.Equal(-SrgbTransfer.Encode(-x), SrgbTransfer.Encode(x), 6);
        }
        Assert.Equal(0.7353569f, SrgbTransfer.Encode(0.5f), 5);
        Assert.Equal(0.2140411f, SrgbTransfer.Decode(0.5f), 5);
    }
}
