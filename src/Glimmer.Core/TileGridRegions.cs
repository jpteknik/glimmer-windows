// Region reads and writes on a TileGrid. The Mac version does these on the GPU (blits between tile textures and
// staging textures in GlimmerRender); here every package shares one CPU implementation. No Swift file of its own.

using System.Collections.Immutable;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace Glimmer.Core;

public sealed partial class TileGrid
{
    /// <summary>Regions at least this many pixels big are worked on one tile per core.</summary>
    private const long ParallelPixels = 2L * TileGeometry.PixelCount;

    /// <summary>
    /// Copies the pixels of <paramref name="rect"/> into <paramref name="rgba"/>: row-major RGBA floats, rect-sized
    /// (<c>Width × Height × 4</c>), premultiplied linear. Where there is no tile the region is transparent (zero).
    /// </summary>
    public unsafe void ReadRegion(PixelRect rect, Span<float> rgba)
    {
        if (rect.IsEmpty) return;
        long needed = rect.Area * 4;
        if (rgba.Length < needed) throw new ArgumentException($"The buffer holds {rgba.Length} floats; the region needs {needed}.", nameof(rgba));
        var coords = rect.TileCoords;
        fixed (float* p = rgba)
        {
            nint dst = (nint)p;
            if (coords.Length > 1 && rect.Area >= ParallelPixels)
                Parallel.For(0, coords.Length, i => ReadTile(coords[i], rect, (float*)dst));
            else
                foreach (var coord in coords) ReadTile(coord, rect, p);
        }
    }

    /// <summary>The pixels of <paramref name="rect"/> as a new array (see <see cref="ReadRegion(PixelRect, Span{float})"/>).</summary>
    public float[] ReadRegion(PixelRect rect)
    {
        var result = new float[rect.Area * 4];
        ReadRegion(rect, result);
        return result;
    }

    private unsafe void ReadTile(TileCoord coord, PixelRect rect, float* dst)
    {
        var tileRect = coord.Rect;
        var part = tileRect.Intersection(rect);
        if (part.IsEmpty) return;
        tiles.TryGetValue(coord, out var tile);
        int rowFloats = part.Width * 4;
        for (int y = part.Y; y < part.MaxY; y++)
        {
            var d = new Span<float>(dst + ((long)(y - rect.Y) * rect.Width + (part.X - rect.X)) * 4, rowFloats);
            if (tile is null)
            {
                d.Clear();
            }
            else if (tile.IsSolid)
            {
                MemoryMarshal.Cast<float, Vector4>(d).Fill(tile.SolidColor);
            }
            else
            {
                int o = ((y - tileRect.Y) * TileGeometry.Size + (part.X - tileRect.X)) * 4;
                TensorPrimitives.ConvertToSingle(tile.PixelSpan.Slice(o, rowFloats), d);
            }
        }
    }

    /// <summary>
    /// A grid where the pixels of <paramref name="rect"/> are replaced by <paramref name="rgba"/> (row-major RGBA
    /// floats, rect-sized, premultiplied linear). Tiles the rect touches are rebuilt and classified as
    /// <see cref="Tile.Make(ReadOnlySpan{float})"/> does: fully transparent ones disappear, uniform ones become solid.
    /// Tiles outside the rect are shared with this grid.
    /// </summary>
    public unsafe TileGrid WriteRegion(PixelRect rect, ReadOnlySpan<float> rgba)
    {
        if (rect.IsEmpty) return this;
        long needed = rect.Area * 4;
        if (rgba.Length < needed) throw new ArgumentException($"The buffer holds {rgba.Length} floats; the region needs {needed}.", nameof(rgba));
        var coords = rect.TileCoords;
        var made = new Tile?[coords.Length];
        fixed (float* p = rgba)
        {
            nint src = (nint)p;
            if (coords.Length > 1 && rect.Area >= ParallelPixels)
                Parallel.For(0, coords.Length, i => made[i] = WriteTile(coords[i], rect, (float*)src));
            else
                for (int i = 0; i < coords.Length; i++) made[i] = WriteTile(coords[i], rect, p);
        }
        return Apply(coords, made);
    }

    private unsafe Tile? WriteTile(TileCoord coord, PixelRect rect, float* src)
    {
        var tileRect = coord.Rect;
        var part = tileRect.Intersection(rect);
        var buffer = new Half[TileGeometry.FloatCount];
        if (part != tileRect && tiles.TryGetValue(coord, out var existing)) existing.CopyTo(buffer.AsSpan());
        int rowFloats = part.Width * 4;
        for (int y = part.Y; y < part.MaxY; y++)
        {
            var s = new ReadOnlySpan<float>(src + ((long)(y - rect.Y) * rect.Width + (part.X - rect.X)) * 4, rowFloats);
            int o = ((y - tileRect.Y) * TileGeometry.Size + (part.X - tileRect.X)) * 4;
            TensorPrimitives.ConvertToHalf(s, buffer.AsSpan(o, rowFloats));
        }
        return Tile.MakeOwned(buffer);
    }

    private TileGrid Apply(TileCoord[] coords, Tile?[] made)
    {
        var b = tiles.ToBuilder();
        for (int i = 0; i < coords.Length; i++)
        {
            if (made[i] is { } tile) b[coords[i]] = tile;
            else b.Remove(coords[i]);
        }
        return new TileGrid(b.ToImmutable());
    }

    /// <summary>
    /// One channel of <paramref name="rect"/> (row-major, rect-sized) into <paramref name="values"/>. Masks and
    /// selections store their value in every channel; missing tiles read as <paramref name="defaultValue"/>.
    /// </summary>
    public unsafe void ReadChannel(PixelRect rect, Span<float> values, int channel = 3, float defaultValue = 0)
    {
        if (rect.IsEmpty) return;
        if ((uint)channel > 3) throw new ArgumentOutOfRangeException(nameof(channel));
        long needed = rect.Area;
        if (values.Length < needed) throw new ArgumentException($"The buffer holds {values.Length} values; the region needs {needed}.", nameof(values));
        var coords = rect.TileCoords;
        fixed (float* p = values)
        {
            nint dst = (nint)p;
            if (coords.Length > 1 && rect.Area >= ParallelPixels)
                Parallel.For(0, coords.Length, i => ReadChannelTile(coords[i], rect, (float*)dst, channel, defaultValue));
            else
                foreach (var coord in coords) ReadChannelTile(coord, rect, p, channel, defaultValue);
        }
    }

    /// <summary>One channel of <paramref name="rect"/> as a new array (see <see cref="ReadChannel(PixelRect, Span{float}, int, float)"/>).</summary>
    public float[] ReadChannel(PixelRect rect, int channel = 3, float defaultValue = 0)
    {
        var result = new float[rect.Area];
        ReadChannel(rect, result, channel, defaultValue);
        return result;
    }

    private unsafe void ReadChannelTile(TileCoord coord, PixelRect rect, float* dst, int channel, float defaultValue)
    {
        var tileRect = coord.Rect;
        var part = tileRect.Intersection(rect);
        if (part.IsEmpty) return;
        tiles.TryGetValue(coord, out var tile);
        for (int y = part.Y; y < part.MaxY; y++)
        {
            var d = new Span<float>(dst + (long)(y - rect.Y) * rect.Width + (part.X - rect.X), part.Width);
            if (tile is null)
            {
                d.Fill(defaultValue);
            }
            else if (tile.IsSolid)
            {
                var c = tile.SolidColor;
                d.Fill(channel switch { 0 => c.X, 1 => c.Y, 2 => c.Z, _ => c.W });
            }
            else
            {
                var px = tile.PixelSpan;
                int o = ((y - tileRect.Y) * TileGeometry.Size + (part.X - tileRect.X)) * 4 + channel;
                for (int x = 0; x < d.Length; x++) d[x] = (float)px[o + x * 4];
            }
        }
    }

    /// <summary>
    /// A grid where <paramref name="rect"/> holds <paramref name="values"/> (row-major, rect-sized) in every channel,
    /// as masks and selections are stored. Missing tiles count as <paramref name="defaultValue"/> where the rect only
    /// partly covers them. A rebuilt tile that is uniformly the default is left out; one that is fully zero while the
    /// default isn't is kept as a solid zero tile.
    /// </summary>
    public unsafe TileGrid WriteChannel(PixelRect rect, ReadOnlySpan<float> values, float defaultValue = 0)
    {
        if (rect.IsEmpty) return this;
        long needed = rect.Area;
        if (values.Length < needed) throw new ArgumentException($"The buffer holds {values.Length} values; the region needs {needed}.", nameof(values));
        var coords = rect.TileCoords;
        var made = new Tile?[coords.Length];
        fixed (float* p = values)
        {
            nint src = (nint)p;
            if (coords.Length > 1 && rect.Area >= ParallelPixels)
                Parallel.For(0, coords.Length, i => made[i] = WriteChannelTile(coords[i], rect, (float*)src, defaultValue));
            else
                for (int i = 0; i < coords.Length; i++) made[i] = WriteChannelTile(coords[i], rect, p, defaultValue);
        }
        return Apply(coords, made);
    }

    private unsafe Tile? WriteChannelTile(TileCoord coord, PixelRect rect, float* src, float defaultValue)
    {
        var tileRect = coord.Rect;
        var part = tileRect.Intersection(rect);
        var buffer = new Half[TileGeometry.FloatCount];
        if (part != tileRect)
        {
            if (tiles.TryGetValue(coord, out var existing)) existing.CopyTo(buffer.AsSpan());
            else if (defaultValue != 0) Tile.Solid(new Vector4(defaultValue)).CopyTo(buffer.AsSpan());
        }
        for (int y = part.Y; y < part.MaxY; y++)
        {
            var s = new ReadOnlySpan<float>(src + (long)(y - rect.Y) * rect.Width + (part.X - rect.X), part.Width);
            int o = ((y - tileRect.Y) * TileGeometry.Size + (part.X - tileRect.X)) * 4;
            for (int x = 0; x < s.Length; x++)
            {
                var h = (Half)s[x];
                int i = o + x * 4;
                buffer[i] = h; buffer[i + 1] = h; buffer[i + 2] = h; buffer[i + 3] = h;
            }
        }
        var tile = Tile.MakeOwned(buffer);
        if (tile is null) return defaultValue == 0 ? null : Tile.Solid(Vector4.Zero);
        if (tile.IsSolid && tile.SolidColor.W == (float)(Half)defaultValue) return null;
        return tile;
    }
}
