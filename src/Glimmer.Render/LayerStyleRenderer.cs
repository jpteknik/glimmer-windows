// Ported from Packages/GlimmerKit/Sources/GlimmerRender/LayerStyleRenderer.swift
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>A rendered effect: premultiplied colour tiles composited with <see cref="Mode"/>.</summary>
internal readonly record struct EffectGrid(TileGrid Grid, BlendMode Mode);

/// <summary>Mirrors <c>EffectColorUniforms</c> in Composite.metal.</summary>
internal struct EffectColorUniforms
{
    /// <summary>Linear straight rgb, a = opacity.</summary>
    public Vector4 Color;
    /// <summary>Width, position (0 outside, 1 inside, 2 center), mode (0 coverage, 1 stroke, 2 inner).</summary>
    public Vector4 Stroke;
}

/// <summary>
/// Renders layer styles (shadows, glow, stroke) from a layer's masked alpha. These need pixels around each tile, so they
/// work on an assembled region.
/// </summary>
internal sealed class LayerStyleRenderer
{
    internal sealed record Rendered(EffectGrid[] Below, EffectGrid[] Above)
    {
        public static Rendered Empty { get; } = new([], []);
    }

    public Rendered Render(TileGrid pixels, ResolvedMask? mask, LayerEffects effects, PixelRect limit)
    {
        if (!effects.HasSpatialEffects || pixels.TileBounds is not { } content) return Rendered.Empty;
        var region = RegionOps.TileAligned(RegionOps.Expand(content, effects.Reach).Intersection(limit));
        if (region.IsEmpty || region.Width > RegionOps.MaxSide || region.Height > RegionOps.MaxSide) return Rendered.Empty;

        var alpha = FloatImage.From(pixels, region, 0, channel: 3);
        var maskImage = FloatImage.From(mask?.Tiles ?? TileGrid.Empty, region, mask?.DefaultValue ?? 1, channel: 3);
        // Effects follow the mask's shape; mask density is not applied to them.

        var below = new List<EffectGrid>();
        var above = new List<EffectGrid>();
        TileGrid Colorize(FloatImage a, FloatImage? b, SRGBColor color, double opacity, Vector4 stroke = default)
        {
            var u = new EffectColorUniforms { Color = new Vector4(color.Linear, (float)opacity), Stroke = stroke };
            return ColorizeTiles(a, b, alpha, maskImage, u, region);
        }

        if (effects.DropShadow is { IsEnabled: true } s)
        {
            var a = Blurred(Alpha(alpha, maskImage, ToVector(s.Offset), s.Spread, invert: false), s.Size);
            below.Add(new EffectGrid(Colorize(a, null, s.Color, s.Opacity), s.BlendMode));
        }
        if (effects.OuterGlow is { IsEnabled: true } g)
        {
            var a = Blurred(Alpha(alpha, maskImage, Vector2.Zero, g.Spread, invert: false), g.Size);
            below.Add(new EffectGrid(Colorize(a, null, g.Color, g.Opacity), g.BlendMode));
        }
        if (effects.InnerShadow is { IsEnabled: true } i)
        {
            var a = Blurred(Alpha(alpha, maskImage, ToVector(i.Offset), i.Spread, invert: true), i.Size);
            above.Add(new EffectGrid(Colorize(a, null, i.Color, i.Opacity, new Vector4(0, 0, 2, 0)), i.BlendMode));
        }
        if (effects.Stroke is { IsEnabled: true } st && st.Width > 0)
        {
            float position = st.Position switch
            {
                StrokePosition.Outside => 0,
                StrokePosition.Inside => 1,
                _ => 2,
            };
            var grid = Colorize(Distance(alpha, maskImage, invert: false), Distance(alpha, maskImage, invert: true), st.Color,
                                st.Opacity, new Vector4((float)st.Width, position, 1, 0));
            above.Add(new EffectGrid(grid, st.BlendMode));
        }
        return new Rendered([.. below], [.. above]);
    }

    private static Vector2 ToVector(Vec2 v) => new((float)v.X, (float)v.Y);

    /// <summary><c>effect_alpha</c>: effective alpha (alpha × mask), shifted by the rounded offset, spread, optionally inverted.</summary>
    internal static FloatImage Alpha(FloatImage alpha, FloatImage mask, Vector2 offset, double spread, bool invert)
    {
        var output = new FloatImage(alpha.Width, alpha.Height);
        CompositeShaders.EffectAlpha(alpha.Pixels, mask.Pixels, alpha.Width, alpha.Height,
                                     new Vector4(offset.X, offset.Y, (float)spread, invert ? 1 : 0), output.Pixels);
        return output;
    }

    /// <summary>A Gaussian with σ = size / 2; sizes up to half a pixel leave the field as it is.</summary>
    internal static FloatImage Blurred(FloatImage field, double size)
    {
        if (!(size > 0.5)) return field;
        var output = new FloatImage(field.Width, field.Height);
        GaussianBlur.Blur(field, output, (float)(size / 2));
        return output;
    }

    /// <summary>
    /// <c>effect_binary</c> followed by the distance transform: distance to the shape (invert false) or to its outside
    /// (invert true).
    /// </summary>
    internal static FloatImage Distance(FloatImage alpha, FloatImage mask, bool invert)
    {
        var binary = new float[alpha.Width * alpha.Height];
        CompositeShaders.EffectBinary(alpha.Pixels, mask.Pixels, invert, binary);
        var output = new FloatImage(alpha.Width, alpha.Height);
        DistanceTransform.Compute(binary, alpha.Width, alpha.Height, output.Pixels);
        return output;
    }

    /// <summary><c>effect_colorize</c> over the region, split into tiles (fully transparent ones are left out).</summary>
    internal static TileGrid ColorizeTiles(FloatImage field, FloatImage? field2, FloatImage alpha, FloatImage mask,
                                           EffectColorUniforms u, PixelRect region)
    {
        var second = field2 ?? field;
        var coords = region.TileCoords;
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, t =>
        {
            const int size = TileGeometry.Size;
            var rect = coords[t].Rect;
            var buffer = new Half[TileGeometry.FloatCount];
            var row = new Vector4[size];
            for (int y = 0; y < size; y++)
            {
                int i0 = (rect.Y - region.Y + y) * field.Width + (rect.X - region.X);
                for (int x = 0; x < size; x++)
                {
                    int i = i0 + x;
                    row[x] = CompositeShaders.EffectColorize(field.Pixels[i], second.Pixels[i], alpha.Pixels[i], mask.Pixels[i], u);
                }
                TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(row.AsSpan()), buffer.AsSpan(y * size * 4, size * 4));
            }
            made[t] = Tile.MakeOwned(buffer);
        });
        var b = new TileGrid.Builder();
        for (int t = 0; t < coords.Length; t++)
            if (made[t] is { } tile) b[coords[t]] = tile;
        return b.ToImmutable();
    }
}

/// <summary>Non-destructive mask feather: a Gaussian blur of the mask's stored tiles.</summary>
internal sealed class MaskFeatherRenderer
{
    public TileGrid Feather(LayerMask mask, PixelRect limit)
    {
        if (!(mask.Feather > 0.25) || mask.Tiles.TileBounds is not { } stored) return mask.Tiles;
        int reach = (int)Math.Ceiling(mask.Feather * 3);
        var region = RegionOps.TileAligned(RegionOps.Expand(stored, reach).Intersection(limit));
        if (region.IsEmpty) return mask.Tiles;
        RegionOps.CheckSize(region);
        var source = FloatImage.From(mask.Tiles, region, mask.DefaultValue, channel: 3);
        var output = new FloatImage(region.Width, region.Height);
        GaussianBlur.Blur(source, output, (float)mask.Feather);
        // Only the processed region (as the Mac's split): tiles equal to the default are left out, which reads the same.
        return TileGrid.Empty.WriteChannel(region, output.Pixels, mask.DefaultValue);
    }
}

/// <summary>Renders a smart object's source through its transform into document tiles.</summary>
public sealed class SmartObjectRenderer
{
    private readonly Dictionary<long, MipChain> sources = [];
    private readonly Lock gate = new();

    /// <summary>For tools (free transform previews and commits).</summary>
    public SmartObjectRenderer() { }

    public TileGrid Render(SmartObject smart, PixelRect limit)
    {
        var region = RegionOps.TileAligned(smart.Bounds.Intersection(limit));
        if (region.IsEmpty || smart.Width <= 0 || smart.Height <= 0) return TileGrid.Empty;

        long key = smart.Source.ContentSignature;
        MipChain? source;
        lock (gate)
        {
            if (!sources.TryGetValue(key, out source))
            {
                if (sources.Count > 8) sources.Clear();
                var rect = new PixelRect(0, 0, smart.Width, smart.Height);
                RegionOps.CheckSize(rect);
                var level0 = RegionImage.From(smart.Source, rect);
                source = MipChain.Build(level0.Pixels, smart.Width, smart.Height);
                sources[key] = source;
            }
        }

        // The transform includes the projective row for perspective; each output pixel maps back to the source.
        var docToSource = Projective.From(smart.Transform).Inverse();
        var coords = region.TileCoords;
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, t =>
        {
            const int size = TileGeometry.Size;
            var rect = coords[t].Rect;
            var buffer = new Half[TileGeometry.FloatCount];
            var row = new Vector4[size];
            for (int y = 0; y < size; y++)
            {
                double py = rect.Y + y + 0.5;
                for (int x = 0; x < size; x++)
                    row[x] = SmartSampling.Sample(source, docToSource, rect.X + x + 0.5, py);
                TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(row.AsSpan()), buffer.AsSpan(y * size * 4, size * 4));
            }
            made[t] = Tile.MakeOwned(buffer);
        });
        var b = new TileGrid.Builder();
        for (int t = 0; t < coords.Length; t++)
            if (made[t] is { } tile) b[coords[t]] = tile;
        return b.ToImmutable();
    }
}
