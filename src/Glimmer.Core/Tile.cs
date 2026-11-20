// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Tile.swift

using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Numerics.Tensors;

namespace Glimmer.Core;

// The Mac's `GPU` enum (the process-wide Metal device) and `TileTexture` (an immutable GPU texture with a never-reused
// identity) have no counterpart: there is no GPU here. A `Tile` holds its pixels itself and carries the identity.

public static class TileGeometry
{
    public const int Size = 256;
    /// <summary>RGBA, 16-bit float per channel (the Mac's <c>rgba16Float</c>).</summary>
    public const int BytesPerPixel = 8;
    public const int BytesPerRow = Size * BytesPerPixel;
    public const int ByteCount = Size * BytesPerRow;
    /// <summary>Channels in a tile (256 × 256 × RGBA): the length of a tile's <c>Half</c> or <c>float</c> buffer.</summary>
    public const int FloatCount = Size * Size * 4;
    public const int PixelCount = Size * Size;
}

/// <summary>A tile's position in the grid. Ordered y first, then x.</summary>
public readonly record struct TileCoord(int X, int Y) : IComparable<TileCoord>
{
    /// <summary>Top-left pixel of this tile in document space.</summary>
    public (int X, int Y) Origin => (X * TileGeometry.Size, Y * TileGeometry.Size);

    public PixelRect Rect => new(X * TileGeometry.Size, Y * TileGeometry.Size, TileGeometry.Size, TileGeometry.Size);

    public int CompareTo(TileCoord other) => Y != other.Y ? Y.CompareTo(other.Y) : X.CompareTo(other.X);

    public static bool operator <(TileCoord a, TileCoord b) => a.CompareTo(b) < 0;
    public static bool operator >(TileCoord a, TileCoord b) => a.CompareTo(b) > 0;
    public static bool operator <=(TileCoord a, TileCoord b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TileCoord a, TileCoord b) => a.CompareTo(b) >= 0;
}

/// <summary>Integer pixel rectangle, y-down.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int MaxX => X + Width;
    public int MaxY => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    /// <summary>Pixels in the rect (0 when empty).</summary>
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public PixelRect Intersection(PixelRect other)
    {
        int x0 = Math.Max(X, other.X), y0 = Math.Max(Y, other.Y);
        int x1 = Math.Min(MaxX, other.MaxX), y1 = Math.Min(MaxY, other.MaxY);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>The smallest rect that holds both; an empty rect adds nothing.</summary>
    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x0 = Math.Min(X, other.X), y0 = Math.Min(Y, other.Y);
        int x1 = Math.Max(MaxX, other.MaxX), y1 = Math.Max(MaxY, other.MaxY);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    public PixelRect OffsetBy(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public bool Contains(int x, int y) => x >= X && x < MaxX && y >= Y && y < MaxY;

    /// <summary>Whether <paramref name="other"/> lies wholly inside (an empty rect is inside every rect).</summary>
    public bool Contains(PixelRect other) =>
        other.IsEmpty || (other.X >= X && other.Y >= Y && other.MaxX <= MaxX && other.MaxY <= MaxY);

    /// <summary>Tiles that overlap this rect, row by row (y, then x).</summary>
    public TileCoord[] TileCoords
    {
        get
        {
            if (IsEmpty) return [];
            const int s = TileGeometry.Size;
            int tx0 = FloorDiv(X, s), ty0 = FloorDiv(Y, s);
            int tx1 = FloorDiv(MaxX - 1, s), ty1 = FloorDiv(MaxY - 1, s);
            var result = new TileCoord[(tx1 - tx0 + 1) * (ty1 - ty0 + 1)];
            int n = 0;
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++) result[n++] = new TileCoord(tx, ty);
            return result;
        }
    }

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>The four numbers only (a record would also list <see cref="TileCoords"/>, which allocates).</summary>
    public override string ToString() => $"PixelRect {{ X = {X}, Y = {Y}, Width = {Width}, Height = {Height} }}";
}

/// <summary>
/// One 256×256 block of premultiplied, linear, extended-sRGB pixels: either one colour for every pixel (no pixel
/// memory) or 256 × 256 RGBA <see cref="Half"/> values, row-major, the same bytes as the Mac's tile textures.
/// </summary>
/// <remarks>
/// A tile is never written after it is made (copy-on-write), and each has an <see cref="Id"/> that is never reused,
/// so identity comparison is a valid change test for caches, undo and incremental saves. A solid tile's identity is
/// its colour (two solid tiles of one colour are the same content); a pixel tile's identity is its id.
/// </remarks>
public sealed class Tile
{
    private static long nextId;

    private readonly Half[]? pixels;

    /// <summary>Never reused within a process.</summary>
    public long Id { get; }

    /// <summary>Every pixel has <see cref="SolidColor"/>; there are no pixel values.</summary>
    public bool IsSolid => pixels is null;

    /// <summary>The colour of a solid tile (premultiplied linear); zero for a pixel tile.</summary>
    public Vector4 SolidColor { get; }

    /// <summary>The 256 × 256 RGBA values of a pixel tile; empty for a solid tile.</summary>
    public ReadOnlyMemory<Half> Pixels => pixels;

    internal ReadOnlySpan<Half> PixelSpan => pixels;

    private Tile(Vector4 color)
    {
        Id = Interlocked.Increment(ref nextId);
        SolidColor = color;
    }

    private Tile(Half[] pixels)
    {
        Id = Interlocked.Increment(ref nextId);
        this.pixels = pixels;
    }

    /// <summary>A tile whose every pixel is <paramref name="color"/> (premultiplied linear). Costs no pixel memory.</summary>
    public static Tile Solid(Vector4 color) => new(color);

    /// <summary>
    /// A pixel tile around <paramref name="pixels"/> (256 × 256 RGBA), which it takes over: the caller must not change
    /// the array afterwards. Not classified: a uniform or transparent buffer stays a pixel tile (as tiles read from a
    /// file do on the Mac). Use <see cref="Make(ReadOnlySpan{Half})"/> to classify.
    /// </summary>
    public static Tile FromHalf(Half[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != TileGeometry.FloatCount)
            throw new ArgumentException($"A tile has {TileGeometry.FloatCount} values, not {pixels.Length}.", nameof(pixels));
        return new Tile(pixels);
    }

    /// <summary>
    /// Builds a tile from 256×256 Half RGBA pixels. Returns null when fully transparent and a solid tile when uniform:
    /// transparent means no pixel has an alpha other than ±0; uniform means all 8-byte pixels are identical.
    /// </summary>
    public static Tile? Make(ReadOnlySpan<Half> pixels)
    {
        CheckLength(pixels.Length);
        return Classify(pixels, out var solid) switch
        {
            Kind.Transparent => null,
            Kind.Uniform => new Tile(solid),
            _ => new Tile(pixels[..TileGeometry.FloatCount].ToArray()),
        };
    }

    /// <summary>
    /// Builds a tile from 256×256 float RGBA pixels: converted to Half first (round to nearest), then classified as
    /// <see cref="Make(ReadOnlySpan{Half})"/> does.
    /// </summary>
    public static Tile? Make(ReadOnlySpan<float> pixels)
    {
        CheckLength(pixels.Length);
        var buffer = new Half[TileGeometry.FloatCount];
        TensorPrimitives.ConvertToHalf(pixels[..TileGeometry.FloatCount], buffer);
        return MakeOwned(buffer);
    }

    /// <summary>Classifies <paramref name="buffer"/> and keeps it (no copy) when the tile has varied pixels.</summary>
    internal static Tile? MakeOwned(Half[] buffer) => Classify(buffer, out var solid) switch
    {
        Kind.Transparent => null,
        Kind.Uniform => new Tile(solid),
        _ => new Tile(buffer),
    };

    private static void CheckLength(int length)
    {
        if (length < TileGeometry.FloatCount)
            throw new ArgumentException($"A tile has {TileGeometry.FloatCount} values, not {length}.");
    }

    private enum Kind { Transparent, Uniform, Varied }

    private const ulong AlphaBitsWithoutSign = 0x7FFF_0000_0000_0000UL;

    /// <summary>
    /// The Mac's rule, bit for bit: alpha is the top 16 bits of a little-endian RGBA pixel and its sign bit is ignored.
    /// </summary>
    private static Kind Classify(ReadOnlySpan<Half> halves, out Vector4 solid)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Tiles are little-endian.");
        var px = MemoryMarshal.Cast<Half, ulong>(halves[..TileGeometry.FloatCount]);
        ulong first = px[0];
        bool uniform = px.IndexOfAnyExcept(first) < 0;
        bool anyVisible = uniform ? (first & AlphaBitsWithoutSign) != 0 : AnyVisible(px);
        solid = default;
        if (!anyVisible) return Kind.Transparent;
        if (!uniform) return Kind.Varied;
        solid = new Vector4((float)halves[0], (float)halves[1], (float)halves[2], (float)halves[3]);
        return Kind.Uniform;
    }

    private static bool AnyVisible(ReadOnlySpan<ulong> px)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && px.Length >= Vector<ulong>.Count)
        {
            var mask = new Vector<ulong>(AlphaBitsWithoutSign);
            var vectors = MemoryMarshal.Cast<ulong, Vector<ulong>>(px);
            var acc = Vector<ulong>.Zero;
            for (int v = 0; v < vectors.Length; v++)
            {
                acc |= vectors[v] & mask;
                if ((v & 63) == 63 && acc != Vector<ulong>.Zero) return true;
            }
            if (acc != Vector<ulong>.Zero) return true;
            i = vectors.Length * Vector<ulong>.Count;
        }
        for (; i < px.Length; i++)
            if ((px[i] & AlphaBitsWithoutSign) != 0) return true;
        return false;
    }

    /// <summary>
    /// Writes the 256 × 256 RGBA values as floats (<see cref="TileGeometry.FloatCount"/> of them). A solid tile writes
    /// its colour exactly.
    /// </summary>
    public void CopyTo(Span<float> destination)
    {
        CheckLength(destination.Length);
        var d = destination[..TileGeometry.FloatCount];
        if (pixels is null) MemoryMarshal.Cast<float, Vector4>(d).Fill(SolidColor);
        else TensorPrimitives.ConvertToSingle(pixels, d);
    }

    /// <summary>
    /// Writes the 256 × 256 RGBA values as Half (Swift's <c>pixels()</c>). A solid tile's colour is rounded to Half,
    /// as on the Mac.
    /// </summary>
    public void CopyTo(Span<Half> destination)
    {
        CheckLength(destination.Length);
        var d = destination[..TileGeometry.FloatCount];
        if (pixels is not null)
        {
            pixels.CopyTo(d);
            return;
        }
        Span<Half> one = [(Half)SolidColor.X, (Half)SolidColor.Y, (Half)SolidColor.Z, (Half)SolidColor.W];
        ulong pattern = MemoryMarshal.Cast<Half, ulong>(one)[0];
        MemoryMarshal.Cast<Half, ulong>(d).Fill(pattern);
    }

    /// <summary>Raw Half RGBA pixels, row-major, 256×256 (Swift's <c>pixels()</c>).</summary>
    public Half[] ToHalfArray()
    {
        var result = new Half[TileGeometry.FloatCount];
        CopyTo(result.AsSpan());
        return result;
    }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) inside the tile, as floats.</summary>
    public Vector4 PixelAt(int x, int y)
    {
        if ((uint)x >= TileGeometry.Size || (uint)y >= TileGeometry.Size) throw new ArgumentOutOfRangeException(x < 0 || x >= TileGeometry.Size ? nameof(x) : nameof(y));
        if (pixels is null) return SolidColor;
        int i = (y * TileGeometry.Size + x) * 4;
        return new Vector4((float)pixels[i], (float)pixels[i + 1], (float)pixels[i + 2], (float)pixels[i + 3]);
    }

    /// <summary>A solid tile of colour zero.</summary>
    internal bool IsTransparentSolid => pixels is null && SolidColor == Vector4.Zero;

    /// <summary>Whether two tiles have the same identity: the same pixel tile, or solid tiles of the same colour bits.</summary>
    public static bool SameIdentity(Tile? a, Tile? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || !a.IsSolid || !b.IsSolid) return false;
        return ColorBits(a.SolidColor) == ColorBits(b.SolidColor);
    }

    internal static (int, int, int, int) ColorBits(Vector4 c) =>
        (BitConverter.SingleToInt32Bits(c.X), BitConverter.SingleToInt32Bits(c.Y),
         BitConverter.SingleToInt32Bits(c.Z), BitConverter.SingleToInt32Bits(c.W));

    /// <summary>Feeds the tile's identity (not its pixels) into a running hash (Swift's <c>combineIdentity(into:)</c>).</summary>
    internal ulong CombineIdentity(ulong hash)
    {
        if (pixels is null)
        {
            var (x, y, z, w) = ColorBits(SolidColor);
            hash = Mix(hash, 0);
            hash = Mix(hash, (uint)x);
            hash = Mix(hash, (uint)y);
            hash = Mix(hash, (uint)z);
            return Mix(hash, (uint)w);
        }
        hash = Mix(hash, 1);
        return Mix(hash, (ulong)Id);
    }

    /// <summary>A strong 64-bit mix (the splitmix64 finalizer) of a running hash and a value.</summary>
    internal static ulong Mix(ulong hash, ulong value)
    {
        ulong z = hash ^ (value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// A sparse grid of tiles. Missing coordinates are fully transparent. Immutable: changes return a new grid that
/// shares the unchanged tiles (Swift's value semantics).
/// </summary>
/// <remarks>
/// Two grids are equal when they hold the same coordinates with tiles of the same identity (see
/// <see cref="Tile.SameIdentity"/>), which is what <see cref="ContentSignature"/> hashes.
/// </remarks>
public sealed partial class TileGrid : IEnumerable<KeyValuePair<TileCoord, Tile>>, IEquatable<TileGrid>
{
    public static TileGrid Empty { get; } = new(ImmutableDictionary<TileCoord, Tile>.Empty);

    private readonly ImmutableDictionary<TileCoord, Tile> tiles;
    private long signature;
    private int hasSignature;

    public TileGrid(ImmutableDictionary<TileCoord, Tile> tiles) => this.tiles = tiles;

    public TileGrid(IEnumerable<KeyValuePair<TileCoord, Tile>> tiles) => this.tiles = ImmutableDictionary.CreateRange(tiles);

    public TileGrid() : this(ImmutableDictionary<TileCoord, Tile>.Empty) { }

    /// <summary>The stored tiles by coordinate (Swift's <c>tiles</c>).</summary>
    public ImmutableDictionary<TileCoord, Tile> Tiles => tiles;

    /// <summary>The tile at <paramref name="coord"/>, or null where the grid is transparent.</summary>
    public Tile? this[TileCoord coord] => tiles.TryGetValue(coord, out var tile) ? tile : null;

    public bool TryGetValue(TileCoord coord, out Tile tile) => tiles.TryGetValue(coord, out tile!);

    public bool ContainsKey(TileCoord coord) => tiles.ContainsKey(coord);

    /// <summary>A grid with <paramref name="tile"/> at <paramref name="coord"/>; null removes the tile.</summary>
    public TileGrid SetItem(TileCoord coord, Tile? tile)
    {
        if (tile is null) return tiles.ContainsKey(coord) ? new TileGrid(tiles.Remove(coord)) : this;
        return new TileGrid(tiles.SetItem(coord, tile));
    }

    public TileGrid Remove(TileCoord coord) => SetItem(coord, null);

    public bool IsEmpty => tiles.IsEmpty;
    public int Count => tiles.Count;
    public IEnumerable<TileCoord> Coords => tiles.Keys;

    public Builder ToBuilder() => new(tiles.ToBuilder());

    /// <summary>Gathers many changes before making a new grid.</summary>
    public sealed class Builder
    {
        private readonly ImmutableDictionary<TileCoord, Tile>.Builder builder;

        internal Builder(ImmutableDictionary<TileCoord, Tile>.Builder builder) => this.builder = builder;

        public Builder() : this(ImmutableDictionary.CreateBuilder<TileCoord, Tile>()) { }

        /// <summary>The tile at <paramref name="coord"/>; setting null removes it.</summary>
        public Tile? this[TileCoord coord]
        {
            get => builder.TryGetValue(coord, out var tile) ? tile : null;
            set
            {
                if (value is null) builder.Remove(coord);
                else builder[coord] = value;
            }
        }

        public bool Remove(TileCoord coord) => builder.Remove(coord);
        public int Count => builder.Count;
        public IEnumerable<TileCoord> Coords => builder.Keys;
        public TileGrid ToImmutable() => new(builder.ToImmutable());
    }

    /// <summary>A grid filled with one colour over every tile touching <paramref name="covering"/>.</summary>
    public static TileGrid Solid(Vector4 color, PixelRect covering)
    {
        var tile = Tile.Solid(color);
        var b = ImmutableDictionary.CreateBuilder<TileCoord, Tile>();
        foreach (var coord in covering.TileCoords) b[coord] = tile;
        return new TileGrid(b.ToImmutable());
    }

    /// <summary>Bounding rect of all stored tiles (tile-aligned), null when empty.</summary>
    public PixelRect? TileBounds
    {
        get
        {
            if (tiles.IsEmpty) return null;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in tiles.Keys)
            {
                minX = Math.Min(minX, c.X); minY = Math.Min(minY, c.Y);
                maxX = Math.Max(maxX, c.X); maxY = Math.Max(maxY, c.Y);
            }
            const int s = TileGeometry.Size;
            return new PixelRect(minX * s, minY * s, (maxX - minX + 1) * s, (maxY - minY + 1) * s);
        }
    }

    /// <summary>
    /// Hash of the tile identities: the sorted coordinates and, per tile, its colour bits (solid) or id (pixels).
    /// Equal grids hash equal; any edit changes it. Deterministic within a process; computed once per grid.
    /// </summary>
    public long ContentSignature
    {
        get
        {
            if (Volatile.Read(ref hasSignature) != 0) return Interlocked.Read(ref signature);
            ulong hash = 0x6A09E667F3BCC908UL;
            foreach (var coord in tiles.Keys.OrderBy(c => c))
            {
                hash = Tile.Mix(hash, (uint)coord.X);
                hash = Tile.Mix(hash, (uint)coord.Y);
                hash = tiles[coord].CombineIdentity(hash);
            }
            hash = Tile.Mix(hash, (ulong)tiles.Count);
            Interlocked.Exchange(ref signature, (long)hash);
            Volatile.Write(ref hasSignature, 1);
            return (long)hash;
        }
    }

    public IEnumerator<KeyValuePair<TileCoord, Tile>> GetEnumerator() => tiles.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(TileGrid? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other.Count != Count) return false;
        if (Volatile.Read(ref hasSignature) != 0 && Volatile.Read(ref other.hasSignature) != 0 && ContentSignature != other.ContentSignature)
            return false;
        foreach (var (coord, tile) in tiles)
        {
            if (!other.tiles.TryGetValue(coord, out var o) || !Tile.SameIdentity(tile, o)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is TileGrid g && Equals(g);
    public override int GetHashCode() => (int)ContentSignature ^ (int)(ContentSignature >> 32);
}

/// <summary>
/// Whether an allocation has failed since it was last asked. The app asks after each edit and says that the edit may
/// be incomplete and can be undone.
/// </summary>
public static class GraphicsMemory
{
    private static int shortages;

    public static void NoteShortage() => Interlocked.Increment(ref shortages);

    /// <summary>True once per shortage (or run of them): reading it clears it.</summary>
    public static bool TakeShortage() => Interlocked.Exchange(ref shortages, 0) > 0;
}

/// <summary>
/// The computer has no memory left for another piece of an image. Measured on the Mac on 2026-11-16 (16 GB, Apple
/// silicon): with 52 full layers and 7.6 GB in use the system swapped and no allocation failed, so this is not
/// expected in practice. If it does happen, the edit is refused with this message; the app never ends because of it.
/// </summary>
public sealed class GraphicsMemoryException : Exception
{
    public GraphicsMemoryException()
        : base("Glimmer ran out of memory for this. Close other documents or merge some layers, then try again.") { }

    public GraphicsMemoryException(Exception inner)
        : base("Glimmer ran out of memory for this. Close other documents or merge some layers, then try again.", inner) { }
}
