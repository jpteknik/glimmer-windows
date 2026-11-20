// New file, no Swift original: the Swift tests read a tile's pixels with Tile.pixels(); this reads one pixel of a grid,
// a missing tile being transparent and a solid one its colour, as every reader of tiles does.
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Formats.Tests;

internal static class TilePixels
{
    /// <summary>The pixel at document (<paramref name="x"/>, <paramref name="y"/>): premultiplied linear RGBA.</summary>
    public static Vector4 At(TileGrid grid, int x, int y)
    {
        const int s = TileGeometry.Size;
        var tile = grid[new TileCoord(PixelRect.FloorDiv(x, s), PixelRect.FloorDiv(y, s))];
        if (tile is null) return Vector4.Zero;
        if (tile.IsSolid) return tile.SolidColor;
        var p = tile.Pixels.Span;
        int o = ((y - PixelRect.FloorDiv(y, s) * s) * s + (x - PixelRect.FloorDiv(x, s) * s)) * 4;
        return new Vector4((float)p[o], (float)p[o + 1], (float)p[o + 2], (float)p[o + 3]);
    }

    /// <summary>The mask value (one channel) at document (<paramref name="x"/>, <paramref name="y"/>), the default where no
    /// tile is stored.</summary>
    public static float MaskAt(LayerMask mask, int x, int y)
    {
        const int s = TileGeometry.Size;
        var coord = new TileCoord(PixelRect.FloorDiv(x, s), PixelRect.FloorDiv(y, s));
        return mask.Tiles[coord] is null ? mask.DefaultValue : At(mask.Tiles, x, y).W;
    }
}
