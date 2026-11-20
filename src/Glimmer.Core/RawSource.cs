// Ported from Packages/GlimmerKit/Sources/GlimmerCore/RawSource.swift

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// How a RAW file is developed. Saved with the layer, so the same file gives the same picture again. Values left null
/// are the decoder's own for that file.
/// </summary>
[JsonConverter(typeof(RawSettingsJsonConverter))]
public sealed record RawSettings
{
    /// <summary>In stops, added to the file's own exposure.</summary>
    public double Exposure { get; init; } = 0;
    /// <summary>White balance in kelvin and tint; null is as the camera shot it.</summary>
    public double? Temperature { get; init; }
    public double? Tint { get; init; }
    /// <summary>0…2; 1 leaves the shadows as they are, more lifts them.</summary>
    public double Shadows { get; init; } = 1;
    public double? Contrast { get; init; }
    public bool HighlightRecovery { get; init; } = true;
    public double? LuminanceNoise { get; init; }
    public double? ColorNoise { get; init; }
    public double? Sharpness { get; init; }
    public bool? LensCorrection { get; init; }
    /// <summary>
    /// The decoder version the picture was developed with ("9", "9.dng" …). Kept, so that a newer system doesn't change
    /// an old document's look; null until the first development.
    /// </summary>
    public string? Process { get; init; }
}

/// <summary>
/// The RAW file a layer was developed from, kept with the layer: the file's bytes as they were, how they were
/// developed, and which pixels that gave. The layer's pixels are the developed picture, a cache that can always be
/// made again from the bytes and the settings. (These types are in every build, so a document keeps its RAW layers
/// whatever the build can do; developing is behind GLIMMER_RAW.)
/// </summary>
public sealed record RawSource
{
    public RawSource(byte[] data, string fileName, RawSettings settings, long? developedSignature = null)
    {
        Data = data;
        FileName = fileName;
        Settings = settings;
        DevelopedSignature = developedSignature;
    }

    /// <summary>The RAW file, unchanged.</summary>
    public byte[] Data { get; init; }
    /// <summary>The file's name when it was opened, for showing and for saving it out again.</summary>
    public string FileName { get; init; }
    public RawSettings Settings { get; init; }
    /// <summary>
    /// The signature of the layer's pixels as they were developed (<see cref="TileGrid.ContentSignature"/>). If the
    /// layer's pixels no longer have it, something was painted or pasted on it since, and developing again replaces that.
    /// </summary>
    public long? DevelopedSignature { get; init; }

    /// <summary>The most a kept RAW file may be: larger files are not kept in a document.</summary>
    public const int MaxBytes = 512 << 20;

    public bool Equals(RawSource? other) =>
        other is not null && FileName == other.FileName && Settings == other.Settings && DevelopedSignature == other.DevelopedSignature
        && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => HashCode.Combine(FileName, Settings, DevelopedSignature, Data.Length);
}

/// <summary>Synthesized <c>Codable</c>: <c>exposure</c>, <c>shadows</c> and <c>highlightRecovery</c> are required.</summary>
public sealed class RawSettingsJsonConverter : JsonConverter<RawSettings>
{
    public override RawSettings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(RawSettings));
        return new RawSettings
        {
            Exposure = o.ReqDouble("exposure"),
            Temperature = o.OptDouble("temperature"),
            Tint = o.OptDouble("tint"),
            Shadows = o.ReqDouble("shadows"),
            Contrast = o.OptDouble("contrast"),
            HighlightRecovery = o.ReqBool("highlightRecovery"),
            LuminanceNoise = o.OptDouble("luminanceNoise"),
            ColorNoise = o.OptDouble("colorNoise"),
            Sharpness = o.OptDouble("sharpness"),
            LensCorrection = o.OptBool("lensCorrection"),
            Process = o.OptString("process"),
        };
    }

    public override void Write(Utf8JsonWriter writer, RawSettings value, JsonSerializerOptions options) =>
        new JsonFields(options)
            .Double("exposure", value.Exposure).Double("temperature", value.Temperature).Double("tint", value.Tint)
            .Double("shadows", value.Shadows).Double("contrast", value.Contrast).Bool("highlightRecovery", value.HighlightRecovery)
            .Double("luminanceNoise", value.LuminanceNoise).Double("colorNoise", value.ColorNoise).Double("sharpness", value.Sharpness)
            .Bool("lensCorrection", value.LensCorrection).String("process", value.Process)
            .WriteTo(writer);
}
