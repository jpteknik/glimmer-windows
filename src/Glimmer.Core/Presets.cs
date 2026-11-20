// Ported from Packages/GlimmerKit/Sources/GlimmerCore/Presets.swift, and the AipsImporter.adjustment(_:_:) it calls
// (Packages/GlimmerKit/Sources/GlimmerCore/AipsImporter.swift), which is here as Adjustment.FromWebParameters so the
// presets and the .aips importer share one implementation.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glimmer.Core;

/// <summary>A step of a preset: an adjustment layer, or a filter applied once.</summary>
public abstract record PresetStep
{
    private protected PresetStep() { }

    public sealed record AdjustmentStep(Adjustment Adjustment) : PresetStep;

    public sealed record FilterStep(string Name, ImmutableDictionary<string, double> Parameters) : PresetStep
    {
        public bool Equals(FilterStep? other) => other is not null && Name == other.Name && Seq.Equal(Parameters, other.Parameters);
        public override int GetHashCode() => HashCode.Combine(Name, Seq.Hash(Parameters));
    }
}

/// <summary>
/// One-click looks, ported from the web app (<c>web/src/ai/presets/presetDefs.ts</c>). Each becomes a group of
/// adjustment layers; filter steps are applied once filters exist and are skipped until then.
/// </summary>
public sealed record Preset
{
    public Preset(string id, string name, string description, (string, string) swatch, IEnumerable<PresetStep> steps)
    {
        Id = id;
        Name = name;
        Description = description;
        Swatch = swatch;
        Steps = [.. steps];
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>Two sRGB hex swatches for the UI.</summary>
    public (string, string) Swatch { get; }
    public ImmutableArray<PresetStep> Steps { get; }

    /// <summary>The preset as a group of adjustment layers (bottom → top), with fresh ids each time.</summary>
    public Layer MakeGroup()
    {
        var children = Steps.OfType<PresetStep.AdjustmentStep>()
            .Select(s => new Layer(s.Adjustment.Kind.Title(), new AdjustmentContent(s.Adjustment)));
        return new Layer(Name, new GroupContent(new LayerGroup(children, isExpanded: false)), blendMode: BlendMode.PassThrough);
    }

    public IReadOnlyList<(string Name, ImmutableDictionary<string, double> Parameters)> FilterSteps =>
        [.. Steps.OfType<PresetStep.FilterStep>().Select(f => (f.Name, f.Parameters))];

    public bool Equals(Preset? other) =>
        other is not null && Id == other.Id && Name == other.Name && Description == other.Description && Swatch == other.Swatch
        && Seq.Equal(Steps, other.Steps);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Description, Swatch, Seq.Hash(Steps));

    // MARK: The presets

    /// <summary>Web-style parameters (same shapes as <c>.aips</c> files) → an adjustment.</summary>
    private static PresetStep Adj(AdjustmentKind kind, JsonObject? parameters = null) =>
        new PresetStep.AdjustmentStep(Adjustment.FromWebParameters(kind, parameters ?? new JsonObject()));

    private static PresetStep Filt(string name, params (string Key, double Value)[] parameters) =>
        new PresetStep.FilterStep(name, parameters.ToImmutableDictionary(p => p.Key, p => p.Value));

    private static JsonArray Pts(params (double X, double Y)[] p) => [.. p.Select(q => (JsonNode)new JsonObject { ["x"] = q.X, ["y"] = q.Y })];
    private static JsonObject Rgba(double r, double g, double b) => new() { ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = 1 };
    private static JsonArray Tone(double r, double g, double b) => [r, g, b];

    public static ImmutableArray<Preset> All { get; } =
    [
        new("teal_orange", "Cinematic Teal & Orange",
            "Hollywood blockbuster split-tone: warm skin, teal shadows.", ("#0e4d5c", "#e8954b"),
        [
            Adj(AdjustmentKind.Curves, new() { ["rgb"] = Pts((0, 0.02), (0.25, 0.2), (0.75, 0.82), (1, 0.98)) }),
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(-0.18, 0.02, 0.24), ["midtones"] = Tone(0.04, 0.0, -0.04),
                ["highlights"] = Tone(0.2, 0.06, -0.22), ["preserveLuminosity"] = true,
            }),
            Adj(AdjustmentKind.Vibrance, new() { ["vibrance"] = 0.22, ["saturation"] = 0.05 }),
            Filt("clarity", ("amount", 0.25)),
        ]),
        new("vintage_film", "Vintage Film",
            "Faded 70s stock: lifted blacks, warm cast, fine grain.", ("#6b5b3e", "#d8c9a3"),
        [
            Adj(AdjustmentKind.Levels, new() { ["inBlack"] = 0, ["inWhite"] = 1, ["gamma"] = 1.05, ["outBlack"] = 0.08, ["outWhite"] = 0.94 }),
            Adj(AdjustmentKind.PhotoFilter, new() { ["color"] = Rgba(0.85, 0.55, 0.22), ["density"] = 0.3, ["preserveLuminosity"] = true }),
            Adj(AdjustmentKind.HueSaturation, new() { ["hue"] = 0, ["saturation"] = -0.18, ["lightness"] = 0.02 }),
            Adj(AdjustmentKind.Curves, new() { ["r"] = Pts((0, 0.06), (1, 0.96)), ["b"] = Pts((0, 0.04), (1, 0.9)) }),
            Filt("add_noise", ("amount", 0.07), ("monochrome", 1)),
        ]),
        new("bw_noir", "B&W Noir",
            "High-contrast black & white with deep shadows and grain.", ("#0a0a0a", "#e6e6e6"),
        [
            Adj(AdjustmentKind.BlackWhite, new() { ["red"] = 0.6, ["yellow"] = 0.85, ["green"] = 0.4, ["cyan"] = 0.3, ["blue"] = 0.15, ["magenta"] = 0.7 }),
            Adj(AdjustmentKind.Curves, new() { ["rgb"] = Pts((0, 0), (0.22, 0.1), (0.78, 0.92), (1, 1)) }),
            Filt("vignette", ("amount", 0.55), ("midpoint", 0.4), ("roundness", 0.4), ("feather", 0.5)),
            Filt("add_noise", ("amount", 0.1), ("monochrome", 1)),
        ]),
        new("hdr_pop", "HDR Pop",
            "Punchy detail and saturation — clarity-driven, modern look.", ("#1a6fb0", "#f2c94c"),
        [
            Filt("clarity", ("amount", 0.7)),
            Filt("sharpen", ("amount", 0.6)),
            Adj(AdjustmentKind.Curves, new() { ["rgb"] = Pts((0, 0.04), (0.35, 0.42), (0.7, 0.74), (1, 0.99)) }),
            Adj(AdjustmentKind.Vibrance, new() { ["vibrance"] = 0.4, ["saturation"] = 0.12 }),
        ]),
        new("golden_hour", "Golden Hour",
            "Warm, glowing sunset light with soft, lifted shadows.", ("#c8722a", "#ffe2a8"),
        [
            Adj(AdjustmentKind.PhotoFilter, new() { ["color"] = Rgba(0.98, 0.66, 0.25), ["density"] = 0.35, ["preserveLuminosity"] = true }),
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(0.05, 0.0, -0.04), ["midtones"] = Tone(0.08, 0.02, -0.08),
                ["highlights"] = Tone(0.16, 0.06, -0.16), ["preserveLuminosity"] = true,
            }),
            Adj(AdjustmentKind.Levels, new() { ["inBlack"] = 0, ["inWhite"] = 1, ["gamma"] = 1.08, ["outBlack"] = 0.06, ["outWhite"] = 1 }),
            Adj(AdjustmentKind.Vibrance, new() { ["vibrance"] = 0.2, ["saturation"] = 0.04 }),
        ]),
        new("cyberpunk", "Cool Cyberpunk",
            "Neon night city: magenta highlights, electric-blue shadows.", ("#1b1040", "#ff2e9a"),
        [
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(-0.1, -0.06, 0.3), ["midtones"] = Tone(0.06, -0.08, 0.12),
                ["highlights"] = Tone(0.24, -0.05, 0.1), ["preserveLuminosity"] = true,
            }),
            Adj(AdjustmentKind.Curves, new() { ["r"] = Pts((0, 0), (0.7, 0.78), (1, 1)), ["b"] = Pts((0, 0.12), (0.5, 0.58), (1, 1)) }),
            Adj(AdjustmentKind.HueSaturation, new() { ["hue"] = 0, ["saturation"] = 0.3, ["lightness"] = -0.02 }),
            Filt("chromatic_aberration", ("amount", 4), ("edgeOnly", 1)),
            Filt("vignette", ("amount", 0.4), ("midpoint", 0.45), ("roundness", 0.6), ("feather", 0.6)),
        ]),
        new("matte_fade", "Matte Fade",
            "Modern Instagram matte: flattened blacks, muted color.", ("#4a4f57", "#cdbfb2"),
        [
            Adj(AdjustmentKind.Levels, new() { ["inBlack"] = 0, ["inWhite"] = 0.96, ["gamma"] = 1.0, ["outBlack"] = 0.12, ["outWhite"] = 0.95 }),
            Adj(AdjustmentKind.Curves, new() { ["rgb"] = Pts((0, 0.12), (0.5, 0.5), (1, 0.94)) }),
            Adj(AdjustmentKind.Vibrance, new() { ["vibrance"] = -0.12, ["saturation"] = -0.16 }),
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(-0.04, 0.0, 0.08), ["midtones"] = Tone(0, 0, 0),
                ["highlights"] = Tone(0.04, 0.02, -0.04), ["preserveLuminosity"] = true,
            }),
        ]),
        new("bleach_bypass", "Bleach Bypass",
            "Desaturated, high-contrast silver-retention war-film look.", ("#2b2b2b", "#bfc4c0"),
        [
            Adj(AdjustmentKind.HueSaturation, new() { ["hue"] = 0, ["saturation"] = -0.55, ["lightness"] = 0 }),
            Adj(AdjustmentKind.Curves, new() { ["rgb"] = Pts((0, 0), (0.25, 0.14), (0.75, 0.9), (1, 1)) }),
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(-0.04, 0.04, 0.0), ["midtones"] = Tone(0.0, 0.06, -0.02),
                ["highlights"] = Tone(0.02, 0.04, -0.04), ["preserveLuminosity"] = true,
            }),
            Filt("clarity", ("amount", 0.45)),
            Filt("add_noise", ("amount", 0.05), ("monochrome", 1)),
        ]),
        new("cross_process", "Cross Process",
            "C-41-in-E6 color shift: green shadows, yellow highlights.", ("#3c5a2e", "#e8d24a"),
        [
            Adj(AdjustmentKind.Curves, new()
            {
                ["r"] = Pts((0, 0), (0.5, 0.58), (1, 0.95)), ["g"] = Pts((0, 0.06), (0.5, 0.5), (1, 1)),
                ["b"] = Pts((0, 0.14), (0.5, 0.42), (1, 0.85)),
            }),
            Adj(AdjustmentKind.ColorBalance, new()
            {
                ["shadows"] = Tone(-0.12, 0.16, -0.06), ["midtones"] = Tone(0.04, 0.06, -0.1),
                ["highlights"] = Tone(0.06, 0.04, -0.18), ["preserveLuminosity"] = false,
            }),
            Adj(AdjustmentKind.HueSaturation, new() { ["hue"] = 0, ["saturation"] = 0.28, ["lightness"] = 0 }),
        ]),
        new("dreamy_soft", "Dreamy Soft",
            "Hazy pastel glow — soft contrast, airy highlights, fine bloom.", ("#d8a7c4", "#fbe9d0"),
        [
            Adj(AdjustmentKind.Levels, new() { ["inBlack"] = 0, ["inWhite"] = 1, ["gamma"] = 1.12, ["outBlack"] = 0.1, ["outWhite"] = 1 }),
            Adj(AdjustmentKind.Vibrance, new() { ["vibrance"] = -0.06, ["saturation"] = -0.1 }),
            Adj(AdjustmentKind.PhotoFilter, new() { ["color"] = Rgba(0.95, 0.7, 0.78), ["density"] = 0.18, ["preserveLuminosity"] = true }),
            Adj(AdjustmentKind.GradientMap, new()
            {
                ["stops"] = new JsonArray(
                    new JsonObject { ["pos"] = 0.0, ["color"] = Rgba(0.22, 0.12, 0.26) },
                    new JsonObject { ["pos"] = 0.5, ["color"] = Rgba(0.78, 0.6, 0.62) },
                    new JsonObject { ["pos"] = 1.0, ["color"] = Rgba(0.99, 0.94, 0.86) }),
            }),
            Filt("lens_blur", ("radius", 3), ("brightnessBoost", 0.6)),
        ]),
    ];
}

public sealed partial record Adjustment
{
    /// <summary>
    /// An adjustment from Glimmer for the web's parameter bag (the <c>params</c> of an <c>.aips</c> adjustment node, and
    /// the presets): numbers go to <see cref="Numbers"/>, booleans to <see cref="Flags"/>, and the kinds with structured
    /// parameters read theirs (colour balance tones, photo filter colour, curve points, levels, gradient stops).
    /// Ported from <c>AipsImporter.adjustment(_:_:)</c>; the <c>.aips</c> importer uses this one.
    /// </summary>
    public static Adjustment FromWebParameters(AdjustmentKind kind, JsonObject parameters)
    {
        var a = new Adjustment(kind);
        var numbers = a.Numbers.ToBuilder();
        var flags = a.Flags.ToBuilder();
        foreach (var (key, value) in parameters)
        {
            if (value is not JsonValue v) continue;
            switch (v.GetValueKind())
            {
                case JsonValueKind.True: flags[key] = true; break;
                case JsonValueKind.False: flags[key] = false; break;
                case JsonValueKind.Number: numbers[key] = NumberOf(v); break;
            }
        }
        var colors = a.Colors.ToBuilder();
        var curves = a.Curves.ToBuilder();
        var levels = a.Levels.ToBuilder();
        var gradient = a.Gradient;
        switch (kind)
        {
            case AdjustmentKind.ColorBalance:
                foreach (var tone in new[] { "shadows", "midtones", "highlights" })
                {
                    if (parameters[tone] is not JsonArray v || v.Count < 3) continue;
                    string[] channels = ["R", "G", "B"];
                    for (int i = 0; i < 3; i++) numbers[tone + channels[i]] = WebDouble(v[i], 0);
                }
                break;
            case AdjustmentKind.PhotoFilter:
                if (parameters["color"] is JsonObject c) colors["color"] = WebColor(c);
                break;
            case AdjustmentKind.Curves:
                foreach (var (key, channel) in new[] { ("rgb", CurveChannel.Rgb), ("r", CurveChannel.Red), ("g", CurveChannel.Green), ("b", CurveChannel.Blue) })
                {
                    if (parameters[key] is not JsonArray pts || !pts.All(p => p is JsonObject)) continue;
                    curves[channel] = [.. pts.Select(p => new CurvePoint(WebDouble(p!["x"], 0), WebDouble(p["y"], 0)))];
                }
                break;
            case AdjustmentKind.Levels:
                levels[CurveChannel.Rgb] = new LevelsChannel(WebDouble(parameters["inBlack"], 0), WebDouble(parameters["inWhite"], 1),
                                                             WebDouble(parameters["gamma"], 1), WebDouble(parameters["outBlack"], 0),
                                                             WebDouble(parameters["outWhite"], 1));
                break;
            case AdjustmentKind.GradientMap:
                if (parameters["stops"] is JsonArray stops && stops.Count >= 2 && stops.All(s => s is JsonObject))
                {
                    gradient = [.. stops.Select(s =>
                    {
                        var color = s!["color"] as JsonObject ?? new JsonObject();
                        return new GradientStop(WebDouble(s["pos"], 0), WebColor(color), WebDouble(color["a"], 1));
                    })];
                }
                break;
        }
        return a with
        {
            Numbers = numbers.ToImmutable(),
            Flags = flags.ToImmutable(),
            Colors = colors.ToImmutable(),
            Curves = curves.ToImmutable(),
            Levels = levels.ToImmutable(),
            Gradient = gradient,
        };
    }

    /// <summary>The web's <c>{r, g, b}</c> colour (missing components are 0).</summary>
    internal static SRGBColor WebColor(JsonObject c) => new(WebDouble(c["r"], 0), WebDouble(c["g"], 0), WebDouble(c["b"], 0));

    /// <summary>A number from a parameter bag, or <paramref name="fallback"/>. A boolean counts as 1 or 0, as an NSNumber does on the Mac.</summary>
    internal static double WebDouble(JsonNode? node, double fallback)
    {
        if (node is not JsonValue v) return fallback;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => NumberOf(v),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => fallback,
        };
    }

    /// <summary>A JSON number as a double, whatever CLR type the node was made from.</summary>
    private static double NumberOf(JsonValue v)
    {
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out decimal m)) return (double)m;
        return double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
