// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Adjustments.swift

using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>Straight (non-premultiplied) sRGB colour, components 0...1.</summary>
[JsonConverter(typeof(SRGBColorJsonConverter))]
public readonly record struct SRGBColor(double R, double G, double B)
{
    public static SRGBColor Black { get; } = new(0, 0, 0);
    public static SRGBColor White { get; } = new(1, 1, 1);

    /// <summary>Linear-light components.</summary>
    public Vector3 Linear => new((float)ColorMath.SrgbToLinear(R), (float)ColorMath.SrgbToLinear(G), (float)ColorMath.SrgbToLinear(B));

    public Vector3 Srgb => new((float)R, (float)G, (float)B);
}

/// <summary>A closed range of numbers (Swift's <c>ClosedRange&lt;Double&gt;</c>).</summary>
public readonly record struct ClosedRange(double LowerBound, double UpperBound)
{
    public bool Contains(double value) => value >= LowerBound && value <= UpperBound;
    public double Clamp(double value) => Math.Min(Math.Max(value, LowerBound), UpperBound);
}

[JsonConverter(typeof(CurvePointJsonConverter))]
public readonly record struct CurvePoint(double X, double Y);

/// <summary>Monotone cubic (Fritsch–Carlson) curve through sorted control points.</summary>
public static class CurveMath
{
    public static ImmutableArray<CurvePoint> Identity { get; } = [new CurvePoint(0, 0), new CurvePoint(1, 1)];

    /// <summary>
    /// A natural cubic spline through the points (second derivative zero at both ends), flat outside them and clamped
    /// to 0…1.
    /// </summary>
    public static double EvaluateNatural(IReadOnlyList<CurvePoint> points, double t)
    {
        var pts = points.OrderBy(p => p.X).ToArray();
        if (pts.Length == 0) return t;
        var first = pts[0];
        var last = pts[^1];
        if (pts.Length == 1 || t <= first.X) return Clamp(first.Y);
        if (t >= last.X) return Clamp(last.Y);
        int n = pts.Length;
        if (n == 2) return Clamp(first.Y + (last.Y - first.Y) * (t - first.X) / (last.X - first.X));
        // Solve for the second derivatives (tridiagonal system).
        var u = new double[n];
        var y2 = new double[n];
        for (int i = 1; i < n - 1; i++)
        {
            double sig = (pts[i].X - pts[i - 1].X) / (pts[i + 1].X - pts[i - 1].X);
            double p = sig * y2[i - 1] + 2;
            y2[i] = (sig - 1) / p;
            double d = (pts[i + 1].Y - pts[i].Y) / (pts[i + 1].X - pts[i].X) - (pts[i].Y - pts[i - 1].Y) / (pts[i].X - pts[i - 1].X);
            u[i] = (6 * d / (pts[i + 1].X - pts[i - 1].X) - sig * u[i - 1]) / p;
        }
        y2[n - 1] = 0;
        for (int i = n - 2; i >= 0; i--) y2[i] = y2[i] * y2[i + 1] + u[i];
        int k = 0;
        while (k < n - 2 && t > pts[k + 1].X) k++;
        double h = pts[k + 1].X - pts[k].X;
        if (!(h > 1e-9)) return Clamp(pts[k].Y);
        double a = (pts[k + 1].X - t) / h, b = (t - pts[k].X) / h;
        return Clamp(a * pts[k].Y + b * pts[k + 1].Y + ((a * a * a - a) * y2[k] + (b * b * b - b) * y2[k + 1]) * h * h / 6);
    }

    /// <summary>
    /// The curve through the points: Glimmer's own (monotone, never overshoots), or the natural spline that curves
    /// from Photoshop files are drawn with.
    /// </summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> points, double t, bool natural) =>
        natural ? EvaluateNatural(points, t) : Evaluate(points, t);

    public static double Evaluate(IReadOnlyList<CurvePoint> points, double t)
    {
        var pts = points.OrderBy(p => p.X).ToArray();
        if (pts.Length == 0) return t;
        var first = pts[0];
        var last = pts[^1];
        if (pts.Length == 1) return Clamp(first.Y);
        if (t <= first.X) return Clamp(first.Y);
        if (t >= last.X) return Clamp(last.Y);

        int n = pts.Length;
        var delta = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = pts[i + 1].X - pts[i].X;
            delta[i] = dx > 1e-9 ? (pts[i + 1].Y - pts[i].Y) / dx : 0;
        }
        var m = new double[n];
        m[0] = delta[0];
        m[n - 1] = delta[n - 2];
        for (int i = 1; i < n - 1; i++)
            m[i] = delta[i - 1] * delta[i] <= 0 ? 0 : (delta[i - 1] + delta[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (delta[i] != 0) continue;
            m[i] = 0;
            m[i + 1] = 0;
        }
        for (int i = 0; i < n - 1; i++)
        {
            if (delta[i] == 0) continue;
            double a = m[i] / delta[i], b = m[i + 1] / delta[i];
            double s = a * a + b * b;
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[i] = tau * a * delta[i];
                m[i + 1] = tau * b * delta[i];
            }
        }

        int k = 0;
        while (k < n - 2 && t > pts[k + 1].X) k++;
        double h = pts[k + 1].X - pts[k].X;
        if (!(h > 1e-9)) return Clamp(pts[k].Y);
        double u = (t - pts[k].X) / h;
        double u2 = u * u, u3 = u2 * u;
        double y = (2 * u3 - 3 * u2 + 1) * pts[k].Y + (u3 - 2 * u2 + u) * h * m[k]
                   + (-2 * u3 + 3 * u2) * pts[k + 1].Y + (u3 - u2) * h * m[k + 1];
        return Clamp(y);
    }

    internal static double Clamp(double v) => Math.Min(Math.Max(v, 0), 1);
}

[JsonConverter(typeof(LevelsChannelJsonConverter))]
public sealed record LevelsChannel(double InBlack = 0, double InWhite = 1, double Gamma = 1, double OutBlack = 0, double OutWhite = 1)
{
    public static LevelsChannel Identity { get; } = new();

    public double Apply(double t)
    {
        double span = Math.Max(1e-4, InWhite - InBlack);
        double v = Math.Min(Math.Max((t - InBlack) / span, 0), 1);
        v = Math.Pow(v, 1 / Math.Max(0.01, Gamma));
        return Math.Min(Math.Max(OutBlack + v * (OutWhite - OutBlack), 0), 1);
    }
}

[JsonConverter(typeof(GradientStopJsonConverter))]
public readonly record struct GradientStop(double Position, SRGBColor Color, double Opacity = 1)
{
    public static ImmutableArray<GradientStop> BlackToWhite { get; } = [new(0, SRGBColor.Black), new(1, SRGBColor.White)];

    /// <summary>Straight sRGB colour and opacity at <paramref name="t"/> (linear interpolation in sRGB, like Photoshop).</summary>
    public static (SRGBColor Color, double Opacity) Sample(IReadOnlyList<GradientStop> stops, double t)
    {
        var s = stops.OrderBy(x => x.Position).ToArray();
        if (s.Length == 0) return (SRGBColor.Black, 1);
        var first = s[0];
        var last = s[^1];
        if (t <= first.Position) return (first.Color, first.Opacity);
        if (t >= last.Position) return (last.Color, last.Opacity);
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (!(t <= s[i + 1].Position)) continue;
            var a = s[i];
            var b = s[i + 1];
            double f = (t - a.Position) / Math.Max(1e-9, b.Position - a.Position);
            var c = new SRGBColor(a.Color.R + (b.Color.R - a.Color.R) * f,
                                  a.Color.G + (b.Color.G - a.Color.G) * f,
                                  a.Color.B + (b.Color.B - a.Color.B) * f);
            return (c, a.Opacity + (b.Opacity - a.Opacity) * f);
        }
        return (last.Color, last.Opacity);
    }
}

/// <summary>The 14 adjustment layer types. Raw values match Glimmer for the web.</summary>
[JsonConverter(typeof(RawValueEnumConverter<AdjustmentKind>))]
public enum AdjustmentKind
{
    [RawValue("brightness_contrast")] BrightnessContrast,
    [RawValue("levels")] Levels,
    [RawValue("curves")] Curves,
    [RawValue("exposure")] Exposure,
    [RawValue("hue_saturation")] HueSaturation,
    [RawValue("vibrance")] Vibrance,
    [RawValue("color_balance")] ColorBalance,
    [RawValue("black_white")] BlackWhite,
    [RawValue("photo_filter")] PhotoFilter,
    [RawValue("channel_mixer")] ChannelMixer,
    [RawValue("invert")] Invert,
    [RawValue("posterize")] Posterize,
    [RawValue("threshold")] Threshold,
    [RawValue("gradient_map")] GradientMap,
}

public sealed record AdjustmentSlider(string Key, string Label, ClosedRange Range, double DefaultValue = 0, double Step = 0.01);

public sealed record AdjustmentToggle(string Key, string Label, bool DefaultValue);

public sealed record AdjustmentColor(string Key, string Label, SRGBColor DefaultValue);

public static class AdjustmentKindExtensions
{
    extension(AdjustmentKind)
    {
        /// <summary>Every kind, in declaration order.</summary>
        public static IReadOnlyList<AdjustmentKind> AllCases => RawValues<AdjustmentKind>.AllCases;

        /// <summary>Swift's <c>AdjustmentKind(rawValue:)</c>.</summary>
        public static AdjustmentKind? FromRawValue(string? raw) => RawValues<AdjustmentKind>.Parse(raw);
    }

    public static string RawValue(this AdjustmentKind kind) => RawValues<AdjustmentKind>.Of(kind);

    public static string Title(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.BrightnessContrast => "Brightness/Contrast",
        AdjustmentKind.Levels => "Levels",
        AdjustmentKind.Curves => "Curves",
        AdjustmentKind.Exposure => "Exposure",
        AdjustmentKind.HueSaturation => "Hue/Saturation",
        AdjustmentKind.Vibrance => "Vibrance",
        AdjustmentKind.ColorBalance => "Color Balance",
        AdjustmentKind.BlackWhite => "Black & White",
        AdjustmentKind.PhotoFilter => "Photo Filter",
        AdjustmentKind.ChannelMixer => "Channel Mixer",
        AdjustmentKind.Invert => "Invert",
        AdjustmentKind.Posterize => "Posterize",
        AdjustmentKind.Threshold => "Threshold",
        AdjustmentKind.GradientMap => "Gradient Map",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The SF Symbol the Mac shows for the kind (the Windows app maps these names to its own icons).</summary>
    public static string SymbolName(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.BrightnessContrast => "sun.max",
        AdjustmentKind.Levels => "chart.bar",
        AdjustmentKind.Curves => "point.topleft.down.to.point.bottomright.curvepath",
        AdjustmentKind.Exposure => "plusminus.circle",
        AdjustmentKind.HueSaturation => "paintpalette",
        AdjustmentKind.Vibrance => "drop.triangle",
        AdjustmentKind.ColorBalance => "scalemass",
        AdjustmentKind.BlackWhite => "circle.lefthalf.filled",
        AdjustmentKind.PhotoFilter => "camera.filters",
        AdjustmentKind.ChannelMixer => "slider.horizontal.3",
        AdjustmentKind.Invert => "circle.righthalf.filled.inverse",
        AdjustmentKind.Posterize => "square.stack.3d.down.right",
        AdjustmentKind.Threshold => "circle.and.line.horizontal",
        AdjustmentKind.GradientMap => "rectangle.righthalf.inset.filled",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Order the shader's <c>adjustColor</c> switch uses (declaration order). Must match the CPU kernels.</summary>
    public static int ShaderIndex(this AdjustmentKind kind) => (int)kind;

    /// <summary>Whether the effect is computed through a lookup table.</summary>
    public static bool UsesLUT(this AdjustmentKind kind) =>
        kind is AdjustmentKind.Levels or AdjustmentKind.Curves or AdjustmentKind.GradientMap;

    private static readonly Dictionary<AdjustmentKind, ImmutableArray<AdjustmentSlider>> SliderTable = BuildSliders();

    private static Dictionary<AdjustmentKind, ImmutableArray<AdjustmentSlider>> BuildSliders()
    {
        static AdjustmentSlider S(string key, string label, double lo, double hi, double def = 0, double step = 0.01) =>
            new(key, label, new ClosedRange(lo, hi), def, step);

        var colorBalance = new List<AdjustmentSlider>();
        foreach (var tone in new[] { "shadows", "midtones", "highlights" })
        {
            foreach (var (c, label) in new[] { ("R", "Cyan – Red"), ("G", "Magenta – Green"), ("B", "Yellow – Blue") })
                colorBalance.Add(S($"{tone}{c}", $"{char.ToUpperInvariant(tone[0])}{tone[1..]}: {label}", -1, 1));
        }
        var mixer = new List<AdjustmentSlider>();
        foreach (var output in new[] { "r", "g", "b" })
        {
            foreach (var (input, label) in new[] { ("r", "Red"), ("g", "Green"), ("b", "Blue"), ("c", "Constant") })
                mixer.Add(S($"{output}{input}", $"{output.ToUpperInvariant()} ← {label}", -2, 2, output == input ? 1 : 0));
        }
        return new Dictionary<AdjustmentKind, ImmutableArray<AdjustmentSlider>>
        {
            [AdjustmentKind.BrightnessContrast] = [S("brightness", "Brightness", -1, 1), S("contrast", "Contrast", -1, 1)],
            [AdjustmentKind.Exposure] = [S("exposure", "Exposure", -5, 5), S("offset", "Offset", -0.5, 0.5),
                                         S("gamma", "Gamma Correction", 0.1, 3, def: 1)],
            [AdjustmentKind.HueSaturation] = [S("hue", "Hue", -180, 180, step: 1), S("saturation", "Saturation", -1, 1),
                                              S("lightness", "Lightness", -1, 1)],
            [AdjustmentKind.Vibrance] = [S("vibrance", "Vibrance", -1, 1), S("saturation", "Saturation", -1, 1)],
            [AdjustmentKind.ColorBalance] = [.. colorBalance],
            [AdjustmentKind.BlackWhite] = [S("red", "Reds", -2, 3, def: 0.4), S("yellow", "Yellows", -2, 3, def: 0.6),
                                           S("green", "Greens", -2, 3, def: 0.4), S("cyan", "Cyans", -2, 3, def: 0.6),
                                           S("blue", "Blues", -2, 3, def: 0.2), S("magenta", "Magentas", -2, 3, def: 0.8)],
            [AdjustmentKind.PhotoFilter] = [S("density", "Density", 0, 1, def: 0.25)],
            [AdjustmentKind.ChannelMixer] = [.. mixer],
            [AdjustmentKind.Posterize] = [S("levels", "Levels", 2, 255, def: 4, step: 1)],
            [AdjustmentKind.Threshold] = [S("level", "Threshold Level", 0, 1, def: 0.5)],
            [AdjustmentKind.Levels] = [],
            [AdjustmentKind.Curves] = [],
            [AdjustmentKind.Invert] = [],
            [AdjustmentKind.GradientMap] = [],
        };
    }

    public static ImmutableArray<AdjustmentSlider> Sliders(this AdjustmentKind kind) => SliderTable[kind];

    public static ImmutableArray<AdjustmentToggle> Toggles(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.ColorBalance or AdjustmentKind.PhotoFilter => [new AdjustmentToggle("preserveLuminosity", "Preserve Luminosity", true)],
        AdjustmentKind.ChannelMixer => [new AdjustmentToggle("monochrome", "Monochrome", false)],
        _ => [],
    };

    public static ImmutableArray<AdjustmentColor> Colors(this AdjustmentKind kind) => kind switch
    {
        AdjustmentKind.PhotoFilter => [new AdjustmentColor("color", "Filter Color", new SRGBColor(0.92, 0.6, 0.2))],
        _ => [],
    };
}

[JsonConverter(typeof(RawValueEnumConverter<CurveChannel>))]
public enum CurveChannel
{
    [RawValue("rgb")] Rgb,
    [RawValue("red")] Red,
    [RawValue("green")] Green,
    [RawValue("blue")] Blue,
}

public static class CurveChannelExtensions
{
    extension(CurveChannel)
    {
        public static IReadOnlyList<CurveChannel> AllCases => RawValues<CurveChannel>.AllCases;
        public static CurveChannel? FromRawValue(string? raw) => RawValues<CurveChannel>.Parse(raw);
    }

    public static string RawValue(this CurveChannel channel) => RawValues<CurveChannel>.Of(channel);

    public static string Title(this CurveChannel channel) => channel switch
    {
        CurveChannel.Rgb => "RGB",
        CurveChannel.Red => "Red",
        CurveChannel.Green => "Green",
        CurveChannel.Blue => "Blue",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}

/// <summary>
/// An adjustment's parameters. Values missing from the bags fall back to the kind's schema defaults, so decoding is
/// lenient.
/// </summary>
[JsonConverter(typeof(AdjustmentJsonConverter))]
public sealed partial record Adjustment
{
    public Adjustment(AdjustmentKind kind)
    {
        Kind = kind;
        Gradient = GradientStop.BlackToWhite;
    }

    public AdjustmentKind Kind { get; init; }
    public ImmutableDictionary<string, double> Numbers { get; init; } = ImmutableDictionary<string, double>.Empty;
    public ImmutableDictionary<string, bool> Flags { get; init; } = ImmutableDictionary<string, bool>.Empty;
    public ImmutableDictionary<string, SRGBColor> Colors { get; init; } = ImmutableDictionary<string, SRGBColor>.Empty;
    /// <summary>Curves: per channel.</summary>
    public ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>> Curves { get; init; } =
        ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>.Empty;
    /// <summary>Levels: per channel.</summary>
    public ImmutableDictionary<CurveChannel, LevelsChannel> Levels { get; init; } = ImmutableDictionary<CurveChannel, LevelsChannel>.Empty;
    /// <summary>Gradient Map.</summary>
    public ImmutableArray<GradientStop> Gradient { get; init; }

    public double Number(string key)
    {
        if (Numbers.TryGetValue(key, out var v)) return v;
        foreach (var s in Kind.Sliders()) if (s.Key == key) return s.DefaultValue;
        return 0;
    }

    public bool Flag(string key)
    {
        if (Flags.TryGetValue(key, out var v)) return v;
        foreach (var t in Kind.Toggles()) if (t.Key == key) return t.DefaultValue;
        return false;
    }

    public SRGBColor Color(string key)
    {
        if (Colors.TryGetValue(key, out var v)) return v;
        foreach (var c in Kind.Colors()) if (c.Key == key) return c.DefaultValue;
        return SRGBColor.White;
    }

    public ImmutableArray<CurvePoint> Curve(CurveChannel channel) =>
        Curves.TryGetValue(channel, out var points) ? points : CurveMath.Identity;

    /// <summary>Swift's <c>levelsChannel(_:)</c> (renamed: a method can't share its return type's name here).</summary>
    public LevelsChannel LevelsChannelFor(CurveChannel channel) =>
        Levels.TryGetValue(channel, out var levels) ? levels : LevelsChannel.Identity;

    /// <summary>
    /// Curves from a Photoshop file run between their points as that program draws them (a natural cubic spline):
    /// measured on a file from Photoshop on 2026-11-16, where Glimmer's own curve through the same points differed
    /// visibly and this one matched.
    /// </summary>
    public const string NaturalCurveFlag = "naturalSpline";

    public bool UsesNaturalCurve => Flags.TryGetValue(NaturalCurveFlag, out var v) && v;

    /// <summary>
    /// A per-channel lookup table (sRGB-encoded in and out) for LUT-based kinds: <paramref name="size"/> entries of
    /// (r, g, b, a).
    /// </summary>
    public Vector4[] LookupTable(int size = 1024)
    {
        var result = new Vector4[size];
        var rgb = Curve(CurveChannel.Rgb);
        var red = Curve(CurveChannel.Red);
        var green = Curve(CurveChannel.Green);
        var blue = Curve(CurveChannel.Blue);
        bool natural = UsesNaturalCurve;
        var lm = LevelsChannelFor(CurveChannel.Rgb);
        var lr = LevelsChannelFor(CurveChannel.Red);
        var lg = LevelsChannelFor(CurveChannel.Green);
        var lb = LevelsChannelFor(CurveChannel.Blue);
        for (int i = 0; i < size; i++)
        {
            double t = (double)i / (size - 1);
            switch (Kind)
            {
                case AdjustmentKind.Curves:
                    float Value(ImmutableArray<CurvePoint> channel) =>
                        (float)CurveMath.Evaluate(rgb, CurveMath.Evaluate(channel, t, natural), natural);
                    result[i] = new Vector4(Value(red), Value(green), Value(blue), 1);
                    break;
                case AdjustmentKind.Levels:
                    result[i] = new Vector4((float)lm.Apply(lr.Apply(t)), (float)lm.Apply(lg.Apply(t)), (float)lm.Apply(lb.Apply(t)), 1);
                    break;
                case AdjustmentKind.GradientMap:
                    var (c, o) = GradientStop.Sample(Gradient, t);
                    result[i] = new Vector4((float)c.R, (float)c.G, (float)c.B, (float)o);
                    break;
                default:
                    result[i] = new Vector4((float)t, (float)t, (float)t, 1);
                    break;
            }
        }
        return result;
    }

    /// <summary>Scalar parameters packed for the adjustment kernels (layouts as in the Mac's Adjust.metal).</summary>
    public float[] ShaderParameters
    {
        get
        {
            float N(string k) => (float)Number(k);
            float F(string k) => Flag(k) ? 1 : 0;
            switch (Kind)
            {
                case AdjustmentKind.BrightnessContrast: return [N("brightness"), N("contrast")];
                case AdjustmentKind.Exposure: return [N("exposure"), N("offset"), N("gamma")];
                case AdjustmentKind.HueSaturation: return [N("hue") * MathF.PI / 180, N("saturation"), N("lightness")];
                case AdjustmentKind.Vibrance: return [N("vibrance"), N("saturation")];
                case AdjustmentKind.ColorBalance:
                {
                    var list = new List<float>();
                    foreach (var t in new[] { "shadows", "midtones", "highlights" })
                        foreach (var c in new[] { "R", "G", "B" }) list.Add(N(t + c));
                    list.Add(F("preserveLuminosity"));
                    return [.. list];
                }
                case AdjustmentKind.BlackWhite:
                    return [.. new[] { "red", "yellow", "green", "cyan", "blue", "magenta" }.Select(N)];
                case AdjustmentKind.PhotoFilter:
                {
                    var c = Color("color");
                    return [(float)c.R, (float)c.G, (float)c.B, N("density"), F("preserveLuminosity")];
                }
                case AdjustmentKind.ChannelMixer:
                    return [.. new[] { "rr", "rg", "rb", "rc", "gr", "gg", "gb", "gc", "br", "bg", "bb", "bc" }.Select(N), F("monochrome")];
                case AdjustmentKind.Posterize: return [MathF.Round(N("levels"), MidpointRounding.AwayFromZero)];
                case AdjustmentKind.Threshold: return [N("level")];
                default: return [];
            }
        }
    }

    public bool Equals(Adjustment? other) =>
        other is not null && Kind == other.Kind
        && Seq.Equal(Numbers, other.Numbers) && Seq.Equal(Flags, other.Flags) && Seq.Equal(Colors, other.Colors)
        && Seq.Equal(Curves, other.Curves, (a, b) => Seq.Equal(a, b))
        && Seq.Equal(Levels, other.Levels)
        && Seq.Equal(Gradient, other.Gradient);

    public override int GetHashCode()
    {
        // The curve points take part too (the renderer keys its caches on adjustments): channels in a fixed order,
        // each with its points, so equal adjustments hash equal and different curves don't all collide.
        var curves = new HashCode();
        foreach (var (channel, points) in Curves.OrderBy(kv => kv.Key))
        {
            curves.Add(channel);
            curves.Add(Seq.Hash(points));
        }
        return HashCode.Combine(Kind, Seq.Hash(Numbers), Seq.Hash(Flags), Seq.Hash(Colors), curves.ToHashCode(),
            Seq.Hash(Levels), Seq.Hash(Gradient));
    }
}

// MARK: JSON

public sealed class SRGBColorJsonConverter : JsonConverter<SRGBColor>
{
    public override SRGBColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(SRGBColor));
        return new SRGBColor(o.ReqDouble("r"), o.ReqDouble("g"), o.ReqDouble("b"));
    }

    public override void Write(Utf8JsonWriter writer, SRGBColor value, JsonSerializerOptions options) =>
        new JsonFields(options).Double("r", value.R).Double("g", value.G).Double("b", value.B).WriteTo(writer);
}

public sealed class CurvePointJsonConverter : JsonConverter<CurvePoint>
{
    public override CurvePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(CurvePoint));
        return new CurvePoint(o.ReqDouble("x"), o.ReqDouble("y"));
    }

    public override void Write(Utf8JsonWriter writer, CurvePoint value, JsonSerializerOptions options) =>
        new JsonFields(options).Double("x", value.X).Double("y", value.Y).WriteTo(writer);
}

/// <summary>Synthesized <c>Codable</c>: every key is required, the property defaults notwithstanding.</summary>
public sealed class LevelsChannelJsonConverter : JsonConverter<LevelsChannel>
{
    public override LevelsChannel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(LevelsChannel));
        return new LevelsChannel(o.ReqDouble("inBlack"), o.ReqDouble("inWhite"), o.ReqDouble("gamma"),
                                 o.ReqDouble("outBlack"), o.ReqDouble("outWhite"));
    }

    public override void Write(Utf8JsonWriter writer, LevelsChannel value, JsonSerializerOptions options) =>
        new JsonFields(options).Double("inBlack", value.InBlack).Double("inWhite", value.InWhite).Double("gamma", value.Gamma)
            .Double("outBlack", value.OutBlack).Double("outWhite", value.OutWhite).WriteTo(writer);
}

public sealed class GradientStopJsonConverter : JsonConverter<GradientStop>
{
    public override GradientStop Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(GradientStop));
        return new GradientStop(o.ReqDouble("position"), o.Req<SRGBColor>("color", options), o.ReqDouble("opacity"));
    }

    public override void Write(Utf8JsonWriter writer, GradientStop value, JsonSerializerOptions options) =>
        new JsonFields(options).Double("position", value.Position).Value("color", value.Color).Double("opacity", value.Opacity).WriteTo(writer);
}

/// <summary>
/// The Mac's custom <c>init(from:)</c> (only <c>kind</c> is required) and its synthesized <c>encode(to:)</c>. The
/// per-channel dictionaries have an enum key, so Swift writes them as flat arrays: <c>["red", [...], "rgb", [...]]</c>.
/// </summary>
public sealed class AdjustmentJsonConverter : JsonConverter<Adjustment>
{
    public override Adjustment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(Adjustment));
        var kind = o.Req<AdjustmentKind>("kind", options);
        var a = new Adjustment(kind);
        if (o.TryGet("numbers", out var numbers))
            a = a with { Numbers = SwiftJson.StringDictionary(numbers, "numbers", v => v.AsDouble("numbers")) };
        if (o.TryGet("flags", out var flags))
            a = a with { Flags = SwiftJson.StringDictionary(flags, "flags", v => v.AsBool("flags")) };
        if (o.TryGet("colors", out var colors))
            a = a with { Colors = SwiftJson.StringDictionary(colors, "colors", v => v.As<SRGBColor>(options, "colors")) };
        if (o.TryGet("curves", out var curves))
            a = a with { Curves = ChannelDictionary(curves, "curves", v => SwiftJson.ArrayOf<CurvePoint>(v, options, "curves")) };
        if (o.TryGet("levels", out var levels))
            a = a with { Levels = ChannelDictionary(levels, "levels", v => v.As<LevelsChannel>(options, "levels")) };
        if (o.TryGet("gradient", out var gradient))
            a = a with { Gradient = SwiftJson.ArrayOf<GradientStop>(gradient, options, "gradient") };
        return a;
    }

    /// <summary>
    /// A <c>[CurveChannel: V]</c> dictionary: Swift's flat <c>[key, value, key, value]</c> array. An object keyed by
    /// the channel names is accepted too (Swift wouldn't, but nothing is lost by reading it).
    /// </summary>
    internal static ImmutableDictionary<CurveChannel, TValue> ChannelDictionary<TValue>(JsonElement e, string key, Func<JsonElement, TValue> readValue)
    {
        var b = ImmutableDictionary.CreateBuilder<CurveChannel, TValue>();
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                var channel = RawValues<CurveChannel>.Parse(p.Name)
                              ?? throw new JsonException($"Cannot initialize CurveChannel from invalid String value {p.Name}");
                b[channel] = readValue(p.Value);
            }
            return b.ToImmutable();
        }
        if (e.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Expected to decode Array for key \"{key}\" but found {SwiftJson.Describe(e)} instead.");
        if (e.GetArrayLength() % 2 != 0)
            throw new JsonException($"Expected collection of key-value pairs for key \"{key}\"; encountered odd-length array instead.");
        using var items = e.EnumerateArray();
        while (items.MoveNext())
        {
            var name = items.Current.AsString(key);
            var channel = RawValues<CurveChannel>.Parse(name)
                          ?? throw new JsonException($"Cannot initialize CurveChannel from invalid String value {name}");
            items.MoveNext();
            b[channel] = readValue(items.Current);
        }
        return b.ToImmutable();
    }

    internal static void WriteChannelDictionary<TValue>(Utf8JsonWriter writer, ImmutableDictionary<CurveChannel, TValue> dict,
                                                        Action<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartArray();
        foreach (var channel in RawValues<CurveChannel>.AllCases)
        {
            if (!dict.TryGetValue(channel, out var value)) continue;
            writer.WriteStringValue(channel.RawValue());
            writeValue(writer, value);
        }
        writer.WriteEndArray();
    }

    public override void Write(Utf8JsonWriter writer, Adjustment value, JsonSerializerOptions options)
    {
        new JsonFields(options)
            .Value("kind", value.Kind)
            .Custom("numbers", w => SwiftJson.WriteStringDictionary(w, value.Numbers, SwiftJson.WriteDouble))
            .Custom("flags", w => SwiftJson.WriteStringDictionary(w, value.Flags, (ww, b) => ww.WriteBooleanValue(b)))
            .Custom("colors", w => SwiftJson.WriteStringDictionary(w, value.Colors, (ww, c) => JsonSerializer.Serialize(ww, c, options)))
            .Custom("curves", w => WriteChannelDictionary(w, value.Curves, (ww, pts) =>
            {
                ww.WriteStartArray();
                foreach (var p in pts) JsonSerializer.Serialize(ww, p, options);
                ww.WriteEndArray();
            }))
            .Custom("levels", w => WriteChannelDictionary(w, value.Levels, (ww, l) => JsonSerializer.Serialize(ww, l, options)))
            .Array("gradient", value.Gradient)
            .WriteTo(writer);
    }
}
