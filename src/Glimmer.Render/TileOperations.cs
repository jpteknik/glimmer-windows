// Ported from Packages/GlimmerKit/Sources/GlimmerRender/TileOperations.swift
using System.Collections.Immutable;
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Operations that produce new tile grids (never mutating existing tiles).</summary>
public static class TileOperations
{
    /// <summary>Shifts a grid by whole pixels.</summary>
    /// <remarks>
    /// As on the Mac, every destination tile a moved tile touches is stored, transparent or not (a partly covered tile
    /// is transparent where nothing landed). That matters for masks, where a missing tile reads as the default.
    /// </remarks>
    public static TileGrid Translate(TileGrid grid, (int X, int Y) offset)
    {
        if (offset == (0, 0) || grid.IsEmpty) return grid;
        const int s = TileGeometry.Size;

        // Whole-tile moves only re-key the grid.
        if (offset.X % s == 0 && offset.Y % s == 0)
        {
            var moved = new TileGrid.Builder();
            foreach (var (c, tile) in grid) moved[new TileCoord(c.X + offset.X / s, c.Y + offset.Y / s)] = tile;
            return moved.ToImmutable();
        }

        // Which source tiles land on each destination tile.
        var sources = new Dictionary<TileCoord, List<(TileCoord Coord, Tile Tile)>>();
        foreach (var (c, tile) in grid)
        {
            foreach (var dest in c.Rect.OffsetBy(offset.X, offset.Y).TileCoords)
            {
                if (!sources.TryGetValue(dest, out var list)) sources[dest] = list = [];
                list.Add((c, tile));
            }
        }

        var destinations = sources.Keys.ToArray();
        var made = new Tile[destinations.Length];
        Parallel.For(0, destinations.Length, i =>
        {
            var dest = destinations[i];
            var parts = sources[dest];
            // A solid colour that covers the whole destination stays solid.
            var first = parts[0].Tile;
            if (first.IsSolid && parts.All(p => p.Tile.IsSolid && p.Tile.SolidColor == first.SolidColor)
                && CoversFully(dest, parts.Select(p => p.Coord), offset))
            {
                made[i] = Tile.Solid(first.SolidColor);
                return;
            }
            var buffer = new Half[TileGeometry.FloatCount];
            var target = dest.Rect;
            foreach (var (src, tile) in parts)
            {
                var placed = src.Rect.OffsetBy(offset.X, offset.Y).Intersection(target);
                for (int y = placed.Y; y < placed.MaxY; y++)
                {
                    var d = buffer.AsSpan(((y - target.Y) * s + (placed.X - target.X)) * 4, placed.Width * 4);
                    if (tile.IsSolid)
                    {
                        var c = tile.SolidColor;
                        Half r = (Half)c.X, g = (Half)c.Y, b = (Half)c.Z, a = (Half)c.W;
                        for (int x = 0; x < d.Length; x += 4)
                        {
                            d[x] = r; d[x + 1] = g; d[x + 2] = b; d[x + 3] = a;
                        }
                    }
                    else
                    {
                        int sy = y - offset.Y - src.Rect.Y, sx = placed.X - offset.X - src.Rect.X;
                        tile.PixelSpan.Slice((sy * s + sx) * 4, placed.Width * 4).CopyTo(d);
                    }
                }
            }
            made[i] = Tile.MakeOwned(buffer) ?? Tile.Solid(Vector4.Zero);
        });
        var result = new TileGrid.Builder();
        for (int i = 0; i < destinations.Length; i++) result[destinations[i]] = made[i];
        return result.ToImmutable();
    }

    private static bool CoversFully(TileCoord dest, IEnumerable<TileCoord> parts, (int X, int Y) offset)
    {
        // Sum of overlap areas equals the tile area (source tiles never overlap each other).
        var target = dest.Rect;
        long area = 0;
        foreach (var src in parts)
        {
            var r = src.Rect.OffsetBy(offset.X, offset.Y).Intersection(target);
            area += (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);
        }
        return area == (long)target.Width * target.Height;
    }

    /// <summary>Moves a whole layer: pixels, mask, smart-object placement, gradient points and (for groups) every descendant.</summary>
    public static Layer Translate(Layer layer, (int X, int Y) offset)
    {
        if (offset == (0, 0)) return layer;
        var moved = layer;
        var d = new Vec2(offset.X, offset.Y);
        if (layer.Mask is { } mask) moved = moved with { Mask = mask with { Tiles = Translate(mask.Tiles, offset) } };
        moved = moved with { VectorMask = layer.VectorMask?.Applying(new AffineTransform(tx: d.X, ty: d.Y)) };
        switch (layer.Content)
        {
            case RasterContent raster:
                moved = moved with { Content = new RasterContent(Translate(raster.Tiles, offset)) };
                break;
            case SmartContent smart:
                moved = moved with { Content = new SmartContent(smart.Smart with { Transform = smart.Smart.Transform.Translated(d) }) };
                break;
            case FillContent { Fill: Fill.Gradient g }:
                moved = moved with
                {
                    Content = new FillContent(new Fill.Gradient(g.Value with { Start = g.Value.Start + d, End = g.Value.End + d })),
                };
                break;
            case GroupContent group:
                moved = moved with
                {
                    Content = new GroupContent(group.Group with
                    {
                        Children = ImmutableArray.CreateRange(group.Group.Children.Select(c => Translate(c, offset))),
                    }),
                };
                break;
            case TextContent textContent:
            {
                var text = textContent.Text;
                if (text.Transform is { } t)
                {
                    // A transformed text moves by moving its transform: T(layout) + d.
                    text = text with { Transform = new AffineTransform(tx: d.X, ty: d.Y).Concatenating(t) };
                }
                else
                {
                    text = text with
                    {
                        Origin = text.Origin + d,
                        OnPath = text.OnPath is { } path ? ImmutableArray.CreateRange(path.Select(p => p + d)) : null,
                    };
                }
                moved = moved with { Content = new TextContent(text) };
                break;
            }
        }
        return moved;
    }
}
