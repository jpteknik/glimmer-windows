// Ported from Packages/GlimmerKit/Sources/GlimmerRender/LensBlur.swift
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Mirrors <c>LensBlurUniforms</c> in Composite.metal.</summary>
internal struct LensBlurUniforms
{
    /// <summary>Depth to keep sharp, 0 (far) ... 1 (near).</summary>
    public float Focus;
    /// <summary>Pixels.</summary>
    public float MaxRadius;
    /// <summary>Highlight boost 0...1.</summary>
    public float Bokeh;
    /// <summary>Depth range that stays sharp.</summary>
    public float Size;
}

/// <summary>
/// Whole-document effects that produce new pixels: depth lens blur and high-quality upscaling. Work in linear float, so
/// no 8-bit round trips.
/// </summary>
public static class DocumentEffects
{
    /// <param name="Focus">0 = far … 1 = near.</param>
    /// <param name="Amount">Fraction of the long side used as the maximum blur radius.</param>
    /// <param name="Bokeh">Highlight boost for bokeh shapes, 0...1.</param>
    /// <param name="FocusRange">Depth range that stays sharp, 0...1.</param>
    public sealed record LensBlurParameters(double Focus = 0.8, double Amount = 0.02, double Bokeh = 0.4, double FocusRange = 0.1);

    /// <summary>The flattened document as a linear premultiplied image (document size).</summary>
    internal static RegionImage CompositeImage(DocumentState state)
    {
        var compositor = new DocumentCompositor();
        var (buffer, _, rect) = compositor.RenderPixels(state);
        var image = new RegionImage(rect.Width, rect.Height);
        TensorPrimitives.ConvertToSingle(buffer, image.Floats);
        return image;
    }

    /// <summary>Depth-driven lens blur of the whole document. <paramref name="depth"/> is document-sized, 0...1, near = 1.</summary>
    public static TileGrid LensBlur(DocumentState state, float[] depth, LensBlurParameters parameters)
    {
        int w = state.Width, h = state.Height;
        if (depth.LongLength < (long)w * h) throw new ArgumentException("The depth map is smaller than the document.", nameof(depth));
        var color = CompositeImage(state);
        var depthImage = new FloatImage(w, h, depth);
        var u = new LensBlurUniforms
        {
            Focus = (float)parameters.Focus, MaxRadius = (float)(parameters.Amount * Math.Max(w, h)),
            Bokeh = (float)parameters.Bokeh, Size = (float)parameters.FocusRange,
        };
        var output = new RegionImage(w, h);
        Parallel.For(0, h, y =>
        {
            var row = output.Row(y);
            for (int x = 0; x < w; x++) row[x] = CompositeShaders.LensBlur(color, depthImage, x, y, u);
        });
        return SplitPartial(output);
    }

    /// <summary>Lanczos-upscaled document composite (<paramref name="scale"/>×), as tiles of the new size.</summary>
    public static TileGrid Upscale(DocumentState state, int scale)
    {
        var color = CompositeImage(state);
        // Edges clamp (the Mac sets that explicitly: the default fades the last rows and columns).
        var output = LanczosScale.Scale(color, state.Width * scale, state.Height * scale);
        return SplitPartial(output);
    }

    /// <summary>
    /// Splits an image with its top-left at the document origin into tiles; edge tiles are transparent beyond it. Fully
    /// transparent tiles are left out.
    /// </summary>
    internal static TileGrid SplitPartial(RegionImage image)
    {
        var bounds = new PixelRect(0, 0, image.Width, image.Height);
        var coords = bounds.TileCoords;
        var made = new Tile?[coords.Length];
        Parallel.For(0, coords.Length, i =>
        {
            const int s = TileGeometry.Size;
            var part = coords[i].Rect.Intersection(bounds);
            var buffer = new Half[TileGeometry.FloatCount];
            for (int y = part.Y; y < part.MaxY; y++)
            {
                var src = image.Pixels.AsSpan(y * image.Width + part.X, part.Width);
                var dst = buffer.AsSpan(((y - coords[i].Rect.Y) * s + (part.X - coords[i].Rect.X)) * 4, part.Width * 4);
                TensorPrimitives.ConvertToHalf(MemoryMarshal.Cast<Vector4, float>(src), dst);
            }
            made[i] = Tile.MakeOwned(buffer);
        });
        var b = new TileGrid.Builder();
        for (int i = 0; i < coords.Length; i++)
            if (made[i] is { } tile) b[coords[i]] = tile;
        return b.ToImmutable();
    }
}
