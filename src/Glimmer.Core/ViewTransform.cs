// Ported from Packages/GlimmerKit/Sources/GlimmerCore/ViewTransform.swift

namespace Glimmer.Core;

/// <summary>
/// Maps between document pixels and view points (both y-down).
/// <c>view = translation + R(rotation) · (scale · document)</c>
/// </summary>
/// <remarks>
/// The transform only affects presentation; compositing always happens in document space. Immutable: the Mac's
/// mutating functions return the changed transform here (<c>t = t.Zoom(2, anchor)</c>).
/// </remarks>
public sealed record ViewTransform
{
    public const double MinScale = 0.02;
    public const double MaxScale = 64.0;

    public ViewTransform(double scale = 1, double rotation = 0, Vec2 translation = default)
    {
        Scale = ClampScale(scale);
        Rotation = Normalize(rotation);
        Translation = translation;
    }

    /// <summary>View points per document pixel.</summary>
    public double Scale { get; private init; }
    /// <summary>Radians, clockwise on screen, normalized to (-π, π].</summary>
    public double Rotation { get; private init; }
    /// <summary>View-space position of the document origin.</summary>
    public Vec2 Translation { get; init; }

    // MARK: Mapping

    internal Double2x2 RotationMatrix
    {
        get
        {
            double c = Math.Cos(Rotation), s = Math.Sin(Rotation);
            return new Double2x2(new Vec2(c, s), new Vec2(-s, c));
        }
    }

    public Vec2 ViewPoint(Vec2 document) => Translation + RotationMatrix * (document * Scale);

    public Vec2 DocumentPoint(Vec2 view) => RotationMatrix.Transpose * (view - Translation) / Scale;

    /// <summary>Homogeneous view → document matrix (column-major, for shaders).</summary>
    public Double3x3 ViewToDocumentMatrix
    {
        get
        {
            var a = RotationMatrix.Transpose * (1 / Scale);
            var b = -(a * Translation);
            return new Double3x3(new Vec3(a.Column0, 0), new Vec3(a.Column1, 0), new Vec3(b, 1));
        }
    }

    /// <summary>Homogeneous document → view matrix (column-major).</summary>
    public Double3x3 DocumentToViewMatrix
    {
        get
        {
            var a = RotationMatrix * Scale;
            return new Double3x3(new Vec3(a.Column0, 0), new Vec3(a.Column1, 0), new Vec3(Translation, 1));
        }
    }

    /// <summary>Document-space bounding box of the visible view area.</summary>
    public PixelRect VisibleDocumentRect(Vec2 viewSize)
    {
        Span<Vec2> corners = [DocumentPoint(new Vec2(0, 0)), DocumentPoint(new Vec2(viewSize.X, 0)),
                              DocumentPoint(new Vec2(0, viewSize.Y)), DocumentPoint(viewSize)];
        Vec2 lo = corners[0], hi = corners[0];
        foreach (var c in corners)
        {
            lo = Vec2.Min(lo, c);
            hi = Vec2.Max(hi, c);
        }
        int x0 = (int)Math.Floor(lo.X), y0 = (int)Math.Floor(lo.Y);
        int x1 = (int)Math.Ceiling(hi.X), y1 = (int)Math.Ceiling(hi.Y);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    // MARK: Manipulation (all anchored so the document point under `anchor` stays put)

    public ViewTransform Pan(Vec2 delta) => this with { Translation = Translation + delta };

    public ViewTransform Zoom(double factor, Vec2 anchor) => SetScale(Scale * factor, anchor);

    public ViewTransform SetScale(double newScale, Vec2 anchor)
    {
        var pinned = DocumentPoint(anchor);
        var t = this with { Scale = ClampScale(newScale) };
        return t with { Translation = anchor - t.RotationMatrix * (pinned * t.Scale) };
    }

    public ViewTransform Rotate(double angle, Vec2 anchor) => SetRotation(Rotation + angle, anchor);

    public ViewTransform SetRotation(double angle, Vec2 anchor)
    {
        var pinned = DocumentPoint(anchor);
        var t = this with { Rotation = Normalize(angle) };
        return t with { Translation = anchor - t.RotationMatrix * (pinned * t.Scale) };
    }

    /// <summary>A transform that centers the (possibly rotated) document in the view.</summary>
    public static ViewTransform Fitting(Vec2 documentSize, Vec2 viewSize, double rotation = 0, double margin = 0.92)
    {
        if (!(documentSize.X > 0) || !(documentSize.Y > 0) || !(viewSize.X > 0) || !(viewSize.Y > 0))
            return new ViewTransform(rotation: rotation);
        double c = Math.Abs(Math.Cos(rotation)), s = Math.Abs(Math.Sin(rotation));
        var bounds = new Vec2(c * documentSize.X + s * documentSize.Y, s * documentSize.X + c * documentSize.Y);
        double scale = Math.Min(viewSize.X / bounds.X, viewSize.Y / bounds.Y) * margin;
        var t = new ViewTransform(scale, rotation);
        return t with { Translation = viewSize / 2 - t.RotationMatrix * (documentSize / 2 * t.Scale) };
    }

    /// <summary>Snaps a free rotation to the nearest right angle when within <paramref name="threshold"/> radians.</summary>
    public static double SnappedRotation(double raw, double threshold = 3 * Math.PI / 180)
    {
        const double quarter = Math.PI / 2;
        double nearest = Math.Round(raw / quarter, MidpointRounding.AwayFromZero) * quarter;
        return Math.Abs(raw - nearest) <= threshold ? nearest : raw;
    }

    // MARK: Helpers

    internal static double ClampScale(double s)
    {
        if (!double.IsFinite(s) || !(s > 0)) return 1;
        return Math.Min(Math.Max(s, MinScale), MaxScale);
    }

    internal static double Normalize(double angle)
    {
        double a = angle % (2 * Math.PI);   // Swift's truncatingRemainder: the sign of the dividend
        if (a <= -Math.PI) a += 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }
}

/// <summary>Photoshop-style zoom stops (as a fraction: 1 = 100 %).</summary>
public static class ZoomLadder
{
    public static IReadOnlyList<double> Stops { get; } =
    [
        0.02, 0.03, 0.04, 0.05, 0.0625, 0.0833, 0.125, 0.1667, 0.25, 0.3333,
        0.5, 0.6667, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64,
    ];

    public static double Next(double zoom)
    {
        foreach (var s in Stops) if (s > zoom * 1.0001) return s;
        return Stops[^1];
    }

    public static double Previous(double zoom)
    {
        for (int i = Stops.Count - 1; i >= 0; i--) if (Stops[i] < zoom * 0.9999) return Stops[i];
        return Stops[0];
    }
}
