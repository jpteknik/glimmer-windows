// Ported from Packages/GlimmerKit/Sources/GlimmerCore/TileBounds.swift

namespace Glimmer.Core;

/// <summary>
/// Where each tile's visible pixels are, remembered by the tile's identity.
/// </summary>
/// <remarks>
/// The tight bounds of a layer's content need every pixel's alpha, and reading a 24-megapixel layer through takes
/// seconds. But a step changes few tiles, and a tile that was not changed is the same tile (same identity) as before:
/// only the tiles a step touched are read again.
/// </remarks>
public sealed class TileBoundsCache
{
    /// <summary>A tile's content inside the tile, as offsets from its origin; null when nothing in it is visible.</summary>
    internal readonly record struct Local(int MinX, int MinY, int MaxX, int MaxY);

    private readonly Dictionary<long, Local?> local = [];
    private readonly Lock gate = new();
    private readonly int limit;
    private int tilesRead;

    public TileBoundsCache(int limit = 200_000) => this.limit = limit;

    /// <summary>How many tiles were read through since the cache was made (for tests and measuring).</summary>
    public int TilesRead
    {
        get { lock (gate) return tilesRead; }
    }

    /// <summary>Alpha above this counts as visible: 0.002 as a Half, as the Mac compares Float16 values.</summary>
    internal static readonly float VisibleAlpha = (float)(Half)0.002f;

    internal Local? Bounds(Tile tile)
    {
        lock (gate)
        {
            if (local.TryGetValue(tile.Id, out var hit)) return hit;
        }
        const int s = TileGeometry.Size;
        var p = tile.PixelSpan;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < s; y++)
        {
            int row = y * s * 4 + 3;
            for (int x = 0; x < s; x++)
            {
                if (!((float)p[row + x * 4] > VisibleAlpha)) continue;
                if (x < minX) minX = x;
                if (x >= maxX) maxX = x + 1;
                if (y < minY) minY = y;
                maxY = y + 1;
            }
        }
        Local? found = minX < maxX ? new Local(minX, minY, maxX, maxY) : null;
        lock (gate)
        {
            if (local.Count > limit) local.Clear();
            local[tile.Id] = found;
            tilesRead++;
        }
        return found;
    }
}

public sealed partial class TileGrid
{
    /// <summary>
    /// The tight bounds of the pixels that are not transparent (alpha above 0.002), in the grid's own coordinates.
    /// Each pixel tile is read through once per identity (<paramref name="cache"/>); a solid tile is all or nothing.
    /// </summary>
    public PixelRect? ContentBounds(TileBoundsCache cache)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var (coord, tile) in tiles)
        {
            var origin = coord.Rect;
            if (tile.IsSolid)
            {
                if (!(tile.SolidColor.W > 0.002f)) continue;
                minX = Math.Min(minX, origin.X); minY = Math.Min(minY, origin.Y);
                maxX = Math.Max(maxX, origin.MaxX); maxY = Math.Max(maxY, origin.MaxY);
            }
            else
            {
                if (cache.Bounds(tile) is not { } b) continue;
                minX = Math.Min(minX, origin.X + b.MinX); minY = Math.Min(minY, origin.Y + b.MinY);
                maxX = Math.Max(maxX, origin.X + b.MaxX); maxY = Math.Max(maxY, origin.Y + b.MaxY);
            }
        }
        if (minX >= maxX) return null;
        return new PixelRect(minX, minY, maxX - minX, maxY - minY);
    }
}
