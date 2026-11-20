// Ported from Packages/GlimmerKit/Sources/GlimmerRender/TilePipelines.swift
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>LayerUniforms</c> in Composite.metal.</summary>
internal struct LayerUniforms
{
    /// <summary>Colour overlay: linear straight rgb, a = opacity (0 = none).</summary>
    public Vector4 Overlay;
    public Vector4 PatternForeground;
    public Vector4 PatternBackground;
    /// <summary>Document pixels.</summary>
    public Vector2 GradientStart;
    public Vector2 GradientEnd;
    /// <summary>Document pixel of the output tile's top-left.</summary>
    public Vector2 TileOrigin;
    public int Mode;
    public float Opacity;
    public int GammaSpace;
    /// <summary>0 texture, 1 linear gradient, 2 radial gradient, 3 pattern.</summary>
    public int SourceKind;
    public float MaskDensity;
    public float ClipDensity;
    public int OverlayMode;
    public int PatternKind;
    public float PatternScale;
    public int GradientReverse;

    public LayerUniforms()
    {
        Opacity = 1;
        PatternScale = 1;
    }
}

/// <summary>Mirrors <c>AdjustUniforms</c> in Composite.metal: <c>params[7]</c> as 28 floats.</summary>
internal struct AdjustUniforms
{
    public AdjustParameters Params;
    /// <summary>AdjustmentKind.ShaderIndex.</summary>
    public int Kind;
    public int Mode;
    public float Opacity;
    public int GammaSpace;
    public float MaskDensity;
    public float ClipDensity;

    public AdjustUniforms()
    {
        Opacity = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float P(int i) => Params[i];

    /// <summary>Pads <paramref name="values"/> with zeros to 28, as <c>setParameters</c> does.</summary>
    public void SetParameters(ReadOnlySpan<float> values)
    {
        for (int i = 0; i < AdjustParameters.Count; i++) Params[i] = i < values.Length ? values[i] : 0;
    }
}

[InlineArray(Count)]
internal struct AdjustParameters
{
    public const int Count = 28;
    private float element0;
}

/// <summary>
/// The shared resources of tile rendering. On the Mac this holds the pipeline states and the 1×1 solid and lookup
/// textures; on the CPU only the lookup tables remain worth sharing.
/// </summary>
internal static class TilePipelines
{
    private static readonly ConcurrentDictionary<object, Vector4[]> Luts = new();

    /// <summary>A lookup table cached by <paramref name="key"/> (compared by value), made once.</summary>
    public static Vector4[] Lut(object key, Func<Vector4[]> make)
    {
        if (Luts.TryGetValue(key, out var hit)) return hit;
        if (Luts.Count > 256) Luts.Clear();
        return Luts.GetOrAdd(key, _ => make());
    }

    /// <summary>The table of an adjustment (LUT-based kinds), keyed by the adjustment's value.</summary>
    public static Vector4[] Lut(Adjustment adjustment) => Lut(new AdjustmentKey(adjustment), () => adjustment.LookupTable());

    /// <summary>A gradient's 1024 straight sRGB colours with opacity, keyed by its stops.</summary>
    public static Vector4[] GradientLut(ImmutableArray<GradientStop> stops) =>
        Lut(new StopsKey(stops), () => DocumentCompositor.GradientTable(stops));

    /// <summary>Adjustment equality with a hash that covers every field (the record's own hash skips curve points).</summary>
    private sealed class AdjustmentKey(Adjustment adjustment) : IEquatable<AdjustmentKey>
    {
        private readonly Adjustment adjustment = adjustment;
        private readonly ulong hash = ContentHash.Of(adjustment);
        public bool Equals(AdjustmentKey? other) => other is not null && hash == other.hash && adjustment.Equals(other.adjustment);
        public override bool Equals(object? obj) => obj is AdjustmentKey k && Equals(k);
        public override int GetHashCode() => (int)hash ^ (int)(hash >> 32);
    }

    private sealed class StopsKey(ImmutableArray<GradientStop> stops) : IEquatable<StopsKey>
    {
        private readonly ImmutableArray<GradientStop> stops = stops.IsDefault ? [] : stops;
        public bool Equals(StopsKey? other) => other is not null && stops.AsSpan().SequenceEqual(other.stops.AsSpan());
        public override bool Equals(object? obj) => obj is StopsKey k && Equals(k);
        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var s in stops) h.Add(s);
            return h.ToHashCode();
        }
    }
}

// MARK: Regions

/// <summary>Assembling tile grids into one region and splitting regions back into tiles.</summary>
internal static class RegionOps
{
    /// <summary>Regions are capped as Metal textures are (16384 on a side).</summary>
    public const int MaxSide = 16384;

    public static void CheckSize(PixelRect region)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Width > MaxSide || region.Height > MaxSide)
            throw RenderError.ResourceCreationFailed($"region {region.Width}×{region.Height} is too large");
    }

    /// <summary>Expands a rect outward to whole tiles.</summary>
    public static PixelRect TileAligned(PixelRect r)
    {
        const int s = TileGeometry.Size;
        int x0 = PixelRect.FloorDiv(r.X, s) * s, y0 = PixelRect.FloorDiv(r.Y, s) * s;
        int x1 = (PixelRect.FloorDiv(r.MaxX - 1, s) + 1) * s, y1 = (PixelRect.FloorDiv(r.MaxY - 1, s) + 1) * s;
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    public static PixelRect Expand(PixelRect r, int n) => new(r.X - n, r.Y - n, r.Width + 2 * n, r.Height + 2 * n);

    /// <summary>
    /// Splits <paramref name="image"/>, covering the tile-aligned <paramref name="region"/>, into tiles. Fully
    /// transparent tiles are left out, or kept as solid zero with <paramref name="keepTransparent"/> (the Mac keeps every
    /// tile it splits; that matters where a missing tile means something else, as for retouch sources).
    /// </summary>
    public static TileGrid Split(RegionImage image, PixelRect region, bool keepTransparent = false)
    {
        var coords = region.TileCoords;
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            const int s = TileGeometry.Size;
            var rect = coords[i].Rect;
            var buffer = new Half[TileGeometry.FloatCount];
            for (int y = 0; y < s; y++)
            {
                var src = image.Pixels.AsSpan((rect.Y - region.Y + y) * image.Width + (rect.X - region.X), s);
                System.Numerics.Tensors.TensorPrimitives.ConvertToHalf(
                    System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(src), buffer.AsSpan(y * s * 4, s * 4));
            }
            made[i] = Tile.MakeOwned(buffer) ?? (keepTransparent ? Tile.Solid(Vector4.Zero) : null);
        });
        var b = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++)
            if (made[i] is { } tile) b[coords[i]] = tile;
        return b.ToImmutable();
    }
}

/// <summary>
/// A 64-bit hash that covers every field of the values the compositor caches and signs by. The records' own
/// <c>GetHashCode</c> is fine for dictionaries, which also compare by value, but a signature is compared by its hash
/// alone, so it must change with any edit: <see cref="Adjustment"/>'s hash, for one, counts its curves without
/// looking at their points.
/// </summary>
internal static class ContentHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong hash, ulong value)
    {
        ulong z = hash ^ (value + 0x9E3779B97F4A7C15UL + (hash << 6) + (hash >> 2));
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong hash, double value) => Mix(hash, (ulong)BitConverter.DoubleToInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong hash, float value) => Mix(hash, (ulong)BitConverter.SingleToUInt32Bits(value));

    public static ulong Mix(ulong hash, string value)
    {
        hash = Mix(hash, (ulong)value.Length);
        foreach (char c in value) hash = Mix(hash, c);
        return hash;
    }

    public static ulong Mix(ulong hash, Vec2 v) => Mix(Mix(hash, v.X), v.Y);

    /// <summary>Every field of a fill layer's fill.</summary>
    public static ulong Of(Fill fill) => fill switch
    {
        Fill.Solid s => Mix(Mix(0x13198A2E03707344UL, 1UL), s.Color),
        Fill.Gradient { Value: var g } => Stops(Mix(Mix(Mix(Mix(Mix(0x13198A2E03707344UL, 2UL), (ulong)g.Style), g.Start), g.End), g.Reverse ? 1UL : 0UL), g.Stops),
        Fill.Pattern { Value: var p } => Mix(Mix(Mix(Mix(Mix(0x13198A2E03707344UL, 3UL), (ulong)p.Kind), p.Scale), p.Foreground), p.Background),
        _ => 0UL,
    };

    /// <summary>Every field of a colour overlay; a missing (or disabled, already filtered out) overlay hashes alike.</summary>
    public static ulong Of(ColorOverlayEffect? overlay) => overlay is null
        ? 0xA4093822299F31D0UL
        : Mix(Mix(Mix(Mix(0x082EFA98EC4E6C89UL, overlay.IsEnabled ? 1UL : 0UL), overlay.Color), overlay.Opacity), (ulong)overlay.BlendMode);

    private static ulong Stops(ulong h, ImmutableArray<GradientStop> stops)
    {
        h = Mix(h, (ulong)(stops.IsDefault ? 0 : stops.Length));
        foreach (var s in stops.IsDefault ? [] : stops) h = Mix(Mix(Mix(h, s.Position), s.Color), s.Opacity);
        return h;
    }

    public static ulong Mix(ulong hash, SRGBColor c) => Mix(Mix(Mix(hash, c.R), c.G), c.B);

    public static ulong Of(Adjustment a)
    {
        ulong h = Mix(0x243F6A8885A308D3UL, (ulong)a.Kind);
        foreach (var (k, v) in a.Numbers.OrderBy(p => p.Key, StringComparer.Ordinal)) h = Mix(Mix(h, k), v);
        h = Mix(h, 1UL);
        foreach (var (k, v) in a.Flags.OrderBy(p => p.Key, StringComparer.Ordinal)) h = Mix(Mix(h, k), v ? 1UL : 0UL);
        h = Mix(h, 2UL);
        foreach (var (k, v) in a.Colors.OrderBy(p => p.Key, StringComparer.Ordinal)) h = Mix(Mix(h, k), v);
        h = Mix(h, 3UL);
        foreach (var (channel, points) in a.Curves.OrderBy(p => p.Key))
        {
            h = Mix(h, (ulong)channel);
            foreach (var p in points.IsDefault ? [] : points) h = Mix(Mix(h, p.X), p.Y);
        }
        h = Mix(h, 4UL);
        foreach (var (channel, l) in a.Levels.OrderBy(p => p.Key))
            h = Mix(Mix(Mix(Mix(Mix(Mix(h, (ulong)channel), l.InBlack), l.InWhite), l.Gamma), l.OutBlack), l.OutWhite);
        h = Mix(h, 5UL);
        foreach (var s in a.Gradient.IsDefault ? [] : a.Gradient) h = Mix(Mix(Mix(h, s.Position), s.Color), s.Opacity);
        return h;
    }
}
