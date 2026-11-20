// Ported from Packages/GlimmerKit/Sources/GlimmerCore/TextAndPaths.swift
//
// The Mac lays text out and draws it with Core Text and Core Graphics. Here that part is TextLayout.cs (SkiaSharp
// and HarfBuzz, fonts found by FontResolver.cs); this file keeps the model, the warp and path math, and the members
// that call into the layout. Text metrics differ between Core Text and Skia, so sizes and positions computed here are
// close to the Mac's, not equal to them.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

// MARK: Text layers

[JsonConverter(typeof(RawValueEnumConverter<TextAlignment>))]
public enum TextAlignment
{
    [RawValue("left")] Left,
    [RawValue("center")] Center,
    [RawValue("right")] Right,
}

/// <summary>A width and a height in document pixels (Swift's <c>CGSize</c>, CoreGraphics-free like <see cref="CGRectLike"/>).</summary>
public readonly record struct CGSizeLike(double Width, double Height);

/// <summary>
/// An editable text layer. Laid out and drawn by <see cref="TextLayout"/> (SkiaSharp and HarfBuzz) where the Mac
/// uses Core Text.
/// </summary>
[JsonConverter(typeof(TextLayerJsonConverter))]
public sealed partial record TextLayer
{
    public TextLayer(string @string, Vec2 origin, string fontName = "HelveticaNeue", double size = 72, SRGBColor color = default,
                     TextAlignment alignment = TextAlignment.Left, double tracking = 0, double lineHeight = 1.2, double? boxWidth = null)
    {
        String = @string;
        FontName = fontName;
        Size = size;
        Color = color;
        Alignment = alignment;
        Tracking = tracking;
        LineHeight = lineHeight;
        Origin = origin;
        BoxWidth = boxWidth;
    }

    public string String { get; init; }
    /// <summary>PostScript font name (e.g. "HelveticaNeue-Bold"), stored as the Mac wrote it; see <see cref="FontResolver"/>.</summary>
    public string FontName { get; init; }
    /// <summary>Point size in document pixels.</summary>
    public double Size { get; init; }
    public SRGBColor Color { get; init; }
    public TextAlignment Alignment { get; init; }
    /// <summary>Extra spacing between characters, in 1/1000 em (like Photoshop's tracking).</summary>
    public double Tracking { get; init; }
    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineHeight { get; init; }
    /// <summary>Top-left of the text box in document pixels.</summary>
    public Vec2 Origin { get; init; }
    /// <summary>Paragraph text wraps at this width; null = point text.</summary>
    public double? BoxWidth { get; init; }
    /// <summary>
    /// Text on a path: the baseline follows this polyline (document pixels). Alignment places the text at the start,
    /// middle or end of the path.
    /// </summary>
    public ImmutableArray<Vec2>? OnPath { get; init; }
    /// <summary>The document path <see cref="OnPath"/> was taken from; the text follows that path when it is edited.</summary>
    public Guid? OnPathID { get; init; }
    /// <summary>Envelope warp (Photoshop's Warp Text). Ignored for text on a path.</summary>
    public TextWarp? Warp { get; init; }
    /// <summary>
    /// Rotation, scale and skew from Free Transform, applied after layout (document → document), so the text stays
    /// editable. Always affine; null when untransformed.
    /// </summary>
    public AffineTransform? Transform { get; init; }
    /// <summary>
    /// Words or letters styled differently from the layer (font, size, colour); null when the whole text has the
    /// layer's style. See TextStyles.cs.
    /// </summary>
    public ImmutableArray<TextStyleRun>? Styles { get; init; }

    // MARK: Layout (TextLayout.cs)

    /// <summary>Size of the laid-out text (document pixels): the frame's width (the box width when set) and height, rounded up, plus 2.</summary>
    public CGSizeLike LayoutSize => TextLayout.For(this).LayoutSize;

    /// <summary>Bounds of the rendered text, after <see cref="Transform"/>.</summary>
    public PixelRect Bounds
    {
        get
        {
            var b = LayoutBounds;
            if (Transform is not { } t || t == AffineTransform.Identity) return b;
            double x0 = b.X, y0 = b.Y, x1 = b.X + b.Width, y1 = b.Y + b.Height;
            Span<Vec2> corners = [t.Apply(new Vec2(x0, y0)), t.Apply(new Vec2(x1, y0)), t.Apply(new Vec2(x1, y1)), t.Apply(new Vec2(x0, y1))];
            Vec2 lo = corners[0], hi = corners[0];
            foreach (var c in corners)
            {
                lo = Vec2.Min(lo, c);
                hi = Vec2.Max(hi, c);
            }
            int nx = (int)Math.Floor(lo.X), ny = (int)Math.Floor(lo.Y);
            return new PixelRect(nx, ny, (int)Math.Ceiling(hi.X) - nx + 1, (int)Math.Ceiling(hi.Y) - ny + 1);
        }
    }

    /// <summary>Bounds of the laid-out text before <see cref="Transform"/>.</summary>
    public PixelRect LayoutBounds
    {
        get
        {
            if (OnPath is { Length: > 1 } line)
            {
                Vec2 lo = line[0], hi = line[0];
                foreach (var p in line)
                {
                    lo = Vec2.Min(lo, p);
                    hi = Vec2.Max(hi, p);
                }
                double pad = Size * 1.3;
                int x0 = (int)Math.Floor(lo.X - pad), y0 = (int)Math.Floor(lo.Y - pad);
                return new PixelRect(x0, y0, (int)Math.Ceiling(hi.X + pad) - x0, (int)Math.Ceiling(hi.Y + pad) - y0);
            }
            var s = LayoutSize;
            var m = ActiveWarp?.Margins(s.Width, s.Height) ?? Vec2.Zero;
            return new PixelRect((int)Math.Floor(Origin.X - m.X), (int)Math.Floor(Origin.Y - m.Y),
                                 (int)Math.Ceiling(s.Width + 2 * m.X) + 1, (int)Math.Ceiling(s.Height + 2 * m.Y) + 1);
        }
    }

    /// <summary>The warp, when it changes anything.</summary>
    public TextWarp? ActiveWarp => OnPath is null && Warp is { IsActive: true } w ? w : null;

    /// <summary>
    /// Renders the text into an 8-bit sRGB picture whose top-left is <see cref="Bounds"/>' origin (premultiplied, as
    /// the Mac's bitmap context is). Null when there is nothing to draw or the picture would be too big.
    /// </summary>
    public TextRaster? Render() => TextLayout.Render(this);

    /// <summary>
    /// The rendered text as tiles at its own position: premultiplied linear, what the compositor uses (the Mac's
    /// <c>DocumentCompositor.rasterize(_:)</c> turns <c>render()</c> into tiles this way).
    /// </summary>
    public TileGrid RenderTiles() => Render() is { } raster ? raster.ToTiles() : TileGrid.Empty;

    /// <summary>The glyph outlines of the laid-out text, in text-box coordinates (y down, origin top-left).</summary>
    public CGPathLike GlyphOutlines()
    {
        var result = new CGPathLike();
        foreach (var (_, path) in GlyphOutlinesByColor()) result.AddPath(path);
        return result;
    }

    /// <summary>The glyph outlines grouped by the colour they're drawn in, in the order colours first appear.</summary>
    public IReadOnlyList<(SRGBColor Color, CGPathLike Path)> GlyphOutlinesByColor() => TextLayout.GlyphOutlinesByColor(this);

    public bool Equals(TextLayer? other) =>
        other is not null && String == other.String && FontName == other.FontName && Size.Equals(other.Size) && Color == other.Color
        && Alignment == other.Alignment && Tracking.Equals(other.Tracking) && LineHeight.Equals(other.LineHeight) && Origin == other.Origin
        && Nullable.Equals(BoxWidth, other.BoxWidth) && Seq.Equal(OnPath, other.OnPath) && Nullable.Equals(OnPathID, other.OnPathID)
        && Equals(Warp, other.Warp) && Equals(Transform, other.Transform) && Seq.Equal(Styles, other.Styles);

    public override int GetHashCode() =>
        HashCode.Combine(String, FontName, Size, Color, HashCode.Combine(Alignment, Tracking, LineHeight, Origin, BoxWidth),
                         Seq.Hash(OnPath), HashCode.Combine(OnPathID, Warp, Transform), Seq.Hash(Styles));

    /// <summary>The stored fields only (a record would also print the layout, which lays the text out).</summary>
    public override string ToString() =>
        $"TextLayer {{ String = {String}, FontName = {FontName}, Size = {Size}, Origin = {Origin}, BoxWidth = {BoxWidth} }}";
}

/// <summary>
/// A picture of rendered text: <see cref="Width"/> × <see cref="Height"/> pixels of 8-bit RGBA, premultiplied, in
/// sRGB, rows top-down, with its top-left at (<see cref="X"/>, <see cref="Y"/>) in the document.
/// </summary>
public sealed class TextRaster
{
    public TextRaster(int x, int y, int width, int height, byte[] pixels)
    {
        if (pixels.Length < (long)width * height * 4) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Premultiplied sRGB RGBA, 4 bytes a pixel, row-major.</summary>
    public byte[] Pixels { get; }

    public PixelRect Rect => new(X, Y, Width, Height);

    /// <summary>The picture as tiles: premultiplied linear extended sRGB, as Glimmer stores pixels.</summary>
    public TileGrid ToTiles()
    {
        var rect = Rect;
        if (rect.IsEmpty) return TileGrid.Empty;
        var table = ColorMath.SrgbByteToLinear;
        var rgba = new float[rect.Area * 4];
        for (long i = 0; i < rect.Area; i++)
        {
            long o = i * 4;
            byte a = Pixels[o + 3];
            if (a == 0) continue;   // stays transparent zero
            float alpha = a / 255f;
            for (int c = 0; c < 3; c++)
            {
                // Unpremultiply to the 8-bit code it stands for, linearize, premultiply again.
                int code = (int)Math.Round(Math.Min(255.0, Pixels[o + c] * 255.0 / a));
                rgba[o + c] = table[code] * alpha;
            }
            rgba[o + 3] = alpha;
        }
        return TileGrid.Empty.WriteRegion(rect, rgba);
    }
}

// MARK: Warp Text

[JsonConverter(typeof(RawValueEnumConverter<TextWarpStyle>))]
public enum TextWarpStyle
{
    [RawValue("none")] None,
    [RawValue("arc")] Arc,
    [RawValue("arch")] Arch,
    [RawValue("bulge")] Bulge,
    [RawValue("wave")] Wave,
    [RawValue("flag")] Flag,
    [RawValue("rise")] Rise,
}

public static class TextWarpStyleTypeExtensions
{
    extension(TextWarpStyle)
    {
        public static IReadOnlyList<TextWarpStyle> AllCases => RawValues<TextWarpStyle>.AllCases;
        public static TextWarpStyle? FromRawValue(string? raw) => RawValues<TextWarpStyle>.Parse(raw);
    }
}

public static class TextAlignmentTypeExtensions
{
    extension(TextAlignment)
    {
        public static IReadOnlyList<TextAlignment> AllCases => RawValues<TextAlignment>.AllCases;
        public static TextAlignment? FromRawValue(string? raw) => RawValues<TextAlignment>.Parse(raw);
    }
}

public static class TextKindExtensions
{
    public static string RawValue(this TextWarpStyle style) => RawValues<TextWarpStyle>.Of(style);
    public static string Id(this TextWarpStyle style) => style.RawValue();
    public static string Title(this TextWarpStyle style) => FillKindExtensions.Capitalized(style.RawValue());

    public static string RawValue(this TextAlignment alignment) => RawValues<TextAlignment>.Of(alignment);
    public static string Title(this TextAlignment alignment) => FillKindExtensions.Capitalized(alignment.RawValue());
}

/// <summary>
/// Photoshop-style text warp envelope (the web app's <c>TextWarp</c>). <see cref="Bend"/>, <see cref="Horizontal"/>
/// and <see cref="Vertical"/> are -1…1.
/// </summary>
[JsonConverter(typeof(TextWarpJsonConverter))]
public sealed record TextWarp(TextWarpStyle Style, double Bend = 0.5, double Horizontal = 0, double Vertical = 0)
{
    public bool IsActive => Style != TextWarpStyle.None && (Bend != 0 || Horizontal != 0 || Vertical != 0);

    /// <summary>
    /// Displacement of the box point at normalised (u, v) (0…1, v down) as a fraction of the box size. The formulas
    /// match the web app.
    /// </summary>
    public Vec2 Displacement(double u, double v)
    {
        double bend = Math.Min(Math.Max(Bend, -1), 1), hz = Math.Min(Math.Max(Horizontal, -1), 1), vt = Math.Min(Math.Max(Vertical, -1), 1);
        double uc = u * 2 - 1, vc = v * 2 - 1;
        double dy = Style switch
        {
            TextWarpStyle.None => 0,
            TextWarpStyle.Arc => bend * 0.5 * (1 - uc * uc) * (1 - v),
            TextWarpStyle.Arch => bend * 0.5 * (1 - uc * uc),
            TextWarpStyle.Bulge => bend * 0.5 * (1 - uc * uc) * vc,
            TextWarpStyle.Wave => bend * 0.3 * Math.Sin(uc * Math.PI * 2),
            TextWarpStyle.Flag => bend * 0.3 * Math.Sin(uc * Math.PI * 2) * (0.4 + 0.6 * u),
            TextWarpStyle.Rise => bend * 0.5 * uc,
            _ => 0,
        };
        dy += vt * 0.3 * uc;
        return new Vec2(hz * 0.3 * vc, dy);
    }

    /// <summary>How far the warp can push content outside a <paramref name="width"/> × <paramref name="height"/> box.</summary>
    public Vec2 Margins(double width, double height)
    {
        double b = Math.Abs(Math.Min(Math.Max(Bend, -1), 1));
        double v = Style switch
        {
            TextWarpStyle.None => 0,
            TextWarpStyle.Arc or TextWarpStyle.Arch or TextWarpStyle.Bulge or TextWarpStyle.Rise => b * 0.5,
            _ => b * 0.3,   // wave, flag
        };
        v += Math.Abs(Vertical) * 0.3;
        return new Vec2(Math.Ceiling(Math.Abs(Horizontal) * 0.3 * width) + 2, Math.Ceiling(v * height) + 2);
    }

    /// <summary>Maps a point in a <paramref name="width"/> × <paramref name="height"/> box (y down) through the envelope.</summary>
    public Vec2 Map(Vec2 p, double width, double height)
    {
        var d = Displacement(width > 0 ? p.X / width : 0, height > 0 ? p.Y / height : 0);
        return p + d * new Vec2(width, height);
    }

    /// <summary>Bends a path (box coordinates, y down). Curves are flattened first so straight stems bend smoothly too.</summary>
    public CGPathLike Apply(CGPathLike path, double width, double height)
    {
        var output = new CGPathLike();
        double step = Math.Max(Math.Min(width, height) / 64, 0.75);
        Vec2 current = Vec2.Zero, start = Vec2.Zero;

        void Emit(Vec2 p, bool move)
        {
            var m = Map(p, width, height);
            if (move) output.MoveTo(m);
            else output.LineTo(m);
        }

        void LineTo(Vec2 p)
        {
            int n = Math.Max((int)Math.Ceiling(Math.Sqrt((p.X - current.X) * (p.X - current.X) + (p.Y - current.Y) * (p.Y - current.Y)) / step), 1);
            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                Emit(new Vec2(current.X + (p.X - current.X) * t, current.Y + (p.Y - current.Y) * t), move: false);
            }
            current = p;
        }

        void Curve(int n, Func<double, Vec2> at)
        {
            for (int i = 1; i <= n; i++) LineTo(at((double)i / n));
        }

        foreach (var e in path.Elements)
        {
            switch (e.Verb)
            {
                case CGPathLike.Verb.Move:
                    current = e.P0;
                    start = current;
                    Emit(current, move: true);
                    break;
                case CGPathLike.Verb.Line:
                    LineTo(e.P0);
                    break;
                case CGPathLike.Verb.Quad:
                {
                    Vec2 p0 = current, c = e.P0, p1 = e.P1;
                    Curve(8, t =>
                    {
                        double a = (1 - t) * (1 - t), b = 2 * (1 - t) * t, d = t * t;
                        return new Vec2(a * p0.X + b * c.X + d * p1.X, a * p0.Y + b * c.Y + d * p1.Y);
                    });
                    break;
                }
                case CGPathLike.Verb.Cubic:
                {
                    Vec2 p0 = current, c1 = e.P0, c2 = e.P1, p1 = e.P2;
                    Curve(10, t =>
                    {
                        double m = 1 - t;
                        double a = m * m * m, b = 3 * m * m * t, c = 3 * m * t * t, d = t * t * t;
                        return new Vec2(a * p0.X + b * c1.X + c * c2.X + d * p1.X, a * p0.Y + b * c1.Y + c * c2.Y + d * p1.Y);
                    });
                    break;
                }
                case CGPathLike.Verb.Close:
                    LineTo(start);
                    output.Close();
                    break;
            }
        }
        return output;
    }
}

/// <summary>
/// A 2D path of lines and Bézier curves, in the coordinates of whoever made it (Swift's <c>CGPath</c>,
/// CoreGraphics-free like <see cref="CGRectLike"/>). Built by appending; never changed once handed out.
/// </summary>
public sealed class CGPathLike
{
    public enum Verb { Move, Line, Quad, Cubic, Close }

    /// <summary>
    /// One element: <c>Move</c>/<c>Line</c> use <see cref="P0"/>; <c>Quad</c> has the control point in P0 and the end in P1;
    /// <c>Cubic</c> has the controls in P0, P1 and the end in P2; <c>Close</c> uses none.
    /// </summary>
    public readonly record struct Element(Verb Verb, Vec2 P0 = default, Vec2 P1 = default, Vec2 P2 = default);

    private readonly List<Element> elements = [];

    public IReadOnlyList<Element> Elements => elements;
    public bool IsEmpty => elements.Count == 0;

    public void MoveTo(Vec2 p) => elements.Add(new Element(Verb.Move, p));
    public void LineTo(Vec2 p) => elements.Add(new Element(Verb.Line, p));
    public void QuadTo(Vec2 control, Vec2 end) => elements.Add(new Element(Verb.Quad, control, end));
    public void CubicTo(Vec2 control1, Vec2 control2, Vec2 end) => elements.Add(new Element(Verb.Cubic, control1, control2, end));
    public void Close() => elements.Add(new Element(Verb.Close));

    /// <summary>Appends <paramref name="other"/>, mapped through <paramref name="transform"/> when given.</summary>
    public void AddPath(CGPathLike other, AffineTransform? transform = null)
    {
        if (transform is null)
        {
            elements.AddRange(other.elements);
            return;
        }
        foreach (var e in other.elements)
            elements.Add(e with { P0 = transform.Apply(e.P0), P1 = transform.Apply(e.P1), P2 = transform.Apply(e.P2) });
    }

    public CGPathLike Transformed(AffineTransform transform)
    {
        var result = new CGPathLike();
        result.AddPath(this, transform);
        return result;
    }

    /// <summary>The bounding box of every point, control points included; null when empty.</summary>
    public CGRectLike? ControlBounds
    {
        get
        {
            Vec2? lo = null, hi = null;
            foreach (var e in elements)
            {
                int n = e.Verb switch { Verb.Move or Verb.Line => 1, Verb.Quad => 2, Verb.Cubic => 3, _ => 0 };
                for (int i = 0; i < n; i++)
                {
                    var p = i == 0 ? e.P0 : i == 1 ? e.P1 : e.P2;
                    lo = lo is { } l ? Vec2.Min(l, p) : p;
                    hi = hi is { } h ? Vec2.Max(h, p) : p;
                }
            }
            return lo is { } a && hi is { } b ? new CGRectLike(a.X, a.Y, b.X, b.Y) : null;
        }
    }

    /// <summary>
    /// The path as a pen path: each subpath's quadratic curves become cubic ones (exactly), so glyph outlines can be
    /// edited with the pen tool.
    /// </summary>
    public VectorPath ToVectorPath(string name)
    {
        var subpaths = new List<Subpath>();
        List<PathAnchor>? anchors = null;
        Vec2 current = Vec2.Zero;

        void Finish(bool closed)
        {
            if (anchors is { Count: > 0 })
            {
                if (closed && anchors.Count > 1 && anchors[^1].Point == anchors[0].Point)
                {
                    // The closing point coincides with the first: fold its incoming handle into the first anchor.
                    var last = anchors[^1];
                    anchors[0] = anchors[0] with { HandleIn = last.HandleIn };
                    anchors.RemoveAt(anchors.Count - 1);
                }
                subpaths.Add(new Subpath(anchors, closed));
            }
            anchors = null;
        }

        void CurveTo(Vec2 c1, Vec2 c2, Vec2 end)
        {
            anchors ??= [new PathAnchor(current)];
            anchors[^1] = anchors[^1] with { HandleOut = c1 };
            anchors.Add(new PathAnchor(end, handleIn: c2));
            current = end;
        }

        foreach (var e in elements)
        {
            switch (e.Verb)
            {
                case Verb.Move:
                    Finish(false);
                    current = e.P0;
                    anchors = [new PathAnchor(current)];
                    break;
                case Verb.Line:
                    anchors ??= [new PathAnchor(current)];
                    anchors.Add(new PathAnchor(e.P0));
                    current = e.P0;
                    break;
                case Verb.Quad:
                    CurveTo(current + (e.P0 - current) * (2.0 / 3), e.P1 + (e.P0 - e.P1) * (2.0 / 3), e.P1);
                    break;
                case Verb.Cubic:
                    CurveTo(e.P0, e.P1, e.P2);
                    break;
                case Verb.Close:
                    Finish(true);
                    break;
            }
        }
        Finish(false);
        return new VectorPath(name, subpaths);
    }
}

// MARK: Paths (pen tool)

[JsonConverter(typeof(PathAnchorJsonConverter))]
public readonly record struct PathAnchor
{
    public PathAnchor(Vec2 point, Vec2? handleIn = null, Vec2? handleOut = null)
    {
        Point = point;
        HandleIn = handleIn ?? point;
        HandleOut = handleOut ?? point;
    }

    public Vec2 Point { get; init; }
    /// <summary>Absolute position of the incoming control point (equal to <see cref="Point"/> for corners).</summary>
    public Vec2 HandleIn { get; init; }
    /// <summary>Absolute position of the outgoing control point (equal to <see cref="Point"/> for corners).</summary>
    public Vec2 HandleOut { get; init; }
}

[JsonConverter(typeof(SubpathJsonConverter))]
public sealed record Subpath
{
    public Subpath(IEnumerable<PathAnchor>? anchors = null, bool isClosed = false)
    {
        Anchors = anchors is null ? [] : [.. anchors];
        IsClosed = isClosed;
    }

    public ImmutableArray<PathAnchor> Anchors { get; init; }
    public bool IsClosed { get; init; }

    public bool Equals(Subpath? other) => other is not null && IsClosed == other.IsClosed && Seq.Equal(Anchors, other.Anchors);
    public override int GetHashCode() => HashCode.Combine(IsClosed, Seq.Hash(Anchors));
}

[JsonConverter(typeof(VectorPathJsonConverter))]
public sealed record VectorPath
{
    public VectorPath(string name, IEnumerable<Subpath>? subpaths = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Subpaths = subpaths is null ? [] : [.. subpaths];
    }

    public Guid Id { get; init; }
    public string Name { get; init; }
    public ImmutableArray<Subpath> Subpaths { get; init; }

    /// <summary>The path with every point mapped through <paramref name="t"/>.</summary>
    public VectorPath Applying(AffineTransform t) => this with
    {
        Subpaths = [.. Subpaths.Select(sub => sub with
        {
            Anchors = [.. sub.Anchors.Select(a => new PathAnchor(t.Apply(a.Point), t.Apply(a.HandleIn), t.Apply(a.HandleOut)))],
        })],
    };

    /// <summary>The area enclosed by the path (each subpath filled; overlapping subpaths add).</summary>
    public Selection AsSelection => Selection.Polygons(Polygons());

    /// <summary>Bounding box of all anchors and handles.</summary>
    public CGRectLike? ControlBounds
    {
        get
        {
            Vec2? lo = null, hi = null;
            foreach (var sub in Subpaths)
            {
                foreach (var a in sub.Anchors)
                {
                    foreach (var p in (ReadOnlySpan<Vec2>)[a.Point, a.HandleIn, a.HandleOut])
                    {
                        lo = lo is { } l ? Vec2.Min(l, p) : p;
                        hi = hi is { } h ? Vec2.Max(h, p) : p;
                    }
                }
            }
            return lo is { } x && hi is { } y ? new CGRectLike(x.X, x.Y, y.X, y.Y) : null;
        }
    }

    // MARK: Shapes (cubic Béziers, so they scale cleanly)

    public static VectorPath Rectangle(CGRectLike r, string name = "Rectangle") => RoundedRectangle(r, 0, name);

    public static VectorPath RoundedRectangle(CGRectLike r, double radius, string name = "Rounded Rectangle")
    {
        double rad = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height / 2)));
        if (!(rad > 0.01))
        {
            return new VectorPath(name, [new Subpath([
                new PathAnchor(new Vec2(r.MinX, r.MinY)), new PathAnchor(new Vec2(r.MaxX, r.MinY)),
                new PathAnchor(new Vec2(r.MaxX, r.MaxY)), new PathAnchor(new Vec2(r.MinX, r.MaxY)),
            ], isClosed: true)]);
        }
        double k = rad * 0.5522847498;
        double x0 = r.MinX, y0 = r.MinY, x1 = r.MaxX, y1 = r.MaxY;
        PathAnchor[] a =
        [
            new(new Vec2(x0 + rad, y0), handleIn: new Vec2(x0 + rad - k, y0)),
            new(new Vec2(x1 - rad, y0), handleOut: new Vec2(x1 - rad + k, y0)),
            new(new Vec2(x1, y0 + rad), handleIn: new Vec2(x1, y0 + rad - k)),
            new(new Vec2(x1, y1 - rad), handleOut: new Vec2(x1, y1 - rad + k)),
            new(new Vec2(x1 - rad, y1), handleIn: new Vec2(x1 - rad + k, y1)),
            new(new Vec2(x0 + rad, y1), handleOut: new Vec2(x0 + rad - k, y1)),
            new(new Vec2(x0, y1 - rad), handleIn: new Vec2(x0, y1 - rad + k)),
            new(new Vec2(x0, y0 + rad), handleOut: new Vec2(x0, y0 + rad - k)),
        ];
        return new VectorPath(name, [new Subpath(a, isClosed: true)]);
    }

    public static VectorPath Ellipse(CGRectLike r, string name = "Ellipse")
    {
        double cx = (r.MinX + r.MaxX) / 2, cy = (r.MinY + r.MaxY) / 2, rx = r.Width / 2, ry = r.Height / 2;
        double kx = rx * 0.5522847498, ky = ry * 0.5522847498;
        PathAnchor[] a =
        [
            new(new Vec2(cx, cy - ry), handleIn: new Vec2(cx - kx, cy - ry), handleOut: new Vec2(cx + kx, cy - ry)),
            new(new Vec2(cx + rx, cy), handleIn: new Vec2(cx + rx, cy - ky), handleOut: new Vec2(cx + rx, cy + ky)),
            new(new Vec2(cx, cy + ry), handleIn: new Vec2(cx + kx, cy + ry), handleOut: new Vec2(cx - kx, cy + ry)),
            new(new Vec2(cx - rx, cy), handleIn: new Vec2(cx - rx, cy + ky), handleOut: new Vec2(cx - rx, cy - ky)),
        ];
        return new VectorPath(name, [new Subpath(a, isClosed: true)]);
    }

    /// <summary>Flattened polygons (one per subpath), <paramref name="steps"/> samples per curved segment.</summary>
    public Vec2[][] Polygons(int steps = 24)
    {
        var result = new Vec2[Subpaths.Length][];
        for (int s = 0; s < Subpaths.Length; s++)
        {
            var sub = Subpaths[s];
            var output = new List<Vec2>();
            int n = sub.Anchors.Length;
            if (n > 0)
            {
                output.Add(sub.Anchors[0].Point);
                int segments = sub.IsClosed ? n : n - 1;
                for (int i = 0; i < Math.Max(segments, 0); i++)
                {
                    PathAnchor a = sub.Anchors[i], b = sub.Anchors[(i + 1) % n];
                    bool straight = a.HandleOut == a.Point && b.HandleIn == b.Point;
                    if (straight)
                    {
                        output.Add(b.Point);
                        continue;
                    }
                    for (int k = 1; k <= steps; k++)
                    {
                        double t = (double)k / steps, u = 1 - t;
                        output.Add(u * u * u * a.Point + 3 * u * u * t * a.HandleOut + 3 * u * t * t * b.HandleIn + t * t * t * b.Point);
                    }
                }
            }
            result[s] = [.. output];
        }
        return result;
    }

    /// <summary>The path as Bézier segments (Swift's <c>cgPath</c>).</summary>
    public CGPathLike CGPath
    {
        get
        {
            var path = new CGPathLike();
            foreach (var sub in Subpaths)
            {
                if (sub.Anchors.IsEmpty) continue;
                var first = sub.Anchors[0];
                path.MoveTo(first.Point);
                int n = sub.Anchors.Length;
                int segments = sub.IsClosed ? n : n - 1;
                for (int i = 0; i < Math.Max(segments, 0); i++)
                {
                    PathAnchor a = sub.Anchors[i], b = sub.Anchors[(i + 1) % n];
                    path.CubicTo(a.HandleOut, b.HandleIn, b.Point);
                }
                if (sub.IsClosed) path.Close();
            }
            return path;
        }
    }

    public bool Equals(VectorPath? other) => other is not null && Id == other.Id && Name == other.Name && Seq.Equal(Subpaths, other.Subpaths);
    public override int GetHashCode() => HashCode.Combine(Id, Name, Seq.Hash(Subpaths));
}

// MARK: Guides and grid

[JsonConverter(typeof(RawValueEnumConverter<GuideAxis>))]
public enum GuideAxis
{
    [RawValue("horizontal")] Horizontal,
    [RawValue("vertical")] Vertical,
}

[JsonConverter(typeof(GuideJsonConverter))]
public sealed record Guide
{
    public Guide(GuideAxis axis, double position, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Axis = axis;
        Position = position;
    }

    public Guid Id { get; init; }
    public GuideAxis Axis { get; init; }
    /// <summary>Document pixels (y for horizontal, x for vertical).</summary>
    public double Position { get; init; }
}

[JsonConverter(typeof(GridSettingsJsonConverter))]
public sealed record GridSettings
{
    public bool IsVisible { get; init; } = false;
    public double Spacing { get; init; } = 100.0;
    public int Subdivisions { get; init; } = 4;
}

[JsonConverter(typeof(ViewAidsJsonConverter))]
public sealed record ViewAids
{
    public ImmutableArray<Guide> Guides { get; init; } = [];
    public GridSettings Grid { get; init; } = new();
    public bool RulersVisible { get; init; } = false;
    public bool Snap { get; init; } = true;

    public bool Equals(ViewAids? other) =>
        other is not null && Seq.Equal(Guides, other.Guides) && Grid == other.Grid && RulersVisible == other.RulersVisible && Snap == other.Snap;

    public override int GetHashCode() => HashCode.Combine(Seq.Hash(Guides), Grid, RulersVisible, Snap);
}

// MARK: JSON

/// <summary>Synthesized <c>Codable</c>: the eight plain properties are required; the optionals are left out when nil.</summary>
public sealed class TextLayerJsonConverter : JsonConverter<TextLayer>
{
    public override TextLayer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(TextLayer));
        return new TextLayer(o.ReqString("string"), o.Req<Vec2>("origin", options), o.ReqString("fontName"), o.ReqDouble("size"),
                             o.Req<SRGBColor>("color", options), o.Req<TextAlignment>("alignment", options), o.ReqDouble("tracking"),
                             o.ReqDouble("lineHeight"), o.OptDouble("boxWidth"))
        {
            OnPath = o.OptArray<Vec2>("onPath", options),
            OnPathID = o.OptUuid("onPathID"),
            Warp = o.Opt<TextWarp>("warp", options),
            Transform = o.Opt<AffineTransform>("transform", options),
            Styles = o.OptArray<TextStyleRun>("styles", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, TextLayer value, JsonSerializerOptions options) =>
        new JsonFields(options)
            .String("string", value.String).String("fontName", value.FontName).Double("size", value.Size).Value("color", value.Color)
            .Value("alignment", value.Alignment).Double("tracking", value.Tracking).Double("lineHeight", value.LineHeight)
            .Value("origin", value.Origin).Double("boxWidth", value.BoxWidth).Array("onPath", value.OnPath)
            .Uuid("onPathID", value.OnPathID).Value("warp", value.Warp).Value("transform", value.Transform)
            .Array("styles", value.Styles).WriteTo(writer);
}

/// <summary>The Mac's custom <c>init(from:)</c>: every field falls back (style to none, numbers to 0) when missing or of the wrong type.</summary>
public sealed class TextWarpJsonConverter : JsonConverter<TextWarp>
{
    public override TextWarp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(TextWarp));
        var style = o.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String
            ? RawValues<TextWarpStyle>.Parse(s.GetString()) ?? TextWarpStyle.None
            : TextWarpStyle.None;
        static double Lenient(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
        return new TextWarp(style, Lenient(o, "bend"), Lenient(o, "horizontal"), Lenient(o, "vertical"));
    }

    public override void Write(Utf8JsonWriter writer, TextWarp value, JsonSerializerOptions options) =>
        new JsonFields(options).Value("style", value.Style).Double("bend", value.Bend).Double("horizontal", value.Horizontal)
            .Double("vertical", value.Vertical).WriteTo(writer);
}

public sealed class PathAnchorJsonConverter : JsonConverter<PathAnchor>
{
    public override PathAnchor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(PathAnchor));
        return new PathAnchor(o.Req<Vec2>("point", options), o.Req<Vec2>("handleIn", options), o.Req<Vec2>("handleOut", options));
    }

    public override void Write(Utf8JsonWriter writer, PathAnchor value, JsonSerializerOptions options) =>
        new JsonFields(options).Value("point", value.Point).Value("handleIn", value.HandleIn).Value("handleOut", value.HandleOut).WriteTo(writer);
}

public sealed class SubpathJsonConverter : JsonConverter<Subpath>
{
    public override Subpath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(Subpath));
        return new Subpath(o.ReqArray<PathAnchor>("anchors", options), o.ReqBool("isClosed"));
    }

    public override void Write(Utf8JsonWriter writer, Subpath value, JsonSerializerOptions options) =>
        new JsonFields(options).Array("anchors", value.Anchors).Bool("isClosed", value.IsClosed).WriteTo(writer);
}

public sealed class VectorPathJsonConverter : JsonConverter<VectorPath>
{
    public override VectorPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(VectorPath));
        return new VectorPath(o.ReqString("name"), o.ReqArray<Subpath>("subpaths", options), o.ReqUuid("id"));
    }

    public override void Write(Utf8JsonWriter writer, VectorPath value, JsonSerializerOptions options) =>
        new JsonFields(options).Uuid("id", value.Id).String("name", value.Name)
            .Array("subpaths", value.Subpaths).WriteTo(writer);
}

public sealed class GuideJsonConverter : JsonConverter<Guide>
{
    public override Guide Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(Guide));
        return new Guide(o.Req<GuideAxis>("axis", options), o.ReqDouble("position"), o.ReqUuid("id"));
    }

    public override void Write(Utf8JsonWriter writer, Guide value, JsonSerializerOptions options) =>
        new JsonFields(options).Uuid("id", value.Id).Value("axis", value.Axis).Double("position", value.Position).WriteTo(writer);
}

public sealed class GridSettingsJsonConverter : JsonConverter<GridSettings>
{
    public override GridSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(GridSettings));
        return new GridSettings { IsVisible = o.ReqBool("isVisible"), Spacing = o.ReqDouble("spacing"), Subdivisions = (int)o.ReqInt("subdivisions") };
    }

    public override void Write(Utf8JsonWriter writer, GridSettings value, JsonSerializerOptions options) =>
        new JsonFields(options).Bool("isVisible", value.IsVisible).Double("spacing", value.Spacing).Int("subdivisions", value.Subdivisions).WriteTo(writer);
}

public sealed class ViewAidsJsonConverter : JsonConverter<ViewAids>
{
    public override ViewAids Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(ViewAids));
        return new ViewAids
        {
            Guides = o.ReqArray<Guide>("guides", options),
            Grid = o.Req<GridSettings>("grid", options),
            RulersVisible = o.ReqBool("rulersVisible"),
            Snap = o.ReqBool("snap"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ViewAids value, JsonSerializerOptions options) =>
        new JsonFields(options).Array("guides", value.Guides).Value("grid", value.Grid)
            .Bool("rulersVisible", value.RulersVisible).Bool("snap", value.Snap).WriteTo(writer);
}
