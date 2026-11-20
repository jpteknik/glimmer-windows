// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Filters.swift

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// The destructive pixel filters (Filter menu). Raw values match the web app's filter ids so presets and agent tool
/// calls carry over unchanged.
/// </summary>
[JsonConverter(typeof(RawValueEnumConverter<FilterKind>))]
public enum FilterKind
{
    [RawValue("gaussian_blur")] GaussianBlur,
    [RawValue("lens_blur")] LensBlur,
    [RawValue("motion_blur")] MotionBlur,
    [RawValue("sharpen")] Sharpen,
    [RawValue("unsharp_mask")] UnsharpMask,
    [RawValue("clarity")] Clarity,
    [RawValue("add_noise")] AddNoise,
    [RawValue("pixelate")] Pixelate,
    [RawValue("find_edges")] FindEdges,
    [RawValue("emboss")] Emboss,
    [RawValue("oil_paint")] OilPaint,
    [RawValue("halftone")] Halftone,
    [RawValue("vignette")] Vignette,
    [RawValue("chromatic_aberration")] ChromaticAberration,
}

public sealed record FilterField(string Key, string Label, ClosedRange Range, double DefaultValue, bool IsToggle, string Unit)
{
    public static FilterField Slider(string key, string label, double lowerBound, double upperBound, double defaultValue, string unit = "") =>
        new(key, label, new ClosedRange(lowerBound, upperBound), defaultValue, false, unit);

    public static FilterField Toggle(string key, string label, bool defaultValue) =>
        new(key, label, new ClosedRange(0, 1), defaultValue ? 1 : 0, true, "");
}

public static class FilterKindExtensions
{
    extension(FilterKind)
    {
        public static IReadOnlyList<FilterKind> AllCases => RawValues<FilterKind>.AllCases;
        public static FilterKind? FromRawValue(string? raw) => RawValues<FilterKind>.Parse(raw);
    }

    public static string RawValue(this FilterKind kind) => RawValues<FilterKind>.Of(kind);

    public static string Title(this FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur => "Gaussian Blur",
        FilterKind.LensBlur => "Lens Blur",
        FilterKind.MotionBlur => "Motion Blur",
        FilterKind.Sharpen => "Sharpen",
        FilterKind.UnsharpMask => "Unsharp Mask",
        FilterKind.Clarity => "Clarity",
        FilterKind.AddNoise => "Add Noise",
        FilterKind.Pixelate => "Pixelate",
        FilterKind.FindEdges => "Find Edges",
        FilterKind.Emboss => "Emboss",
        FilterKind.OilPaint => "Oil Paint",
        FilterKind.Halftone => "Halftone",
        FilterKind.Vignette => "Vignette",
        FilterKind.ChromaticAberration => "Chromatic Aberration",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Filter menu submenu.</summary>
    public static string Group(this FilterKind kind) => kind switch
    {
        FilterKind.GaussianBlur or FilterKind.LensBlur or FilterKind.MotionBlur => "Blur",
        FilterKind.Sharpen or FilterKind.UnsharpMask or FilterKind.Clarity => "Sharpen",
        FilterKind.AddNoise => "Noise",
        FilterKind.Pixelate or FilterKind.Halftone => "Pixelate",
        FilterKind.FindEdges or FilterKind.Emboss or FilterKind.OilPaint => "Stylize",
        FilterKind.Vignette or FilterKind.ChromaticAberration => "Lens",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static readonly Dictionary<FilterKind, ImmutableArray<FilterField>> FieldTable = new()
    {
        [FilterKind.GaussianBlur] = [FilterField.Slider("radius", "Radius", 0, 100, 4, unit: "px")],
        [FilterKind.LensBlur] = [FilterField.Slider("radius", "Radius", 0, 60, 8, unit: "px"),
                                 FilterField.Slider("brightnessBoost", "Highlight Bloom", 0, 1, 0.3)],
        [FilterKind.MotionBlur] = [FilterField.Slider("angle", "Angle", -180, 180, 0, unit: "°"),
                                   FilterField.Slider("distance", "Distance", 0, 200, 20, unit: "px")],
        [FilterKind.Sharpen] = [FilterField.Slider("amount", "Amount", 0, 5, 1)],
        [FilterKind.UnsharpMask] = [FilterField.Slider("amount", "Amount", 0, 5, 0.8),
                                    FilterField.Slider("radius", "Radius", 0.5, 10, 2, unit: "px"),
                                    FilterField.Slider("threshold", "Threshold", 0, 1, 0)],
        [FilterKind.Clarity] = [FilterField.Slider("amount", "Amount", -1, 1, 0.4)],
        [FilterKind.AddNoise] = [FilterField.Slider("amount", "Amount", 0, 1, 0.1), FilterField.Toggle("monochrome", "Monochromatic", false)],
        [FilterKind.Pixelate] = [FilterField.Slider("cell", "Cell Size", 2, 200, 10, unit: "px")],
        [FilterKind.FindEdges] = [],
        [FilterKind.Emboss] = [FilterField.Slider("angle", "Angle", -180, 180, 135, unit: "°"), FilterField.Slider("amount", "Amount", 0, 5, 1)],
        [FilterKind.OilPaint] = [FilterField.Slider("radius", "Radius", 1, 10, 4, unit: "px"), FilterField.Slider("intensity", "Intensity", 0, 1, 1)],
        [FilterKind.Halftone] = [FilterField.Slider("cellSize", "Cell Size", 2, 40, 8, unit: "px"), FilterField.Slider("angle", "Angle", 0, 90, 45, unit: "°"),
                                 FilterField.Toggle("monochrome", "Monochrome", true)],
        [FilterKind.Vignette] = [FilterField.Slider("amount", "Amount", 0, 1, 0.5), FilterField.Slider("midpoint", "Midpoint", 0, 1, 0.5),
                                 FilterField.Slider("roundness", "Roundness", 0, 1, 0.5), FilterField.Slider("feather", "Feather", 0.01, 1, 0.5)],
        [FilterKind.ChromaticAberration] = [FilterField.Slider("amount", "Amount", 0, 40, 6, unit: "px"),
                                            FilterField.Toggle("edgeOnly", "Stronger at Edges", true)],
    };

    public static ImmutableArray<FilterField> Fields(this FilterKind kind) => FieldTable[kind];
}

/// <summary>
/// A filter with its parameters. Toggles are stored as 0/1 so the web's <c>{monochrome: 1}</c> style parameter bags
/// decode directly.
/// </summary>
[JsonConverter(typeof(FilterSettingsJsonConverter))]
public sealed record FilterSettings
{
    public FilterSettings(FilterKind kind, IEnumerable<KeyValuePair<string, double>>? values = null)
    {
        Kind = kind;
        Values = values is null ? ImmutableDictionary<string, double>.Empty : ImmutableDictionary.CreateRange(values);
    }

    public FilterKind Kind { get; init; }
    public ImmutableDictionary<string, double> Values { get; init; }

    /// <summary>Parses a web-style id (<c>"add_noise"</c>) and parameter bag; null for an unknown id.</summary>
    public static FilterSettings? FromId(string id, IEnumerable<KeyValuePair<string, double>>? values = null) =>
        FilterKind.FromRawValue(id) is { } kind ? new FilterSettings(kind, values) : null;

    public double Value(string key)
    {
        if (Values.TryGetValue(key, out var v)) return v;
        foreach (var f in Kind.Fields()) if (f.Key == key) return f.DefaultValue;
        return 0;
    }

    public bool Flag(string key) => Value(key) >= 0.5;

    /// <summary>How far (px) the filter reads from neighbouring pixels.</summary>
    public int Reach
    {
        get
        {
            double r = Kind switch
            {
                FilterKind.GaussianBlur => Value("radius") * 1.5,
                FilterKind.LensBlur => Value("radius"),
                FilterKind.MotionBlur => Value("distance") / 2,
                FilterKind.Sharpen or FilterKind.FindEdges => 1,
                FilterKind.UnsharpMask => Value("radius") * 3,
                FilterKind.Clarity => 80,
                FilterKind.Emboss => 2,
                FilterKind.OilPaint => Value("radius"),
                FilterKind.ChromaticAberration => Value("amount"),
                FilterKind.Pixelate => Value("cell"),
                _ => 0,   // addNoise, halftone, vignette
            };
            return (int)Math.Ceiling(r) + 1;
        }
    }

    public bool Equals(FilterSettings? other) => other is not null && Kind == other.Kind && Seq.Equal(Values, other.Values);
    public override int GetHashCode() => HashCode.Combine(Kind, Seq.Hash(Values));
}

public sealed class FilterSettingsJsonConverter : JsonConverter<FilterSettings>
{
    public override FilterSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(FilterSettings));
        var kind = o.Req<FilterKind>("kind", options);
        var values = SwiftJson.StringDictionary(o.Req("values"), "values", v => v.AsDouble("values"));
        return new FilterSettings(kind, values);
    }

    public override void Write(Utf8JsonWriter writer, FilterSettings value, JsonSerializerOptions options) =>
        new JsonFields(options)
            .Value("kind", value.Kind)
            .Custom("values", w => SwiftJson.WriteStringDictionary(w, value.Values, SwiftJson.WriteDouble))
            .WriteTo(writer);
}
