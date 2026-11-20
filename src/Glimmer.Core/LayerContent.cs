// Ported from Packages/GlimmerKit/Sources/GlimmerCore/LayerContent.swift

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

// MARK: Masks

/// <summary>
/// A non-destructive layer mask in document coordinates. Stored tiles hold the mask value in every channel; missing
/// tiles use <see cref="DefaultValue"/>.
/// </summary>
public sealed record LayerMask
{
    public LayerMask(TileGrid? tiles = null, float defaultValue = 1, bool isEnabled = true, double density = 1, double feather = 0)
    {
        Tiles = tiles ?? TileGrid.Empty;
        DefaultValue = defaultValue;
        IsEnabled = isEnabled;
        Density = density;
        Feather = feather;
    }

    public TileGrid Tiles { get; init; }
    /// <summary>1 = reveal (white), 0 = hide (black), used where no tile is stored.</summary>
    public float DefaultValue { get; init; }
    public bool IsEnabled { get; init; }
    /// <summary>0...1. At 0 the mask has no effect.</summary>
    public double Density { get; init; }
    /// <summary>Gaussian feather radius in pixels (non-destructive).</summary>
    public double Feather { get; init; }

    public static LayerMask RevealAll { get; } = new(defaultValue: 1);
    public static LayerMask HideAll { get; } = new(defaultValue: 0);

    /// <summary>The mask tile at <paramref name="coord"/>, synthesizing a solid tile for the default.</summary>
    public Tile TileAt(TileCoord coord) => Tiles[coord] ?? Tile.Solid(new Vector4(DefaultValue));
}

// MARK: Layer styles

[JsonConverter(typeof(ShadowEffectJsonConverter))]
public sealed record ShadowEffect
{
    public bool IsEnabled { get; init; } = true;
    public SRGBColor Color { get; init; } = SRGBColor.Black;
    public double Opacity { get; init; } = 0.75;
    /// <summary>Light angle in degrees (0 = from the right, counter-clockwise positive).</summary>
    public double Angle { get; init; } = 120.0;
    public double Distance { get; init; } = 10.0;
    public double Size { get; init; } = 10.0;
    /// <summary>0...1, thickens the shape before blurring.</summary>
    public double Spread { get; init; } = 0.0;
    public BlendMode BlendMode { get; init; } = BlendMode.Multiply;

    /// <summary>Offset in document pixels (y down), opposite the light.</summary>
    public Vec2 Offset
    {
        get
        {
            double r = Angle * Math.PI / 180;
            return new Vec2(-Math.Cos(r) * Distance, Math.Sin(r) * Distance);
        }
    }
}

[JsonConverter(typeof(GlowEffectJsonConverter))]
public sealed record GlowEffect
{
    public bool IsEnabled { get; init; } = true;
    public SRGBColor Color { get; init; } = new(1, 1, 0.75);
    public double Opacity { get; init; } = 0.75;
    public double Size { get; init; } = 10.0;
    public double Spread { get; init; } = 0.0;
    public BlendMode BlendMode { get; init; } = BlendMode.Screen;
}

/// <summary>Where a stroke sits relative to the layer's edge (Swift's <c>StrokeEffect.Position</c>).</summary>
[JsonConverter(typeof(RawValueEnumConverter<StrokePosition>))]
public enum StrokePosition
{
    [RawValue("outside")] Outside,
    [RawValue("inside")] Inside,
    [RawValue("center")] Center,
}

[JsonConverter(typeof(StrokeEffectJsonConverter))]
public sealed record StrokeEffect
{
    public bool IsEnabled { get; init; } = true;
    public SRGBColor Color { get; init; } = new(1, 0, 0);
    public double Opacity { get; init; } = 1.0;
    public double Width { get; init; } = 3.0;
    public StrokePosition Position { get; init; } = StrokePosition.Outside;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;
}

[JsonConverter(typeof(ColorOverlayEffectJsonConverter))]
public sealed record ColorOverlayEffect
{
    public bool IsEnabled { get; init; } = true;
    public SRGBColor Color { get; init; } = new(1, 0, 0);
    public double Opacity { get; init; } = 1.0;
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;
}

[JsonConverter(typeof(LayerEffectsJsonConverter))]
public sealed record LayerEffects
{
    public ShadowEffect? DropShadow { get; init; }
    public ShadowEffect? InnerShadow { get; init; }
    public GlowEffect? OuterGlow { get; init; }
    public StrokeEffect? Stroke { get; init; }
    public ColorOverlayEffect? ColorOverlay { get; init; }

    public static LayerEffects None { get; } = new();

    public bool HasEnabled =>
        DropShadow?.IsEnabled == true || InnerShadow?.IsEnabled == true || OuterGlow?.IsEnabled == true
        || Stroke?.IsEnabled == true || ColorOverlay?.IsEnabled == true;

    /// <summary>Effects that need neighbourhood processing (everything but colour overlay).</summary>
    public bool HasSpatialEffects =>
        DropShadow?.IsEnabled == true || InnerShadow?.IsEnabled == true || OuterGlow?.IsEnabled == true || Stroke?.IsEnabled == true;

    /// <summary>The largest distance an effect reaches beyond the layer's pixels.</summary>
    public int Reach
    {
        get
        {
            double r = 0;
            if (DropShadow is { IsEnabled: true } d) r = Math.Max(r, d.Distance + d.Size * 1.5);
            if (InnerShadow is { IsEnabled: true } i) r = Math.Max(r, i.Distance + i.Size * 1.5);
            if (OuterGlow is { IsEnabled: true } g) r = Math.Max(r, g.Size * 1.5);
            if (Stroke is { IsEnabled: true } s) r = Math.Max(r, s.Width);
            return (int)Math.Ceiling(r) + 2;
        }
    }

    /// <summary>
    /// The same styles for an image scaled by <paramref name="factor"/> (Image Size): distances, sizes and stroke
    /// widths follow, so a shadow looks the same on the smaller image.
    /// </summary>
    public LayerEffects Scaled(double factor) => this with
    {
        DropShadow = DropShadow is null ? null : DropShadow with { Distance = DropShadow.Distance * factor, Size = DropShadow.Size * factor },
        InnerShadow = InnerShadow is null ? null : InnerShadow with { Distance = InnerShadow.Distance * factor, Size = InnerShadow.Size * factor },
        OuterGlow = OuterGlow is null ? null : OuterGlow with { Size = OuterGlow.Size * factor },
        Stroke = Stroke is null ? null : Stroke with { Width = Stroke.Width * factor },
    };
}

// MARK: Fill layers

[JsonConverter(typeof(RawValueEnumConverter<PatternKind>))]
public enum PatternKind
{
    [RawValue("checkerboard")] Checkerboard,
    [RawValue("dots")] Dots,
    [RawValue("stripes")] Stripes,
    [RawValue("grid")] Grid,
    [RawValue("noise")] Noise,
}

[JsonConverter(typeof(RawValueEnumConverter<GradientStyle>))]
public enum GradientStyle
{
    [RawValue("linear")] Linear,
    [RawValue("radial")] Radial,
}

public static class PatternKindTypeExtensions
{
    extension(PatternKind)
    {
        public static IReadOnlyList<PatternKind> AllCases => RawValues<PatternKind>.AllCases;
        public static PatternKind? FromRawValue(string? raw) => RawValues<PatternKind>.Parse(raw);
    }
}

public static class GradientStyleTypeExtensions
{
    extension(GradientStyle)
    {
        public static IReadOnlyList<GradientStyle> AllCases => RawValues<GradientStyle>.AllCases;
        public static GradientStyle? FromRawValue(string? raw) => RawValues<GradientStyle>.Parse(raw);
    }
}

public static class FillKindExtensions
{
    public static string RawValue(this PatternKind kind) => RawValues<PatternKind>.Of(kind);
    /// <summary><c>rawValue.capitalized</c>.</summary>
    public static string Title(this PatternKind kind) => Capitalized(kind.RawValue());
    public static int ShaderIndex(this PatternKind kind) => (int)kind;

    public static string RawValue(this GradientStyle style) => RawValues<GradientStyle>.Of(style);
    public static string Title(this GradientStyle style) => Capitalized(style.RawValue());

    /// <summary>Swift's <c>String.capitalized</c> for one lower-case word.</summary>
    internal static string Capitalized(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}

[JsonConverter(typeof(GradientFillJsonConverter))]
public sealed record GradientFill
{
    public GradientFill(Vec2 start, Vec2 end, IEnumerable<GradientStop>? stops = null, GradientStyle style = GradientStyle.Linear,
                        bool reverse = false)
    {
        Stops = stops is null ? GradientStop.BlackToWhite : [.. stops];
        Style = style;
        Start = start;
        End = end;
        Reverse = reverse;
    }

    public ImmutableArray<GradientStop> Stops { get; init; }
    public GradientStyle Style { get; init; }
    /// <summary>Document pixels.</summary>
    public Vec2 Start { get; init; }
    public Vec2 End { get; init; }
    public bool Reverse { get; init; }

    public bool Equals(GradientFill? other) =>
        other is not null && Seq.Equal(Stops, other.Stops) && Style == other.Style && Start == other.Start && End == other.End
        && Reverse == other.Reverse;

    public override int GetHashCode() => HashCode.Combine(Seq.Hash(Stops), Style, Start, End, Reverse);
}

[JsonConverter(typeof(PatternFillJsonConverter))]
public sealed record PatternFill
{
    public PatternKind Kind { get; init; } = PatternKind.Checkerboard;
    /// <summary>Cell size in document pixels.</summary>
    public double Scale { get; init; } = 32;
    public SRGBColor Foreground { get; init; } = new(0.2, 0.2, 0.2);
    public SRGBColor Background { get; init; } = SRGBColor.White;
}

/// <summary>What a fill layer paints: one colour, a gradient or a pattern (Swift's <c>enum Fill</c>).</summary>
[JsonConverter(typeof(FillJsonConverter))]
public abstract record Fill
{
    private protected Fill() { }

    public sealed record Solid(SRGBColor Color) : Fill;
    public sealed record Gradient(GradientFill Value) : Fill;
    public sealed record Pattern(PatternFill Value) : Fill;

    public string Title => this switch
    {
        Solid => "Color Fill",
        Gradient => "Gradient Fill",
        Pattern => "Pattern Fill",
        _ => throw new InvalidOperationException(),
    };
}

// MARK: Smart objects

/// <summary>
/// Affine map from smart-object source pixels to document pixels: <c>doc = [a c; b d] · src + (tx, ty)</c>.
/// A 2D transform: affine, plus an optional projective row (<c>g</c>, <c>h</c>) for perspective/distort. With g = h = 0
/// it's an ordinary affine transform. Matrix (column vectors): x' = (a·x + c·y + tx) / w, y' = (b·x + d·y + ty) / w,
/// w = g·x + h·y + 1.
/// </summary>
[JsonConverter(typeof(AffineTransformJsonConverter))]
public sealed record AffineTransform
{
    public AffineTransform(double a = 1, double b = 0, double c = 0, double d = 1, double tx = 0, double ty = 0, double g = 0, double h = 0)
    {
        A = a; B = b; C = c; D = d; Tx = tx; Ty = ty; G = g; H = h;
    }

    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double D { get; init; }
    public double Tx { get; init; }
    public double Ty { get; init; }
    public double G { get; init; }
    public double H { get; init; }

    public bool IsAffine => Math.Abs(G) < 1e-15 && Math.Abs(H) < 1e-15;

    /// <summary>
    /// The projective transform taking the source rectangle (0,0)–size to the quadrilateral <paramref name="corners"/>
    /// (top-left, top-right, bottom-right, bottom-left).
    /// </summary>
    public static AffineTransform Quad(Vec2 sourceSize, IReadOnlyList<Vec2> corners)
    {
        if (corners.Count != 4) throw new ArgumentException("A quadrilateral has four corners.", nameof(corners));
        var (p0, p1, p2, p3) = (corners[0], corners[1], corners[2], corners[3]);
        // Unit square → quad (Heckbert).
        var s = p0 - p1 + p2 - p3;
        Vec2 d1 = p1 - p2, d2 = p3 - p2;
        double g = 0, h = 0;
        double den = d1.X * d2.Y - d2.X * d1.Y;
        if ((Math.Abs(s.X) > 1e-12 || Math.Abs(s.Y) > 1e-12) && Math.Abs(den) > 1e-12)
        {
            g = (s.X * d2.Y - d2.X * s.Y) / den;
            h = (d1.X * s.Y - s.X * d1.Y) / den;
        }
        double a = p1.X - p0.X + g * p1.X, b = p1.Y - p0.Y + g * p1.Y;
        double c = p3.X - p0.X + h * p3.X, d = p3.Y - p0.Y + h * p3.Y;
        // Pre-scale the unit square to the source size.
        double sx = 1 / Math.Max(sourceSize.X, 1e-12), sy = 1 / Math.Max(sourceSize.Y, 1e-12);
        return new AffineTransform(a * sx, b * sx, c * sy, d * sy, p0.X, p0.Y, g * sx, h * sy);
    }

    public static AffineTransform Identity { get; } = new();

    /// <summary>
    /// Scale, then rotate (radians, clockwise on screen) about the scaled box centre, with the unrotated box's
    /// top-left at (x, y). Matches the web app's <c>SmartTransform {tx, ty, sx, sy, rot}</c>.
    /// </summary>
    public static AffineTransform Make(double x, double y, double scaleX, double scaleY, double rotation, Vec2 sourceSize)
    {
        double cr = Math.Cos(rotation), sr = Math.Sin(rotation);
        double hw = sourceSize.X * scaleX / 2, hh = sourceSize.Y * scaleY / 2;
        // doc = centre + R · (S · src - halfSize)
        double cx = x + hw, cy = y + hh;
        double a = cr * scaleX, b = sr * scaleX, c = -sr * scaleY, d = cr * scaleY;
        double tx = cx - (cr * hw - sr * hh);
        double ty = cy - (sr * hw + cr * hh);
        return new AffineTransform(a, b, c, d, tx, ty);
    }

    /// <summary>Inverse of <see cref="Make"/>: the box position, scales and rotation for <paramref name="sourceSize"/>.</summary>
    public (double X, double Y, double ScaleX, double ScaleY, double Rotation) Decompose(Vec2 sourceSize)
    {
        double sx = Math.Sqrt(A * A + B * B);
        double rotation = Math.Atan2(B, A);
        double sy = sx > 1e-12 ? (A * D - B * C) / sx : 1;
        var centre = Apply(sourceSize / 2);
        double x = centre.X - sourceSize.X * sx / 2, y = centre.Y - sourceSize.Y * sy / 2;
        return (x, y, sx, sy, rotation);
    }

    public Vec2 Apply(Vec2 p)
    {
        double w = G * p.X + H * p.Y + 1;
        var q = new Vec2(A * p.X + C * p.Y + Tx, B * p.X + D * p.Y + Ty);
        return Math.Abs(w) > 1e-12 ? q / w : q;
    }

    public Double3x3 Matrix => new(new Vec3(A, B, G), new Vec3(C, D, H), new Vec3(Tx, Ty, 1));

    /// <summary>Swift's <c>init(matrix:)</c>: normalised so the bottom-right entry is 1.</summary>
    public static AffineTransform FromMatrix(Double3x3 m)
    {
        double k = Math.Abs(m.Column2.Z) > 1e-15 ? 1 / m.Column2.Z : 1;
        return new AffineTransform(m.Column0.X * k, m.Column0.Y * k, m.Column1.X * k, m.Column1.Y * k,
                                   m.Column2.X * k, m.Column2.Y * k, m.Column0.Z * k, m.Column1.Z * k);
    }

    public AffineTransform Inverse
    {
        get
        {
            var m = Matrix;
            if (!(Math.Abs(m.Determinant) > 1e-15)) return Identity;
            return FromMatrix(m.Inverse);
        }
    }

    /// <summary><c>self ∘ inner</c> (inner applied first).</summary>
    public AffineTransform Concatenating(AffineTransform inner) => FromMatrix(Matrix * inner.Matrix);

    public AffineTransform Translated(Vec2 offset) => new AffineTransform(1, 0, 0, 1, offset.X, offset.Y).Concatenating(this);

    /// <summary>Document-space bounding box of a source rectangle.</summary>
    public PixelRect Bounds(Vec2 sourceSize)
    {
        Span<Vec2> pts = [Apply(new Vec2(0, 0)), Apply(new Vec2(sourceSize.X, 0)), Apply(new Vec2(0, sourceSize.Y)), Apply(sourceSize)];
        Vec2 lo = pts[0], hi = pts[0];
        foreach (var p in pts)
        {
            lo = Vec2.Min(lo, p);
            hi = Vec2.Max(hi, p);
        }
        int x0 = (int)Math.Floor(lo.X), y0 = (int)Math.Floor(lo.Y);
        return new PixelRect(x0, y0, (int)Math.Ceiling(hi.X) - x0, (int)Math.Ceiling(hi.Y) - y0);
    }
}

/// <summary>A filter applied non-destructively to a smart object (after its transform).</summary>
[JsonConverter(typeof(SmartFilterJsonConverter))]
public sealed record SmartFilter
{
    public SmartFilter(FilterSettings settings, Guid? id = null, bool isEnabled = true)
    {
        Id = id ?? Guid.NewGuid();
        Settings = settings;
        IsEnabled = isEnabled;
    }

    public Guid Id { get; init; }
    public FilterSettings Settings { get; init; }
    public bool IsEnabled { get; init; }
}

public sealed record SmartObject
{
    public SmartObject(TileGrid source, int width, int height, AffineTransform transform, IEnumerable<SmartFilter>? filters = null)
    {
        Source = source;
        Width = width;
        Height = height;
        Transform = transform;
        Filters = filters is null ? [] : [.. filters];
    }

    /// <summary>Source pixels, top-left at the origin.</summary>
    public TileGrid Source { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public AffineTransform Transform { get; init; }
    /// <summary>Smart filters, applied in order on top of the transformed source.</summary>
    public ImmutableArray<SmartFilter> Filters { get; init; }

    public Vec2 SourceSize => new(Width, Height);
    public PixelRect Bounds => Transform.Bounds(SourceSize);

    public bool Equals(SmartObject? other) =>
        other is not null && Equals(Source, other.Source) && Width == other.Width && Height == other.Height
        && Transform == other.Transform && Seq.Equal(Filters, other.Filters);

    public override int GetHashCode() => HashCode.Combine(Source, Width, Height, Transform, Seq.Hash(Filters));
}

// MARK: Groups

public sealed record LayerGroup
{
    public LayerGroup(IEnumerable<Layer>? children = null, bool isExpanded = true)
    {
        Children = children is null ? [] : [.. children];
        IsExpanded = isExpanded;
    }

    /// <summary>Bottom → top.</summary>
    public ImmutableArray<Layer> Children { get; init; }
    public bool IsExpanded { get; init; }

    public bool Equals(LayerGroup? other) => other is not null && IsExpanded == other.IsExpanded && Seq.Equal(Children, other.Children);
    public override int GetHashCode() => HashCode.Combine(IsExpanded, Seq.Hash(Children));
}

// MARK: JSON (synthesized Codable: every key required on reading; nil optionals left out)

public sealed class ShadowEffectJsonConverter : JsonConverter<ShadowEffect>
{
    public override ShadowEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(ShadowEffect));
        return new ShadowEffect
        {
            IsEnabled = o.ReqBool("isEnabled"),
            Color = o.Req<SRGBColor>("color", options),
            Opacity = o.ReqDouble("opacity"),
            Angle = o.ReqDouble("angle"),
            Distance = o.ReqDouble("distance"),
            Size = o.ReqDouble("size"),
            Spread = o.ReqDouble("spread"),
            BlendMode = o.Req<BlendMode>("blendMode", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, ShadowEffect value, JsonSerializerOptions options) =>
        new JsonFields(options).Bool("isEnabled", value.IsEnabled).Value("color", value.Color).Double("opacity", value.Opacity)
            .Double("angle", value.Angle).Double("distance", value.Distance).Double("size", value.Size).Double("spread", value.Spread)
            .Value("blendMode", value.BlendMode).WriteTo(writer);
}

public sealed class GlowEffectJsonConverter : JsonConverter<GlowEffect>
{
    public override GlowEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(GlowEffect));
        return new GlowEffect
        {
            IsEnabled = o.ReqBool("isEnabled"),
            Color = o.Req<SRGBColor>("color", options),
            Opacity = o.ReqDouble("opacity"),
            Size = o.ReqDouble("size"),
            Spread = o.ReqDouble("spread"),
            BlendMode = o.Req<BlendMode>("blendMode", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, GlowEffect value, JsonSerializerOptions options) =>
        new JsonFields(options).Bool("isEnabled", value.IsEnabled).Value("color", value.Color).Double("opacity", value.Opacity)
            .Double("size", value.Size).Double("spread", value.Spread).Value("blendMode", value.BlendMode).WriteTo(writer);
}

public sealed class StrokeEffectJsonConverter : JsonConverter<StrokeEffect>
{
    public override StrokeEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(StrokeEffect));
        return new StrokeEffect
        {
            IsEnabled = o.ReqBool("isEnabled"),
            Color = o.Req<SRGBColor>("color", options),
            Opacity = o.ReqDouble("opacity"),
            Width = o.ReqDouble("width"),
            Position = o.Req<StrokePosition>("position", options),
            BlendMode = o.Req<BlendMode>("blendMode", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, StrokeEffect value, JsonSerializerOptions options) =>
        new JsonFields(options).Bool("isEnabled", value.IsEnabled).Value("color", value.Color).Double("opacity", value.Opacity)
            .Double("width", value.Width).Value("position", value.Position).Value("blendMode", value.BlendMode).WriteTo(writer);
}

public sealed class ColorOverlayEffectJsonConverter : JsonConverter<ColorOverlayEffect>
{
    public override ColorOverlayEffect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(ColorOverlayEffect));
        return new ColorOverlayEffect
        {
            IsEnabled = o.ReqBool("isEnabled"),
            Color = o.Req<SRGBColor>("color", options),
            Opacity = o.ReqDouble("opacity"),
            BlendMode = o.Req<BlendMode>("blendMode", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, ColorOverlayEffect value, JsonSerializerOptions options) =>
        new JsonFields(options).Bool("isEnabled", value.IsEnabled).Value("color", value.Color).Double("opacity", value.Opacity)
            .Value("blendMode", value.BlendMode).WriteTo(writer);
}

public sealed class LayerEffectsJsonConverter : JsonConverter<LayerEffects>
{
    public override LayerEffects Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(LayerEffects));
        return new LayerEffects
        {
            DropShadow = o.Opt<ShadowEffect>("dropShadow", options),
            InnerShadow = o.Opt<ShadowEffect>("innerShadow", options),
            OuterGlow = o.Opt<GlowEffect>("outerGlow", options),
            Stroke = o.Opt<StrokeEffect>("stroke", options),
            ColorOverlay = o.Opt<ColorOverlayEffect>("colorOverlay", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, LayerEffects value, JsonSerializerOptions options) =>
        new JsonFields(options).Value("dropShadow", value.DropShadow).Value("innerShadow", value.InnerShadow)
            .Value("outerGlow", value.OuterGlow).Value("stroke", value.Stroke).Value("colorOverlay", value.ColorOverlay).WriteTo(writer);
}

public sealed class GradientFillJsonConverter : JsonConverter<GradientFill>
{
    public override GradientFill Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(GradientFill));
        return new GradientFill(o.Req<Vec2>("start", options), o.Req<Vec2>("end", options),
                                o.ReqArray<GradientStop>("stops", options), o.Req<GradientStyle>("style", options), o.ReqBool("reverse"));
    }

    public override void Write(Utf8JsonWriter writer, GradientFill value, JsonSerializerOptions options) =>
        new JsonFields(options).Array("stops", value.Stops).Value("style", value.Style)
            .Value("start", value.Start).Value("end", value.End).Bool("reverse", value.Reverse).WriteTo(writer);
}

public sealed class PatternFillJsonConverter : JsonConverter<PatternFill>
{
    public override PatternFill Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(PatternFill));
        return new PatternFill
        {
            Kind = o.Req<PatternKind>("kind", options),
            Scale = o.ReqDouble("scale"),
            Foreground = o.Req<SRGBColor>("foreground", options),
            Background = o.Req<SRGBColor>("background", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, PatternFill value, JsonSerializerOptions options) =>
        new JsonFields(options).Value("kind", value.Kind).Double("scale", value.Scale).Value("foreground", value.Foreground)
            .Value("background", value.Background).WriteTo(writer);
}

/// <summary>
/// Swift's synthesized coding of an enum with associated values: one key naming the case, whose value holds the
/// unlabelled payload as <c>_0</c>: <c>{"solid":{"_0":{"r":1,"g":0,"b":0}}}</c>.
/// </summary>
public sealed class FillJsonConverter : JsonConverter<Fill>
{
    public override Fill Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(Fill));
        var props = o.EnumerateObject().ToList();
        if (props.Count != 1) throw new JsonException("Invalid number of keys found, expected one.");
        var payload = SwiftJson.ExpectObject(props[0].Value, props[0].Name);
        return props[0].Name switch
        {
            "solid" => new Fill.Solid(payload.Req<SRGBColor>("_0", options)),
            "gradient" => new Fill.Gradient(payload.Req<GradientFill>("_0", options)),
            "pattern" => new Fill.Pattern(payload.Req<PatternFill>("_0", options)),
            var other => throw new JsonException($"Cannot initialize Fill from invalid case {other}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Fill value, JsonSerializerOptions options)
    {
        var (name, payload) = value switch
        {
            Fill.Solid s => ("solid", new JsonFields(options).Value("_0", s.Color)),
            Fill.Gradient g => ("gradient", new JsonFields(options).Value("_0", g.Value)),
            Fill.Pattern p => ("pattern", new JsonFields(options).Value("_0", p.Value)),
            _ => throw new JsonException("Unknown fill."),
        };
        new JsonFields(options).Custom(name, payload.WriteTo).WriteTo(writer);
    }
}

/// <summary>The Mac's custom coding: <c>g</c> and <c>h</c> are optional when reading and left out for affine transforms.</summary>
public sealed class AffineTransformJsonConverter : JsonConverter<AffineTransform>
{
    public override AffineTransform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(AffineTransform));
        return new AffineTransform(o.ReqDouble("a"), o.ReqDouble("b"), o.ReqDouble("c"), o.ReqDouble("d"),
                                   o.ReqDouble("tx"), o.ReqDouble("ty"), o.OptDouble("g") ?? 0, o.OptDouble("h") ?? 0);
    }

    public override void Write(Utf8JsonWriter writer, AffineTransform value, JsonSerializerOptions options)
    {
        var f = new JsonFields(options).Double("a", value.A).Double("b", value.B).Double("c", value.C).Double("d", value.D)
            .Double("tx", value.Tx).Double("ty", value.Ty);
        if (!value.IsAffine) f.Double("g", value.G).Double("h", value.H);
        f.WriteTo(writer);
    }
}

public sealed class SmartFilterJsonConverter : JsonConverter<SmartFilter>
{
    public override SmartFilter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(SmartFilter));
        return new SmartFilter(o.Req<FilterSettings>("settings", options), o.ReqUuid("id"), o.ReqBool("isEnabled"));
    }

    public override void Write(Utf8JsonWriter writer, SmartFilter value, JsonSerializerOptions options) =>
        new JsonFields(options).Uuid("id", value.Id).Value("settings", value.Settings).Bool("isEnabled", value.IsEnabled).WriteTo(writer);
}
