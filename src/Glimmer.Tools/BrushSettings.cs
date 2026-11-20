// Ported from Packages/GlimmerKit/Sources/GlimmerTools/BrushSettings.swift
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Tools;

/// <summary>Brush parameters (mirrors the web app's <c>BrushParams</c>).</summary>
/// <remarks>
/// JSON is the Mac's <c>Codable</c> shape: an object with every property under its camelCase name. As there, reading
/// needs every key (a missing one is an error) and ignores keys it doesn't know.
/// </remarks>
[JsonConverter(typeof(BrushSettingsJsonConverter))]
public sealed record BrushSettings
{
    /// <summary>Diameter in document pixels.</summary>
    public double Size { get; init; } = 30;
    /// <summary>0 = soft, 1 = hard.</summary>
    public double Hardness { get; init; } = 0.8;
    /// <summary>Caps the stroke (0...1).</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Paint laid down per dab; builds up within a stroke (0...1).</summary>
    public double Flow { get; init; } = 1;
    /// <summary>Dab spacing as a fraction of the diameter.</summary>
    public double Spacing { get; init; } = 0.1;
    public double Roundness { get; init; } = 1;
    /// <summary>Degrees.</summary>
    public double Angle { get; init; } = 0;
    /// <summary>Random offset as a fraction of the diameter.</summary>
    public double Scatter { get; init; } = 0;
    /// <summary>Random size variation 0...1.</summary>
    public double SizeJitter { get; init; } = 0;
    public bool Textured { get; init; } = false;
    public bool PressureSize { get; init; } = true;
    public bool PressureOpacity { get; init; } = false;
    /// <summary>The dab's long axis follows the direction the pen leans.</summary>
    public bool TiltAngle { get; init; } = false;
    /// <summary>The dab flattens as the pen leans over (like the side of a pencil).</summary>
    public bool TiltRoundness { get; init; } = false;
    /// <summary>Barrel rotation of the pen turns the dab.</summary>
    public bool RotationAngle { get; init; } = false;

    public static IReadOnlyList<(string Name, BrushSettings Settings)> Presets { get; } =
    [
        ("Soft Round", new BrushSettings { Hardness = 0, Size = 60 }),
        ("Hard Round", new BrushSettings { Hardness = 1, Size = 20 }),
        ("Chalk", new BrushSettings { Hardness = 0.6, Textured = true, Size = 40, Flow = 0.8 }),
        ("Calligraphic", new BrushSettings { Hardness = 0.95, Roundness = 0.25, Angle = 45, Size = 30 }),
        ("Airbrush", new BrushSettings { Hardness = 0, Flow = 0.08, Size = 120, Spacing = 0.05 }),
        ("Pencil", new BrushSettings { Hardness = 1, Size = 2, Spacing = 0.05 }),
    ];
}

/// <summary>Reads and writes <see cref="BrushSettings"/> as the Mac's synthesized <c>Codable</c> does.</summary>
public sealed class BrushSettingsJsonConverter : JsonConverter<BrushSettings>
{
    private static readonly string[] NumberKeys = ["size", "hardness", "opacity", "flow", "spacing", "roundness", "angle", "scatter", "sizeJitter"];
    private static readonly string[] FlagKeys = ["textured", "pressureSize", "pressureOpacity", "tiltAngle", "tiltRoundness", "rotationAngle"];

    public override BrushSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var o = document.RootElement;
        if (o.ValueKind != JsonValueKind.Object) throw new JsonException("BrushSettings: expected an object.");
        double N(string key)
        {
            if (!o.TryGetProperty(key, out var v)) throw new JsonException($"BrushSettings: missing key \"{key}\".");
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d)) throw new JsonException($"BrushSettings: \"{key}\" is not a number.");
            return d;
        }
        bool F(string key)
        {
            if (!o.TryGetProperty(key, out var v)) throw new JsonException($"BrushSettings: missing key \"{key}\".");
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new JsonException($"BrushSettings: \"{key}\" is not a Boolean."),
            };
        }
        return new BrushSettings
        {
            Size = N("size"), Hardness = N("hardness"), Opacity = N("opacity"), Flow = N("flow"), Spacing = N("spacing"),
            Roundness = N("roundness"), Angle = N("angle"), Scatter = N("scatter"), SizeJitter = N("sizeJitter"),
            Textured = F("textured"), PressureSize = F("pressureSize"), PressureOpacity = F("pressureOpacity"),
            TiltAngle = F("tiltAngle"), TiltRoundness = F("tiltRoundness"), RotationAngle = F("rotationAngle"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BrushSettings value, JsonSerializerOptions options)
    {
        double[] numbers = [value.Size, value.Hardness, value.Opacity, value.Flow, value.Spacing, value.Roundness, value.Angle,
                            value.Scatter, value.SizeJitter];
        bool[] flags = [value.Textured, value.PressureSize, value.PressureOpacity, value.TiltAngle, value.TiltRoundness,
                        value.RotationAngle];
        writer.WriteStartObject();
        for (int i = 0; i < NumberKeys.Length; i++) writer.WriteNumber(NumberKeys[i], numbers[i]);
        for (int i = 0; i < FlagKeys.Length; i++) writer.WriteBoolean(FlagKeys[i], flags[i]);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Turns pointer samples into evenly spaced dabs (with pressure, jitter, scatter), carrying the remaining distance
/// between events.
/// </summary>
public sealed class StrokeSampler
{
    /// <param name="Position">Document pixels.</param>
    /// <param name="Pressure">0...1 (1 for a mouse).</param>
    /// <param name="Tilt">Pen tilt, -1...1 on each axis (document orientation, y down); zero for a mouse.</param>
    /// <param name="Rotation">Pen barrel rotation in degrees.</param>
    public readonly record struct Sample(Vec2 Position, double Pressure = 1, Vec2 Tilt = default, double Rotation = 0);

    public BrushSettings Settings { get; set; }
    private Sample? last;
    private double carry;
    private readonly Random rng;

    /// <param name="random">The source of jitter and scatter (the system generator when null).</param>
    public StrokeSampler(BrushSettings settings, Random? random = null)
    {
        Settings = settings;
        rng = random ?? Random.Shared;
    }

    public List<DabInstance> Begin(Sample s)
    {
        last = s;
        carry = 0;
        return [Dab(s)];
    }

    /// <summary>Swift's <c>move(to:)</c>.</summary>
    public List<DabInstance> Move(Sample s)
    {
        if (last is not { } previous) return Begin(s);
        Vec2 delta = s.Position - previous.Position;
        double length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        double step = Math.Max(0.5, Settings.Spacing * Settings.Size);
        var dabs = new List<DabInstance>();
        double travelled = step - carry;
        while (travelled <= length)
        {
            double t = travelled / Math.Max(length, 1e-9);
            Vec2 p = previous.Position + delta * t;
            var sample = new Sample(p, previous.Pressure + (s.Pressure - previous.Pressure) * t,
                                    previous.Tilt + (s.Tilt - previous.Tilt) * t,
                                    previous.Rotation + (s.Rotation - previous.Rotation) * t);
            dabs.Add(Dab(sample));
            travelled += step;
        }
        carry = length - (travelled - step);
        last = s;
        return dabs;
    }

    internal DabInstance Dab(Sample s)
    {
        Vec2 p = s.Position;
        double pressure = s.Pressure;
        double size = Settings.Size * (Settings.PressureSize ? 0.15 + 0.85 * pressure : 1);
        if (Settings.SizeJitter > 0) size *= 1 - Settings.SizeJitter * rng.NextDouble();
        Vec2 position = p;
        if (Settings.Scatter > 0)
        {
            double a = rng.NextDouble() * 2 * Math.PI;
            double r = Settings.Scatter * Settings.Size * rng.NextDouble();
            position += new Vec2(Math.Cos(a), Math.Sin(a)) * r;
        }
        double flow = Settings.Flow * (Settings.PressureOpacity ? 0.1 + 0.9 * pressure : 1);
        var (angle, roundness) = Shape(Settings, s);
        return new DabInstance(new Vector2((float)position.X, (float)position.Y), (float)(Math.Max(size, 0.5) / 2),
                               (float)Settings.Hardness, (float)roundness, (float)(angle * Math.PI / 180), (float)flow,
                               Settings.Textured);
    }

    /// <summary>Dab angle (degrees) and roundness after tilt and rotation dynamics.</summary>
    public static (double Angle, double Roundness) Shape(BrushSettings settings, Sample s)
    {
        double angle = settings.Angle, roundness = settings.Roundness;
        double lean = Math.Min(Math.Sqrt(s.Tilt.X * s.Tilt.X + s.Tilt.Y * s.Tilt.Y), 1);
        if (settings.TiltAngle && lean > 0.05) angle = Math.Atan2(s.Tilt.Y, s.Tilt.X) * 180 / Math.PI;
        if (settings.RotationAngle) angle += s.Rotation;
        if (settings.TiltRoundness) roundness *= Math.Max(0.2, 1 - 0.8 * lean);
        return (angle, roundness);
    }
}
