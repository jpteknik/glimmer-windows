// Ported from App/EditorSession+Shapes.swift
using Glimmer.Core;

namespace Glimmer.Editor;

public static class ShapeKindExtensions
{
    public static string Title(this ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "Rectangle",
        ShapeKind.RoundedRectangle => "Rounded Rectangle",
        ShapeKind.Ellipse => "Ellipse",
        _ => "Line",
    };

    /// <summary>The Swift raw value (the case name).</summary>
    public static string RawValue(this ShapeKind kind) => kind switch
    {
        ShapeKind.Rectangle => "rectangle",
        ShapeKind.RoundedRectangle => "roundedRectangle",
        ShapeKind.Ellipse => "ellipse",
        _ => "line",
    };
}

public sealed partial class EditorSession
{
    // MARK: Shapes

    /// <summary>Outline of a rounded rectangle (corner arcs sampled finely enough for antialiasing).</summary>
    public static IReadOnlyList<Vec2> RoundedRectPolygon(CGRectLike r, double radius)
    {
        double rad = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height / 2)));
        if (!(rad > 0.5)) return [new(r.MinX, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, r.MaxY), new(r.MinX, r.MaxY)];
        int steps = Math.Max(8, Math.Clamp((int)(rad / 2), 8, 48));
        Vec2[] centres = [new(r.MaxX - rad, r.MinY + rad), new(r.MaxX - rad, r.MaxY - rad), new(r.MinX + rad, r.MaxY - rad), new(r.MinX + rad, r.MinY + rad)];
        var points = new List<Vec2>();
        for (int i = 0; i < centres.Length; i++)
        {
            double start = -Math.PI / 2 + i * Math.PI / 2;
            for (int k = 0; k <= steps; k++)
            {
                double a = start + k / (double)steps * Math.PI / 2;
                points.Add(centres[i] + new Vec2(Math.Cos(a), Math.Sin(a)) * rad);
            }
        }
        return points;
    }

    /// <summary>A shape becomes a solid colour fill layer with the shape as its mask, so it stays recolourable and editable.</summary>
    public void FinishShape()
    {
        if (shapeDrag is not { } drag) return;
        ShapeDrag = null;
        var r = new CGRectLike(drag.Start.X, drag.Start.Y, drag.End.X, drag.End.Y);
        VectorPath path;
        switch (shapeKind)
        {
            case ShapeKind.Rectangle:
                if (r.Width < 1 || r.Height < 1) return;
                path = VectorPath.Rectangle(r);
                break;
            case ShapeKind.RoundedRectangle:
                if (r.Width < 1 || r.Height < 1) return;
                path = VectorPath.RoundedRectangle(r, shapeCornerRadius);
                break;
            case ShapeKind.Ellipse:
                if (r.Width < 1 || r.Height < 1) return;
                path = VectorPath.Ellipse(r);
                break;
            default:
                {
                    var v = drag.End - drag.Start;
                    if (v.Length < 1) return;
                    var n = new Vec2(-v.Y, v.X) / v.Length * Math.Max(shapeLineWidth, 1) / 2;
                    path = new VectorPath("Line", [new Subpath(new[] { drag.Start + n, drag.End + n, drag.End - n, drag.Start - n }.Select(p => new PathAnchor(p)),
                                                               isClosed: true)]);
                    break;
                }
        }
        // A shape is a solid fill layer with a vector mask: recolourable, editable with the pen, scalable.
        var layer = new Layer(Environment.LayerName(shapeKind.Title()), new FillContent(new Fill.Solid(shapeFillColor ?? foreground)),
                              mask: path.AsSelection.AsMask, vectorMask: path);
        if (shapeFillColor is null && foregroundOpacity < 1) layer = layer with { Opacity = foregroundOpacity };
        if (shapeStrokeWidth >= 1)
        {
            // The outline is a layer style, so its colour and width stay editable.
            layer = layer with
            {
                Effects = layer.Effects with { Stroke = new StrokeEffect { Color = shapeStrokeColor, Width = shapeStrokeWidth, Position = StrokePosition.Center } },
            };
        }
        Edit(new UiText("New {0}", new UiText(shapeKind.Title())), s => s.InsertLayerAboveActive(layer));
    }
}
