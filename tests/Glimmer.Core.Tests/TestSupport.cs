// Helpers shared by the ported tests (the free functions at the top of
// Packages/GlimmerKit/Tests/GlimmerCoreTests/GlimmerPackageTests.swift, and the noise layers several suites make).

using System.Numerics;

namespace Glimmer.Core.Tests;

internal static class TestTiles
{
    /// <summary>A tile with a gradient so it can't collapse to solid.</summary>
    public static Tile GradientTile(float seed = 0)
    {
        const int s = TileGeometry.Size;
        var px = new Half[s * s * 4];
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                int i = (y * s + x) * 4;
                px[i] = (Half)(x / 255f * 0.5f + seed);
                px[i + 1] = (Half)(y / 255f * 0.5f);
                px[i + 2] = (Half)seed;
                px[i + 3] = (Half)(0.5f + (x + y) % 2 * 0.5f);
            }
        }
        return Tile.Make(px)!;
    }

    public static DocumentState SampleState()
    {
        var grid = TileGrid.Empty
            .SetItem(new TileCoord(0, 0), GradientTile(0.1f))
            .SetItem(new TileCoord(-1, 2), GradientTile(0.3f))
            .SetItem(new TileCoord(1, 0), Tile.Solid(new Vector4(0.2f, 0.1f, 0.05f, 0.5f)));
        var a = new Layer("Background", new RasterContent(TileGrid.Solid(new Vector4(1, 1, 1, 1), new PixelRect(0, 0, 600, 400))));
        var b = new Layer("Paint", new RasterContent(grid), isVisible: false, opacity: 0.4, blendMode: BlendMode.Multiply);
        return new DocumentState(600, 400, WorkingColorSpace.DisplayP3, BlendSpace.Gamma, [a, b], a.Id);
    }

    public static void AssertSamePixels(TileGrid a, TileGrid b)
    {
        Assert.Equal(a.Coords.ToHashSet(), b.Coords.ToHashSet());
        foreach (var (coord, tile) in a)
        {
            if (b[coord] is not { } other) continue;
            Assert.True(tile.ToHalfArray().AsSpan().SequenceEqual(other.ToHalfArray()), $"tile {coord.X},{coord.Y} differs");
        }
    }

    /// <summary>The Mac tests' random pixels: <c>seed = seed &amp;* 1_664_525 &amp;+ 1_013_904_223</c>, one value a channel.</summary>
    public static Tile NoiseTile(ref uint seed, int stride = 1)
    {
        var px = new Half[TileGeometry.FloatCount];
        for (int j = 0; j < px.Length; j += stride)
        {
            seed = unchecked(seed * 1_664_525 + 1_013_904_223);
            px[j] = (Half)((seed >> 8) / 16_777_216f);
        }
        return Tile.FromHalf(px);
    }

    /// <summary>A layer of pixels that don't compress, so its file is well over the small-file limit.</summary>
    public static Layer NoiseLayer(string name, uint seed, int tiles = 1, int columns = 4)
    {
        var b = new TileGrid.Builder();
        for (int i = 0; i < tiles; i++) b[new TileCoord(i % columns, i / columns)] = NoiseTile(ref seed);
        return new Layer(name, new RasterContent(b.ToImmutable()));
    }

    public static string TempFolder(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():D}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    public static Half[] Pixels(Tile tile) => tile.ToHalfArray();
}
