// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Selection.swift

using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Glimmer.Core;

/// <summary>
/// A soft pixel selection in document space: 1 = selected, 0 = not. Stored tiles hold the value in every channel;
/// missing tiles use <see cref="DefaultValue"/>.
/// </summary>
public sealed record Selection
{
    public Selection(TileGrid? tiles = null, float defaultValue = 0)
    {
        Tiles = tiles ?? TileGrid.Empty;
        DefaultValue = defaultValue;
    }

    public TileGrid Tiles { get; init; }
    public float DefaultValue { get; init; }

    public enum Operation { Replace, Add, Subtract, Intersect }

    public static Selection All() => new(defaultValue: 1);

    // MARK: Shapes

    public static Selection Rectangle(CGRectLike r) =>
        Rasterize(r.PixelBounds, (x, y) =>
        {
            // Exact area of the pixel square inside the rectangle.
            double w = Math.Max(0, Math.Min(x + 1.0, r.MaxX) - Math.Max(x, r.MinX));
            double h = Math.Max(0, Math.Min(y + 1.0, r.MaxY) - Math.Max(y, r.MinY));
            return (float)(w * h);
        });

    public static Selection Ellipse(CGRectLike r)
    {
        double cx = (r.MinX + r.MaxX) / 2, cy = (r.MinY + r.MaxY) / 2;
        double rx = Math.Max((r.MaxX - r.MinX) / 2, 1e-6), ry = Math.Max((r.MaxY - r.MinY) / 2, 1e-6);
        return Rasterize(r.PixelBounds, (x, y) =>
        {
            // Approximate signed distance to the ellipse edge, 1 px anti-aliasing.
            double dx = (x + 0.5 - cx) / rx, dy = (y + 0.5 - cy) / ry;
            double f = Math.Sqrt(dx * dx + dy * dy);
            double d = (f - 1) * Math.Min(rx, ry);
            return (float)Math.Min(Math.Max(0.5 - d, 0), 1);
        });
    }

    /// <summary>A closed polygon (lasso), anti-aliased, even-odd filled.</summary>
    public static Selection Polygon(IReadOnlyList<Vec2> points) => Polygons([points]);

    /// <summary>
    /// The union of several polygons (each filled even-odd), rasterized in one pass: combining them one by one costs a
    /// full selection merge per polygon. Drawn with Skia into an 8-bit coverage buffer, as the Mac draws them with
    /// Core Graphics into an 8-bit grey context.
    /// </summary>
    public static Selection Polygons(IEnumerable<IReadOnlyList<Vec2>> polygons)
    {
        var list = polygons.Where(p => p.Count >= 3).ToList();
        if (list.Count == 0) return new Selection();
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var polygon in list)
        {
            foreach (var p in polygon)
            {
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
        }
        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY)) return new Selection();
        long x0 = (long)Math.Floor(minX) - 1, y0 = (long)Math.Floor(minY) - 1;
        long x1 = (long)Math.Ceiling(maxX) + 1, y1 = (long)Math.Ceiling(maxY) + 1;
        long w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0 || w * h >= 400_000_000 || x0 < int.MinValue / 2 || y0 < int.MinValue / 2 || x1 > int.MaxValue / 2 || y1 > int.MaxValue / 2)
            return new Selection();
        var bytes = PolygonCoverage.Fill(list, (int)x0, (int)y0, (int)w, (int)h);
        var rect = new PixelRect((int)x0, (int)y0, (int)w, (int)h);
        int stride = (int)w;
        return Rasterize(rect, (x, y) => bytes[(long)(y - rect.Y) * stride + (x - rect.X)] / 255f);
    }

    /// <summary>
    /// Builds a selection from per-pixel values over <paramref name="bounds"/> (everything else 0). Tiles are built on
    /// several threads at once, so <paramref name="value"/> must be safe to call from more than one thread.
    /// </summary>
    public static Selection Rasterize(PixelRect bounds, Func<int, int, float> value)
    {
        var coords = bounds.TileCoords;
        var made = new Tile?[coords.Length];
        void Build(int i)
        {
            var coord = coords[i];
            var tileRect = coord.Rect;
            var part = tileRect.Intersection(bounds);
            var buffer = new Half[TileGeometry.FloatCount];
            for (int y = part.Y; y < part.MaxY; y++)
            {
                for (int x = part.X; x < part.MaxX; x++)
                {
                    var v = (Half)value(x, y);
                    int o = ((y - tileRect.Y) * TileGeometry.Size + (x - tileRect.X)) * 4;
                    buffer[o] = v; buffer[o + 1] = v; buffer[o + 2] = v; buffer[o + 3] = v;
                }
            }
            made[i] = Tile.MakeOwned(buffer);
        }
        if (coords.Length > 1) Parallel.For(0, coords.Length, Build);
        else if (coords.Length == 1) Build(0);
        var b = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++)
            if (made[i] is { } tile) b[coords[i]] = tile;
        return new Selection(b.ToImmutable());
    }

    /// <summary>Builds a selection from a mask covering <paramref name="rect"/> (values 0...1, row-major).</summary>
    public static Selection From(float[] mask, PixelRect rect) =>
        Rasterize(rect, (x, y) => mask[(long)(y - rect.Y) * rect.Width + (x - rect.X)]);

    // MARK: Combining

    private static float Combine(Operation op, float a, float b) => op switch
    {
        Operation.Replace => b,
        Operation.Add => Math.Max(a, b),
        Operation.Subtract => a * (1 - b),
        _ => Math.Min(a, b),   // intersect
    };

    public Selection Combined(Selection other, Operation op)
    {
        if (op == Operation.Replace) return other;
        var coords = Tiles.Coords.Union(other.Tiles.Coords).ToArray();
        var made = new Tile[coords.Length];
        void Build(int i)
        {
            var a = TileAt(coords[i]).ToHalfArray();
            var b = other.TileAt(coords[i]).ToHalfArray();
            var output = new Half[a.Length];
            for (int k = 0; k < a.Length; k += 4)
            {
                var v = (Half)Combine(op, (float)a[k + 3], (float)b[k + 3]);
                output[k] = v; output[k + 1] = v; output[k + 2] = v; output[k + 3] = v;
            }
            made[i] = Tile.MakeOwned(output) ?? Tile.Solid(Vector4.Zero);
        }
        if (coords.Length > 1) Parallel.For(0, coords.Length, Build);
        else if (coords.Length == 1) Build(0);
        var builder = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++) builder[coords[i]] = made[i];
        return new Selection(builder.ToImmutable(), Combine(op, DefaultValue, other.DefaultValue)).DropDefaultTiles();
    }

    public Selection Inverted()
    {
        var entries = Tiles.ToArray();
        var made = new Tile[entries.Length];
        void Build(int i)
        {
            var p = entries[i].Value.ToHalfArray();
            var output = new Half[p.Length];
            for (int k = 0; k < p.Length; k += 4)
            {
                var v = (Half)(1 - (float)p[k + 3]);
                output[k] = v; output[k + 1] = v; output[k + 2] = v; output[k + 3] = v;
            }
            made[i] = Tile.MakeOwned(output) ?? Tile.Solid(Vector4.Zero);
        }
        if (entries.Length > 1) Parallel.For(0, entries.Length, Build);
        else if (entries.Length == 1) Build(0);
        var builder = new TileGrid.Builder();
        for (int i = 0; i < entries.Length; i++) builder[entries[i].Key] = made[i];
        return new Selection(builder.ToImmutable(), 1 - DefaultValue);
    }

    /// <summary>Removes stored tiles that equal the default value.</summary>
    internal Selection DropDefaultTiles()
    {
        var builder = Tiles.ToBuilder();
        foreach (var (coord, tile) in Tiles)
        {
            if (tile.IsSolid && tile.SolidColor.W == DefaultValue) builder.Remove(coord);
            if (tile.IsTransparentSolid && DefaultValue == 0) builder.Remove(coord);
        }
        return this with { Tiles = builder.ToImmutable() };
    }

    // MARK: Reading

    public Tile TileAt(TileCoord coord) => Tiles[coord] ?? Tile.Solid(new Vector4(DefaultValue));

    /// <summary>Selection values (0...1) for <paramref name="rect"/>, row-major.</summary>
    public float[] Values(PixelRect rect)
    {
        var output = new float[Math.Max(0, rect.Area)];
        Tiles.ReadChannel(rect, output, channel: 3, defaultValue: DefaultValue);
        return output;
    }

    /// <summary>Tight bounds of selected pixels within <paramref name="limit"/> (null when nothing is selected).</summary>
    public PixelRect? Bounds(PixelRect limit)
    {
        if (DefaultValue > 0) return limit;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var gate = new Lock();
        var entries = Tiles.ToArray();
        float threshold = TileBoundsCache.VisibleAlpha;
        Parallel.ForEach(entries, entry =>
        {
            var (coord, tile) = entry;
            var part = coord.Rect.Intersection(limit);
            if (part.IsEmpty) return;
            int lx = int.MaxValue, ly = int.MaxValue, hx = int.MinValue, hy = int.MinValue;
            if (tile.IsSolid)
            {
                if (!(tile.SolidColor.W > 0)) return;
                (lx, ly, hx, hy) = (part.X, part.Y, part.MaxX, part.MaxY);
            }
            else
            {
                var p = tile.PixelSpan;
                var r = coord.Rect;
                for (int y = part.Y; y < part.MaxY; y++)
                {
                    for (int x = part.X; x < part.MaxX; x++)
                    {
                        if (!((float)p[((y - r.Y) * TileGeometry.Size + (x - r.X)) * 4 + 3] > threshold)) continue;
                        lx = Math.Min(lx, x); ly = Math.Min(ly, y);
                        hx = Math.Max(hx, x + 1); hy = Math.Max(hy, y + 1);
                    }
                }
                if (lx >= hx) return;
            }
            lock (gate)
            {
                minX = Math.Min(minX, lx); minY = Math.Min(minY, ly);
                maxX = Math.Max(maxX, hx); maxY = Math.Max(maxY, hy);
            }
        });
        if (minX >= maxX) return null;
        return new PixelRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>A layer mask with the same shape.</summary>
    public LayerMask AsMask => new(Tiles, DefaultValue);
}

/// <summary>A floating-point rectangle (CoreGraphics-free so Core stays portable).</summary>
public readonly record struct CGRectLike
{
    public CGRectLike(double x0, double y0, double x1, double y1)
    {
        MinX = Math.Min(x0, x1); MaxX = Math.Max(x0, x1);
        MinY = Math.Min(y0, y1); MaxY = Math.Max(y0, y1);
    }

    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    /// <summary>The pixels the rectangle touches.</summary>
    public PixelRect PixelBounds
    {
        get
        {
            int x0 = (int)Math.Floor(MinX), y0 = (int)Math.Floor(MinY);
            return new PixelRect(x0, y0, (int)Math.Ceiling(MaxX) - x0, (int)Math.Ceiling(MaxY) - y0);
        }
    }
}

/// <summary>Anti-aliased polygon coverage, the part of <c>Selection.polygons</c> the Mac does with a CGContext.</summary>
internal static class PolygonCoverage
{
    /// <summary>Skia draws in bands no larger than this, so coordinates stay well inside its fixed-point range.</summary>
    private const int Band = 8192;

    /// <summary>
    /// One byte of coverage per pixel of the w × h area whose top-left is (x0, y0): each polygon filled even-odd and
    /// painted over the ones before (so overlapping polygons add up to full coverage).
    /// </summary>
    public static unsafe byte[] Fill(List<IReadOnlyList<Vec2>> polygons, int x0, int y0, int w, int h)
    {
        var bytes = new byte[(long)w * h];
        using var paths = new DisposableList<SKPath>();
        foreach (var polygon in polygons)
        {
            var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.MoveTo((float)(polygon[0].X - x0), (float)(polygon[0].Y - y0));
            for (int i = 1; i < polygon.Count; i++) path.LineTo((float)(polygon[i].X - x0), (float)(polygon[i].Y - y0));
            path.Close();
            paths.Add(path);
        }
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.SrcOver };
        fixed (byte* basePtr = bytes)
        {
            for (int by = 0; by < h; by += Band)
            {
                for (int bx = 0; bx < w; bx += Band)
                {
                    int bw = Math.Min(Band, w - bx), bh = Math.Min(Band, h - by);
                    var info = new SKImageInfo(bw, bh, SKColorType.Alpha8, SKAlphaType.Premul);
                    using var bitmap = new SKBitmap();
                    if (!bitmap.InstallPixels(info, (IntPtr)(basePtr + (long)by * w + bx), w)) throw new GraphicsMemoryException();
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Translate(-bx, -by);
                    foreach (var path in paths) canvas.DrawPath(path, paint);
                    canvas.Flush();
                }
            }
        }
        return bytes;
    }

    private sealed class DisposableList<T> : List<T>, IDisposable where T : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in this) item.Dispose();
        }
    }
}
