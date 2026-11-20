// Not a port: the region reads and writes the Mac does on the GPU are TileGrid methods here, used by every package.

using System.Numerics;
using static Glimmer.Core.Tests.TestTiles;

namespace Glimmer.Core.Tests;

public class TileGridRegionTests
{
    /// <summary>A value per pixel and channel that tells every position apart (exact in Half).</summary>
    private static float Value(int x, int y, int c) => ((x * 7 + y * 13 + c * 3) & 255) / 256f + 0.001953125f;

    private static float[] Pattern(PixelRect r)
    {
        var data = new float[r.Area * 4];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                for (int c = 0; c < 4; c++) data[((long)y * r.Width + x) * 4 + c] = Value(r.X + x, r.Y + y, c);
        return data;
    }

    [Fact]
    public void WhatIsWrittenReadsBackExactlyAcrossTileEdgesAndNegativeCoordinates()
    {
        var rect = new PixelRect(-300, -20, 700, 290);   // nine tiles, three of them partly
        var grid = TileGrid.Empty.WriteRegion(rect, Pattern(rect));
        Assert.Equal(rect.TileCoords.Length, grid.Count);
        var back = grid.ReadRegion(rect);
        Assert.Equal(Pattern(rect), back);
        // A window inside it, and one that sticks out (transparent outside what was written).
        var window = new PixelRect(-10, 100, 40, 300);
        var read = grid.ReadRegion(window);
        for (int y = 0; y < window.Height; y++)
        {
            for (int x = 0; x < window.Width; x++)
            {
                int dx = window.X + x, dy = window.Y + y;
                float expected = rect.Contains(dx, dy) ? Value(dx, dy, 1) : 0;
                Assert.Equal(expected, read[(y * window.Width + x) * 4 + 1]);
            }
        }
    }

    [Fact]
    public void AWriteKeepsThePixelsAroundItAndSharesUntouchedTiles()
    {
        var far = GradientTile();
        var base0 = TileGrid.Solid(new Vector4(0.5f, 0.25f, 0, 0.5f), new PixelRect(0, 0, 512, 256)).SetItem(new TileCoord(9, 9), far);
        var spot = new PixelRect(100, 100, 10, 10);
        var red = Enumerable.Range(0, (int)spot.Area * 4).Select(i => i % 4 is 0 or 3 ? 1f : 0f).ToArray();
        var grid = base0.WriteRegion(spot, red);
        Assert.Same(far, grid[new TileCoord(9, 9)]);                      // untouched: the same tile
        Assert.Same(base0[new TileCoord(1, 0)], grid[new TileCoord(1, 0)]);
        Assert.False(grid[new TileCoord(0, 0)]!.IsSolid);                  // rebuilt: pixels now
        Assert.Equal(new Vector4(1, 0, 0, 1), grid[new TileCoord(0, 0)]!.PixelAt(105, 105));
        Assert.Equal(new Vector4(0.5f, 0.25f, 0, 0.5f), grid[new TileCoord(0, 0)]!.PixelAt(99, 105));
    }

    [Fact]
    public void ATileWrittenTransparentGoesAndOneWrittenUniformBecomesSolid()
    {
        var grid = TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 512, 256));
        var whole = new TileCoord(1, 0).Rect;
        var cleared = grid.WriteRegion(whole, new float[whole.Area * 4]);
        Assert.Null(cleared[new TileCoord(1, 0)]);
        var blue = Enumerable.Range(0, (int)whole.Area * 4).Select(i => i % 4 is 2 or 3 ? 1f : 0f).ToArray();
        var painted = grid.WriteRegion(whole, blue);
        Assert.True(painted[new TileCoord(1, 0)]!.IsSolid);
        Assert.Equal(new Vector4(0, 0, 1, 1), painted[new TileCoord(1, 0)]!.SolidColor);
        // Writing a partial region of a missing tile starts from transparent.
        var part = new PixelRect(600, 0, 2, 2);
        var made = TileGrid.Empty.WriteRegion(part, Enumerable.Repeat(1f, 16).ToArray());
        Assert.Equal(new Vector4(1, 1, 1, 1), made[new TileCoord(2, 0)]!.PixelAt(600 - 512, 0));
        Assert.Equal(Vector4.Zero, made[new TileCoord(2, 0)]!.PixelAt(0, 0));
    }

    [Fact]
    public void ABigRegionIsTheSameWhenWorkedOnInParallel()
    {
        // Big enough for the parallel path; compared with a tile-by-tile read through PixelAt.
        var rect = new PixelRect(-256, 0, 1024, 600);
        var grid = TileGrid.Empty.WriteRegion(rect, Pattern(rect));
        var read = grid.ReadRegion(rect);
        foreach (var (x, y) in new[] { (-256, 0), (-1, 255), (0, 256), (767, 599), (300, 400) })
        {
            var coord = new TileCoord(PixelRect.FloorDiv(x, 256), PixelRect.FloorDiv(y, 256));
            var expected = grid[coord]!.PixelAt(x - coord.X * 256, y - coord.Y * 256);
            int i = ((y - rect.Y) * rect.Width + (x - rect.X)) * 4;
            Assert.Equal(expected, new Vector4(read[i], read[i + 1], read[i + 2], read[i + 3]));
        }
    }

    [Fact]
    public void OneChannelReadsAndWritesAsMasksAreStored()
    {
        var rect = new PixelRect(250, 250, 20, 20);
        var values = Enumerable.Range(0, 400).Select(i => (i % 20) / 20f).ToArray();
        // A mask whose default is "reveal" (1): missing tiles read 1 around the written area.
        var mask = TileGrid.Empty.WriteChannel(rect, values, defaultValue: 1);
        var read = mask.ReadChannel(new PixelRect(240, 250, 40, 1), defaultValue: 1);
        Assert.Equal(1, read[0]);
        Assert.Equal(0, read[10]);                 // x = 250: the first written value
        Assert.Equal(19 / 20f, read[29], 3);
        Assert.Equal(1, read[39]);
        // Every channel holds the value.
        var t = mask[new TileCoord(0, 0)]!;
        var p = t.PixelAt(255, 255);
        Assert.True(p.X == p.Y && p.Y == p.Z && p.Z == p.W);
        // Writing the default back everywhere leaves no tiles; writing zero over a tile with default 1 keeps a solid zero.
        var whole = new TileCoord(0, 0).Rect;
        Assert.Null(mask.WriteChannel(whole, Enumerable.Repeat(1f, (int)whole.Area).ToArray(), defaultValue: 1)[new TileCoord(0, 0)]);
        var hidden = mask.WriteChannel(whole, new float[whole.Area], defaultValue: 1)[new TileCoord(0, 0)]!;
        Assert.True(hidden.IsSolid && hidden.SolidColor == Vector4.Zero);
    }

    [Fact]
    public void TheTransparencyRuleIgnoresTheSignBitOfAlpha()
    {
        var px = new Half[TileGeometry.FloatCount];
        for (int i = 3; i < px.Length; i += 4) px[i] = Half.NegativeZero;
        px[0] = (Half)1;   // colour without alpha: still transparent by the rule
        Assert.Null(Tile.Make(px));
        px[7] = (Half)0.001f;
        Assert.False(Tile.Make(px)!.IsSolid);
    }

    [Fact]
    public void CopiesExpandSolidTiles()
    {
        var tile = Tile.Solid(new Vector4(0.2f, 0.4f, 0.6f, 1));
        var floats = new float[TileGeometry.FloatCount];
        tile.CopyTo(floats);
        Assert.Equal(0.2f, floats[^4]);
        Assert.Equal(1f, floats[^1]);
        var halves = tile.ToHalfArray();
        Assert.Equal((Half)0.4f, halves[TileGeometry.FloatCount / 2 + 1]);
        Assert.True(tile.Pixels.IsEmpty && tile.IsSolid);
        Assert.Throws<ArgumentException>(() => Tile.FromHalf(new Half[10]));
    }

    [Fact]
    public void GridsWithTheSameTilesAreEqual()
    {
        var t = GradientTile();
        var a = TileGrid.Empty.SetItem(new TileCoord(0, 0), t).SetItem(new TileCoord(1, 0), Tile.Solid(Vector4.One));
        var b = TileGrid.Empty.SetItem(new TileCoord(1, 0), Tile.Solid(Vector4.One)).SetItem(new TileCoord(0, 0), t);
        Assert.Equal(a, b);
        Assert.Equal(a.ContentSignature, b.ContentSignature);
        Assert.NotEqual(a, b.SetItem(new TileCoord(0, 0), GradientTile()));
        Assert.Equal(new PixelRect(0, 0, 512, 256), a.TileBounds);
        Assert.Null(TileGrid.Empty.TileBounds);
    }

    [Fact]
    public void RectGeometry()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(5, -5, 10, 10);
        Assert.Equal(new PixelRect(5, 0, 5, 5), a.Intersection(b));
        Assert.Equal(new PixelRect(0, -5, 15, 15), a.Union(b));
        Assert.Equal(a, a.Union(new PixelRect(100, 100, 0, 5)));
        Assert.True(a.Contains(9, 9) && !a.Contains(10, 9) && a.Contains(new PixelRect(2, 2, 3, 3)));
        Assert.Equal(-2, PixelRect.FloorDiv(-257, 256));
        Assert.Equal(-1, PixelRect.FloorDiv(-256, 256));
        Assert.True(new TileCoord(5, 0) < new TileCoord(0, 1));
    }
}
