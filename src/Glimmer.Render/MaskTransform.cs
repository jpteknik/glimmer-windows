// Ported from Packages/GlimmerKit/Sources/GlimmerRender/MaskTransform.swift
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Moves a layer mask through the transform its layer gets (Free Transform), so the mask stays in line with the pixels.</summary>
public static class MaskTransform
{
    /// <summary>The mask's tiles after <paramref name="transform"/> (document → document; may be a perspective).</summary>
    /// <remarks>
    /// <para>Stored tiles are resampled. Everywhere else the mask keeps its default value: for a reveal-all mask (default
    /// white) the hidden part is what moves, so nothing outside the moved area turns black.</para>
    /// <para><paramref name="exact"/> false is for live previews: it skips the two passes a white-default mask needs and
    /// instead fills the default over <paramref name="cover"/> (where the layer's content is) before resampling. That is
    /// right wherever the layer has content; only empty surroundings differ.</para>
    /// </remarks>
    public static TileGrid Apply(LayerMask mask, AffineTransform transform, PixelRect limit, bool exact = true, PixelRect? cover = null)
    {
        if (mask.Tiles.TileBounds is not { } stored) return mask.Tiles;  // one value everywhere: nothing to move
        var renderer = new SmartObjectRenderer();
        TileGrid Resample(TileGrid grid)
        {
            if (grid.TileBounds is not { } b) return TileGrid.Empty;
            var source = TileOperations.Translate(grid, (-b.X, -b.Y));
            var placed = transform.Concatenating(new AffineTransform(tx: b.X, ty: b.Y));
            return renderer.Render(new SmartObject(source, b.Width, b.Height, placed), limit);
        }
        if (!(mask.DefaultValue >= 0.5f)) return Resample(mask.Tiles);
        if (!exact)
        {
            var grid = mask.Tiles.ToBuilder();
            var region = stored;
            if (cover is { Width: > 0, Height: > 0 } c)
            {
                int x0 = Math.Min(c.X, stored.X), y0 = Math.Min(c.Y, stored.Y);
                region = RegionOps.TileAligned(new PixelRect(x0, y0, Math.Max(c.MaxX, stored.MaxX) - x0, Math.Max(c.MaxY, stored.MaxY) - y0));
            }
            foreach (var coord in region.TileCoords)
                if (grid[coord] is null) grid[coord] = Tile.Solid(Vector4.One);
            return Resample(grid.ToImmutable());
        }
        // Move what the mask hides (1 − value, zero outside the stored tiles), then turn it back.
        var hidden = new TileGrid.Builder();
        foreach (var (coord, tile) in mask.Tiles) hidden[coord] = Inverted(tile, emptyAsNil: true);
        var moved = Resample(hidden.ToImmutable());
        var result = new TileGrid.Builder();
        foreach (var (coord, tile) in moved) result[coord] = Inverted(tile, emptyAsNil: false);
        return result.ToImmutable();
    }

    /// <summary>
    /// 1 − value in every channel. A tile that becomes all zero is null (<paramref name="emptyAsNil"/>, for a sparse
    /// source) or stored as solid black (a mask must keep it: missing means default).
    /// </summary>
    internal static Tile? Inverted(Tile tile, bool emptyAsNil)
    {
        Tile? empty = emptyAsNil ? null : Tile.Solid(Vector4.Zero);
        if (tile.IsSolid)
        {
            float v = 1 - Math.Clamp(tile.SolidColor.W, 0, 1);
            return v <= 0 ? empty : Tile.Solid(new Vector4(v));
        }
        var px = tile.PixelSpan;
        var values = new float[TileGeometry.FloatCount];
        for (int i = 0; i < values.Length; i++) values[i] = 1 - Math.Clamp((float)px[i], 0, 1);
        return Tile.Make(values) ?? empty;
    }
}
