// Ported from Packages/GlimmerKit/Sources/GlimmerRender/PixelOps.swift
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>ApplyUniforms</c> in Composite.metal.</summary>
internal struct ApplyUniforms
{
    /// <summary>Linear, premultiplied (paint).</summary>
    public Vector4 Color;
    /// <summary>0 paint, 1 erase, 2 mask value, 3 keep selected, 4 toward effect, 5 dodge, 6 burn.</summary>
    public int Mode;
    public float Opacity;
    /// <summary>Value painted into masks.</summary>
    public float MaskTarget;
}

/// <summary>Pixel operations producing new tiles (originals are never written).</summary>
public sealed class PixelOps
{
    public enum Mode
    {
        /// <summary>Paint <c>color</c> with coverage × opacity.</summary>
        Paint = 0,
        /// <summary>Erase alpha with coverage × opacity.</summary>
        Erase = 1,
        /// <summary>Move a mask value toward <c>maskTarget</c>.</summary>
        Mask = 2,
        /// <summary>Keep only the selected part (multiply by the selection).</summary>
        KeepSelected = 3,
        /// <summary>Blend toward a precomputed effect (clone, heal, blur, sharpen tools).</summary>
        TowardEffect = 4,
        /// <summary>Dodge / burn: <c>color.x</c> exposure, <c>color.y</c> range (0 shadows, 1 midtones, 2 highlights).</summary>
        Dodge = 5,
        Burn = 6,
    }

    public PixelOps() { }

    /// <summary>
    /// Applies <paramref name="mode"/> on <paramref name="coords"/>, returning the new tiles (null = became empty).
    /// </summary>
    /// <param name="source">The tiles being edited; <paramref name="sourceDefault"/> fills missing ones (masks).</param>
    /// <param name="coverage">Per-tile coverage, 256 × 256 values (the R channel on the Mac); missing = full coverage
    /// when <paramref name="fullCoverage"/>.</param>
    internal Dictionary<TileCoord, Tile?> Encode(IReadOnlyList<TileCoord> coords, TileGrid source, float? sourceDefault,
                                                 Func<TileCoord, float[]?> coverage, bool fullCoverage, Selection? selection,
                                                 Mode mode, Vector4 color, float opacity, float maskTarget, TileGrid? effect = null)
    {
        var u = new ApplyUniforms { Color = color, Mode = (int)mode, Opacity = opacity, MaskTarget = maskTarget };
        var made = new Tile?[coords.Count];
        Parallel.For(0, coords.Count, i =>
        {
            var coord = coords[i];
            var original = source[coord] ?? Tile.Solid(new Vector4(sourceDefault ?? 0));
            made[i] = Apply(original, coverage(coord), fullCoverage, selection?.TileAt(coord), effect?[coord] ?? original, u,
                            sourceDefault);
        });
        var output = new Dictionary<TileCoord, Tile?>(coords.Count);
        for (int i = 0; i < coords.Count; i++) output[coords[i]] = made[i];
        return output;
    }

    /// <summary><c>pixel_apply_fragment</c> over one tile.</summary>
    private static Tile? Apply(Tile original, float[]? wet, bool fullCoverage, Tile? selection, Tile effect, in ApplyUniforms u,
                               float? sourceDefault)
    {
        const int size = TileGeometry.Size;
        var output = new Half[TileGeometry.FloatCount];
        var o = new Vector4[size];
        var e = new Vector4[size];
        var s = new float[size];
        var floats = new float[size * 4];
        var result = new Vector4[size];
        float missing = fullCoverage ? 1f : 0f;
        bool needsEffect = u.Mode == (int)Mode.TowardEffect;
        for (int y = 0; y < size; y++)
        {
            DocumentCompositor.ReadRow(original, y, 0, o, floats);
            if (needsEffect) DocumentCompositor.ReadRow(effect, y, 0, e, floats);
            if (selection is null) s.AsSpan().Fill(1f);
            else DocumentCompositor.ReadAlphaRow(selection, y, 0, s, floats);
            for (int x = 0; x < size; x++)
            {
                float w = wet is null ? missing : wet[y * size + x];
                result[x] = CompositeShaders.PixelApplyFragment(o[x], w, s[x], e[x], u);
            }
            TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(result.AsSpan()), output.AsSpan(y * size * 4, size * 4));
        }
        // A mask whose default isn't zero keeps a cleared tile as solid zero: leaving it out would read as the default.
        return Tile.MakeOwned(output) ?? (sourceDefault is { } d && d != 0 ? Tile.Solid(Vector4.Zero) : null);
    }

    /// <summary>One-shot operation over the selected area (or a region), e.g. clear, keep-selected, fill.</summary>
    /// <remarks>
    /// <paramref name="sourceDefault"/> follows <paramref name="coords"/> here (second in Swift) because C# optional
    /// parameters come last. <paramref name="coverage"/> holds coverage tiles (value in red), as
    /// <see cref="Coverage(Selection, IReadOnlyList{TileCoord})"/> makes.
    /// </remarks>
    public TileGrid Run(Mode mode, TileGrid source, Selection? selection, IReadOnlyList<TileCoord> coords, float? sourceDefault = null,
                        IReadOnlyDictionary<TileCoord, Tile>? coverage = null, bool fullCoverage = true, Vector4 color = default,
                        float opacity = 1, float maskTarget = 0)
    {
        var tiles = Encode(coords, source, sourceDefault,
                           c => coverage is not null && coverage.TryGetValue(c, out var t) ? RedChannel(t) : null,
                           fullCoverage, selection, mode, color, opacity, maskTarget);
        var b = source.ToBuilder();
        foreach (var (c, t) in tiles) b[c] = t;
        return b.ToImmutable();
    }

    /// <summary>Coverage tiles from a selection-like grid (e.g. a flood-fill mask), for <see cref="Run"/>.</summary>
    public IReadOnlyDictionary<TileCoord, Tile> Coverage(Selection selection, IReadOnlyList<TileCoord> coords)
    {
        var output = new Dictionary<TileCoord, Tile>(coords.Count);
        foreach (var c in coords) output[c] = selection.TileAt(c);
        return output;
    }

    private static float[] RedChannel(Tile tile)
    {
        var values = new float[TileGeometry.PixelCount];
        if (tile.IsSolid)
        {
            values.AsSpan().Fill(tile.SolidColor.X);
            return values;
        }
        var px = tile.PixelSpan;
        for (int i = 0; i < values.Length; i++) values[i] = (float)px[i * 4];
        return values;
    }
}
