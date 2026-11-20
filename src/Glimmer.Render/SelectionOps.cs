// Ported from Packages/GlimmerKit/Sources/GlimmerRender/SelectionOps.swift
using Glimmer.Core;

namespace Glimmer.Render;

/// <summary>Feather / grow / shrink, any radius (the web app capped these at 32 px).</summary>
public static class SelectionOps
{
    /// <summary>Swift's <c>enum Operation</c>.</summary>
    public abstract record Operation(double Radius)
    {
        public sealed record Feather(double Radius) : Operation(Radius);
        public sealed record Grow(double Radius) : Operation(Radius);
        public sealed record Shrink(double Radius) : Operation(Radius);
    }

    /// <exception cref="RenderError">The region to process is larger than a region may be.</exception>
    public static Selection Apply(Operation op, Selection selection, PixelRect limit)
    {
        double radius = op.Radius;
        if (!(radius > 0)) return selection;
        // Work over everything that can change: the stored tiles plus reach, within the document.
        var stored = selection.Tiles.TileBounds ?? limit;
        var region = RegionOps.TileAligned(RegionOps.Expand(selection.DefaultValue > 0 ? limit : stored, (int)Math.Ceiling(radius * 3))
                                                    .Intersection(RegionOps.Expand(limit, 256)));
        if (region.IsEmpty) return selection;
        RegionOps.CheckSize(region);
        // The selection value is in every channel; red is what the Mac's gray transform picks up.
        var source = FloatImage.From(selection.Tiles, region, selection.DefaultValue, channel: 0);
        var output = op switch
        {
            Operation.Feather => Feather(source, radius),
            Operation.Grow => GrowOrShrink(source, radius, grow: true),
            _ => GrowOrShrink(source, radius, grow: false),
        };
        // Tiles outside the processed region keep their values.
        return new Selection(selection.Tiles.WriteChannel(region, output.Pixels, selection.DefaultValue), selection.DefaultValue);
    }

    /// <summary>Feather: a Gaussian with σ = radius / 2, edges clamped.</summary>
    internal static FloatImage Feather(FloatImage selection, double radius)
    {
        var output = new FloatImage(selection.Width, selection.Height);
        GaussianBlur.Blur(selection, output, (float)(radius / 2));
        return output;
    }

    /// <summary>
    /// value = grow ? (d ≤ r) : (d &gt; r), with a 1 px anti-aliased edge: grow keeps pixels within
    /// <paramref name="radius"/> of the selection, shrink pixels farther than it from the unselected area.
    /// </summary>
    internal static FloatImage GrowOrShrink(FloatImage selection, double radius, bool grow)
    {
        int n = selection.Width * selection.Height;
        // Distance from the (thresholded) selection, or from its complement for shrink.
        var input = new float[n];
        for (int i = 0; i < n; i++)
        {
            bool on = selection.Pixels[i] > 0.5f;  // MPSImageThresholdBinary: value > threshold
            input[i] = grow ? (on ? 1f : 0f) : (on ? 0f : 1f);
        }
        var distance = new float[n];
        DistanceTransform.Compute(input, selection.Width, selection.Height, distance);
        var output = new FloatImage(selection.Width, selection.Height);
        float r = (float)radius;
        for (int i = 0; i < n; i++) output.Pixels[i] = CompositeShaders.SelectionDistanceRemap(distance[i], r, grow);
        return output;
    }
}
