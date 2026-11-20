// New file, no Swift original. On the Mac ImageImporter.image(from:) returns a CGImage: decoded, turned upright, and
// colour-converted only when it is drawn into tiles. This is that picture here: the decoded samples as the file encodes
// them (8 or 16 bits, half or single floats), the file's orientation, and how the samples become linear sRGB
// (SourceColor). ImageImporter.Tiles makes Glimmer's tiles from it.
using System.Buffers;
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.Formats;

/// <summary>
/// A decoded picture, before it becomes tiles. <see cref="Width"/> and <see cref="Height"/> are the upright size (an
/// EXIF orientation that turns the picture swaps them).
/// </summary>
/// <remarks>
/// <see cref="IPlatformImageDecoder"/> implementations make one with <see cref="FromRgba8"/>, <see cref="FromRgba16"/>
/// or <see cref="FromLinearRgba"/>. A picture is never changed after it is made, so it can be shared between threads.
/// </remarks>
public sealed class DecodedImage
{
    private readonly int storedWidth, storedHeight;
    private readonly int orientation;
    private readonly byte[]? bytes;
    private readonly ushort[]? shorts;
    private readonly Half[]? halves;
    private readonly float[]? floats;
    /// <summary>Samples per pixel: 1 (grey), 2 (grey and alpha) or 4 (RGBA). Alpha is straight, never premultiplied.</summary>
    private readonly int channels;
    internal SourceColor Color { get; }

    private DecodedImage(int width, int height, int orientation, int channels, SourceColor color,
                         byte[]? bytes = null, ushort[]? shorts = null, Half[]? halves = null, float[]? floats = null)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A picture has at least one pixel.");
        if (orientation is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(orientation), "An EXIF orientation is 1 to 8.");
        long needed = (long)width * height * channels;
        long have = bytes?.LongLength ?? shorts?.LongLength ?? halves?.LongLength ?? floats?.LongLength ?? 0;
        if (have != needed) throw new ArgumentException($"{have} samples for a {width} × {height} picture with {channels} per pixel.");
        storedWidth = width; storedHeight = height; this.orientation = orientation; this.channels = channels; Color = color;
        this.bytes = bytes; this.shorts = shorts; this.halves = halves; this.floats = floats;
    }

    /// <summary>The width upright.</summary>
    public int Width => orientation >= 5 ? storedHeight : storedWidth;

    /// <summary>The height upright.</summary>
    public int Height => orientation >= 5 ? storedWidth : storedHeight;

    /// <summary>The EXIF orientation the picture was stored with (1 = upright as stored).</summary>
    public int Orientation => orientation;

    // MARK: Making one

    /// <summary>8-bit RGBA, straight alpha, row after row as the file stores the picture.</summary>
    /// <param name="iccProfile">The file's RGB profile; null (or one that can't be read) is sRGB.</param>
    /// <param name="orientation">The file's EXIF orientation, 1–8; the picture is turned upright when it becomes tiles.</param>
    public static DecodedImage FromRgba8(int width, int height, byte[] rgba, ReadOnlyMemory<byte>? iccProfile = null, int orientation = 1) =>
        new(width, height, orientation, 4, ColorOf(iccProfile, gray: false), bytes: rgba);

    /// <summary>16-bit RGBA, straight alpha, row after row as the file stores the picture.</summary>
    public static DecodedImage FromRgba16(int width, int height, ushort[] rgba, ReadOnlyMemory<byte>? iccProfile = null, int orientation = 1) =>
        new(width, height, orientation, 4, ColorOf(iccProfile, gray: false), shorts: rgba);

    /// <summary>RGBA floats in linear light with sRGB's primaries (scRGB), straight alpha; values above 1 are kept.</summary>
    public static DecodedImage FromLinearRgba(int width, int height, float[] rgba, int orientation = 1) =>
        new(width, height, orientation, 4, SourceColor.LinearSrgb, floats: rgba);

    internal static DecodedImage Bytes(int width, int height, int channels, byte[] samples, SourceColor color, int orientation = 1) =>
        new(width, height, orientation, channels, color, bytes: samples);

    internal static DecodedImage Shorts(int width, int height, int channels, ushort[] samples, SourceColor color, int orientation = 1) =>
        new(width, height, orientation, channels, color, shorts: samples);

    internal static DecodedImage Halves(int width, int height, Half[] rgba, SourceColor color, int orientation = 1) =>
        new(width, height, orientation, 4, color, halves: rgba);

    internal static DecodedImage Floats(int width, int height, int channels, float[] samples, SourceColor color, int orientation = 1) =>
        new(width, height, orientation, channels, color, floats: samples);

    private static SourceColor ColorOf(ReadOnlyMemory<byte>? icc, bool gray) =>
        icc is { Length: > 0 } profile ? SourceColor.FromIcc(profile.Span, gray) ?? (gray ? SourceColor.GrayGamma22 : SourceColor.Srgb)
                                       : gray ? SourceColor.GrayGamma22 : SourceColor.Srgb;

    // MARK: Reading it

    /// <summary>
    /// The stored pixel shown at upright (<paramref name="x"/>, <paramref name="y"/>), for each EXIF orientation:
    /// 2 mirrored, 3 turned half round, 4 mirrored top to bottom, 5 transposed, 6 turned a quarter clockwise to show,
    /// 7 transverse, 8 turned a quarter anticlockwise to show.
    /// </summary>
    internal (int X, int Y) Stored(int x, int y) => orientation switch
    {
        2 => (storedWidth - 1 - x, y),
        3 => (storedWidth - 1 - x, storedHeight - 1 - y),
        4 => (x, storedHeight - 1 - y),
        5 => (y, x),
        6 => (y, storedHeight - 1 - x),
        7 => (storedWidth - 1 - y, storedHeight - 1 - x),
        8 => (storedWidth - 1 - y, x),
        _ => (x, y),
    };

    /// <summary>The upright pixel at (<paramref name="x"/>, <paramref name="y"/>) as Glimmer keeps pixels: linear sRGB,
    /// premultiplied.</summary>
    public Vector4 PixelAt(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(x));
        var (sx, sy) = Stored(x, y);
        return Linear((long)sy * storedWidth + sx);
    }

    private Vector4 Linear(long pixel)
    {
        float r, g, b, a;
        long i = pixel * channels;
        if (bytes is { } b8)
        {
            var lut = Color.Lut8;
            if (channels == 4) { r = lut[b8[i]]; g = lut[b8[i + 1]]; b = lut[b8[i + 2]]; a = b8[i + 3] / 255f; }
            else { r = g = b = lut[b8[i]]; a = channels == 2 ? b8[i + 1] / 255f : 1; }
        }
        else if (shorts is { } s16)
        {
            var lut = Color.Lut16;
            if (channels == 4) { r = lut[s16[i]]; g = lut[s16[i + 1]]; b = lut[s16[i + 2]]; a = s16[i + 3] / 65535f; }
            else { r = g = b = lut[s16[i]]; a = channels == 2 ? s16[i + 1] / 65535f : 1; }
        }
        else if (halves is { } h16)
        {
            var lut = Color.LutHalf;
            r = lut[BitConverter.HalfToUInt16Bits(h16[i])]; g = lut[BitConverter.HalfToUInt16Bits(h16[i + 1])];
            b = lut[BitConverter.HalfToUInt16Bits(h16[i + 2])]; a = (float)h16[i + 3];
        }
        else
        {
            var f = floats!;
            if (channels == 4) { r = Lin(f[i]); g = Lin(f[i + 1]); b = Lin(f[i + 2]); a = f[i + 3]; }
            else { r = g = b = Lin(f[i]); a = channels == 2 ? f[i + 1] : 1; }
        }
        Color.ToSrgb(ref r, ref g, ref b);
        a = float.IsNaN(a) ? 0 : Math.Clamp(a, 0f, 1f);
        return new Vector4(r * a, g * a, b * a, a);
    }

    private float Lin(float v) => SourceColor.Sanitize(Color.IsLinear ? v : (float)Color.Linear(v));

    /// <summary>Upright row <paramref name="y"/> as Glimmer keeps pixels (linear sRGB, premultiplied): R, G, B, A for each
    /// of <see cref="Width"/> pixels. (New: for RAW development, which works on the whole picture.)</summary>
    internal void Row(int y, Span<float> destination)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        for (int x = 0; x < Width; x++)
        {
            var (sx, sy) = Stored(x, y);
            var v = Linear((long)sy * storedWidth + sx);
            destination[x * 4] = v.X; destination[x * 4 + 1] = v.Y; destination[x * 4 + 2] = v.Z; destination[x * 4 + 3] = v.W;
        }
    }

    /// <summary>Writes the part of the picture inside <paramref name="tile"/> (document pixels) into a tile's float buffer,
    /// the picture's top-left at <paramref name="origin"/>. The rest of the buffer is left as it is.</summary>
    internal void Fill(PixelRect tile, (int X, int Y) origin, Span<float> buffer)
    {
        var part = tile.Intersection(new PixelRect(origin.X, origin.Y, Width, Height));
        for (int y = part.Y; y < part.MaxY; y++)
        {
            int row = (y - tile.Y) * TileGeometry.Size;
            for (int x = part.X; x < part.MaxX; x++)
            {
                var (sx, sy) = Stored(x - origin.X, y - origin.Y);
                var v = Linear((long)sy * storedWidth + sx);
                int o = (row + x - tile.X) * 4;
                buffer[o] = v.X; buffer[o + 1] = v.Y; buffer[o + 2] = v.Z; buffer[o + 3] = v.W;
            }
        }
    }

    /// <summary>The picture as tiles with its top-left at <paramref name="origin"/> (Swift's <c>ImageImporter.tiles(from:origin:)</c>).
    /// Tiles are made in parallel; transparent ones are not stored and uniform ones are one colour.</summary>
    internal TileGrid ToTiles((int X, int Y) origin)
    {
        if (origin.X < -(1 << 30) || origin.Y < -(1 << 30) || (long)origin.X + Width > 1 << 30 || (long)origin.Y + Height > 1 << 30)
            throw new ArgumentOutOfRangeException(nameof(origin), "The picture would lie outside the space a document has.");
        var coords = new PixelRect(origin.X, origin.Y, Width, Height).TileCoords;
        var made = new Tile?[coords.Length];
        void Make(int i)
        {
            var buffer = ArrayPool<float>.Shared.Rent(TileGeometry.FloatCount);
            try
            {
                var span = buffer.AsSpan(0, TileGeometry.FloatCount);
                span.Clear();
                Fill(coords[i].Rect, origin, span);
                made[i] = Tile.Make((ReadOnlySpan<float>)span);
            }
            finally { ArrayPool<float>.Shared.Return(buffer); }
        }
        if (coords.Length > 1) Parallel.For(0, coords.Length, Make);
        else if (coords.Length == 1) Make(0);
        var grid = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++)
            if (made[i] is { } tile) grid[coords[i]] = tile;
        return grid.ToImmutable();
    }
}
