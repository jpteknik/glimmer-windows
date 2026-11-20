// Ported from Packages/GlimmerKit/Sources/GlimmerCore/DocumentManifest.swift

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// The <c>.glimmer</c> document type (the Mac's <c>UTType.glimmerDocument</c>).
/// </summary>
/// <remarks>
/// The only file type a Glimmer document reads and writes. Images are imported into new, untitled documents and never
/// opened in place: autosave once overwrote an opened JPEG with a <c>.glimmer</c> package on the Mac.
/// </remarks>
public static class GlimmerFileTypes
{
    public const string Extension = ".glimmer";
    public const string TypeIdentifier = "com.brattoo.glimmer.document";
    public static ImmutableArray<string> DocumentExtensions { get; } = [Extension];
}

/// <summary>
/// The colour space documents are tagged with. Pixels are always stored as linear extended sRGB, which covers Display
/// P3 and beyond; the tag decides the default export profile.
/// </summary>
[JsonConverter(typeof(RawValueEnumConverter<WorkingColorSpace>))]
public enum WorkingColorSpace
{
    [RawValue("sRGB")] SRGB,
    [RawValue("displayP3")] DisplayP3,
}

/// <summary>Where blend modes are evaluated.</summary>
[JsonConverter(typeof(RawValueEnumConverter<BlendSpace>))]
public enum BlendSpace
{
    /// <summary>Linear light. Matches Glimmer for the web.</summary>
    [RawValue("linear")] Linear,
    /// <summary>Gamma-encoded, Photoshop-compatible.</summary>
    [RawValue("gamma")] Gamma,
}

public static class WorkingColorSpaceExtensions
{
    extension(WorkingColorSpace)
    {
        public static IReadOnlyList<WorkingColorSpace> AllCases => RawValues<WorkingColorSpace>.AllCases;
        public static WorkingColorSpace? FromRawValue(string? raw) => RawValues<WorkingColorSpace>.Parse(raw);
    }

    public static string RawValue(this WorkingColorSpace space) => RawValues<WorkingColorSpace>.Of(space);
}

public static class BlendSpaceExtensions
{
    extension(BlendSpace)
    {
        public static IReadOnlyList<BlendSpace> AllCases => RawValues<BlendSpace>.AllCases;
        public static BlendSpace? FromRawValue(string? raw) => RawValues<BlendSpace>.Parse(raw);
    }

    public static string RawValue(this BlendSpace space) => RawValues<BlendSpace>.Of(space);
}

/// <summary>What a layer is, in the manifest (Swift's <c>LayerRecord.Kind</c>).</summary>
[JsonConverter(typeof(RawValueEnumConverter<LayerRecordKind>))]
public enum LayerRecordKind
{
    [RawValue("raster")] Raster,
    [RawValue("adjustment")] Adjustment,
    [RawValue("group")] Group,
    [RawValue("fill")] Fill,
    [RawValue("smart")] Smart,
    [RawValue("text")] Text,
}

/// <summary>
/// Serialized form of a layer. Pixels live in separate tile files: <c>layers/&lt;id&gt;.tiles</c> (raster),
/// <c>&lt;id&gt;.mask.tiles</c>, <c>&lt;id&gt;.smart.tiles</c>.
/// </summary>
[JsonConverter(typeof(LayerRecordJsonConverter))]
public sealed record LayerRecord
{
    public sealed record MaskRecord(float DefaultValue, bool Enabled, double Density, double Feather);

    public sealed record SmartRecord(int Width, int Height, AffineTransform Transform, ImmutableArray<SmartFilter>? Filters)
    {
        public bool Equals(SmartRecord? other) =>
            other is not null && Width == other.Width && Height == other.Height && Transform == other.Transform && Seq.Equal(Filters, other.Filters);
        public override int GetHashCode() => HashCode.Combine(Width, Height, Transform, Seq.Hash(Filters));
    }

    /// <summary>
    /// A layer developed from a RAW file: its settings, name and the signature of the developed pixels. The file's bytes
    /// are in the package as <c>&lt;id&gt;.raw</c>.
    /// </summary>
    /// <param name="PixelsChanged">True when the layer's pixels were changed after they were developed (painted on, say).</param>
    public sealed record RawRecord(string FileName, RawSettings Settings, long ByteCount, bool? PixelsChanged);

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required LayerRecordKind Kind { get; init; }
    public required bool Visible { get; init; }
    public required double Opacity { get; init; }
    public required BlendMode BlendMode { get; init; }
    public bool? Clipped { get; init; }
    public MaskRecord? Mask { get; init; }
    public VectorPath? VectorMask { get; init; }
    public LayerEffects? Effects { get; init; }
    public Adjustment? Adjustment { get; init; }
    public Fill? Fill { get; init; }
    public SmartRecord? Smart { get; init; }
    public TextLayer? Text { get; init; }
    /// <summary>See <see cref="Layer.FormerText"/>.</summary>
    public string? FormerText { get; init; }
    public RawRecord? Raw { get; init; }
    public bool? Expanded { get; init; }
    /// <summary>Groups only, bottom → top.</summary>
    public ImmutableArray<LayerRecord>? Children { get; init; }
}

/// <summary>Top-level description of a document, stored as <c>manifest.json</c> in the package.</summary>
[JsonConverter(typeof(DocumentManifestJsonConverter))]
public sealed record DocumentManifest
{
    public const int CurrentFormatVersion = 1;
    public const int MaxDimension = 32_768;

    public DocumentManifest(int width, int height, WorkingColorSpace colorSpace = WorkingColorSpace.SRGB,
                            BlendSpace blendSpace = BlendSpace.Linear, IEnumerable<LayerRecord>? layers = null, Guid? activeLayerID = null)
    {
        FormatVersion = CurrentFormatVersion;
        Width = width;
        Height = height;
        ColorSpace = colorSpace;
        BlendSpace = blendSpace;
        Layers = layers is null ? [] : [.. layers];
        ActiveLayerID = activeLayerID;
    }

    public int FormatVersion { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public WorkingColorSpace ColorSpace { get; init; }
    public BlendSpace BlendSpace { get; init; }
    /// <summary>Bottom → top.</summary>
    public ImmutableArray<LayerRecord> Layers { get; init; }
    public Guid? ActiveLayerID { get; init; }
    public ImmutableArray<VectorPath>? Paths { get; init; }
    public ViewAids? Aids { get; init; }
    /// <summary>Present while the document is an edit of another app's file (see <see cref="DocumentLink"/>).</summary>
    public DocumentLink? Link { get; init; }
    /// <summary>The record of what a Photoshop file lost when this document was made from it.</summary>
    public PSDImportReport? ImportReport { get; init; }
}

// MARK: JSON (synthesized Codable)

public sealed class LayerRecordJsonConverter : JsonConverter<LayerRecord>
{
    public override LayerRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(LayerRecord));
        LayerRecord.MaskRecord? mask = null;
        if (o.TryGet("mask", out var m))
        {
            SwiftJson.ExpectObject(m, "mask");
            mask = new LayerRecord.MaskRecord(m.ReqFloat("defaultValue"), m.ReqBool("enabled"), m.ReqDouble("density"), m.ReqDouble("feather"));
        }
        LayerRecord.SmartRecord? smart = null;
        if (o.TryGet("smart", out var s))
        {
            SwiftJson.ExpectObject(s, "smart");
            smart = new LayerRecord.SmartRecord((int)s.ReqInt("width"), (int)s.ReqInt("height"), s.Req<AffineTransform>("transform", options),
                                                s.OptArray<SmartFilter>("filters", options));
        }
        LayerRecord.RawRecord? raw = null;
        if (o.TryGet("raw", out var r))
        {
            SwiftJson.ExpectObject(r, "raw");
            raw = new LayerRecord.RawRecord(r.ReqString("fileName"), r.Req<RawSettings>("settings", options), r.ReqInt("byteCount"),
                                            r.OptBool("pixelsChanged"));
        }
        return new LayerRecord
        {
            Id = o.ReqUuid("id"),
            Name = o.ReqString("name"),
            Kind = o.Req<LayerRecordKind>("kind", options),
            Visible = o.ReqBool("visible"),
            Opacity = o.ReqDouble("opacity"),
            BlendMode = o.Req<BlendMode>("blendMode", options),
            Clipped = o.OptBool("clipped"),
            Mask = mask,
            VectorMask = o.Opt<VectorPath>("vectorMask", options),
            Effects = o.Opt<LayerEffects>("effects", options),
            Adjustment = o.Opt<Adjustment>("adjustment", options),
            Fill = o.Opt<Fill>("fill", options),
            Smart = smart,
            Text = o.Opt<TextLayer>("text", options),
            FormerText = o.OptString("formerText"),
            Raw = raw,
            Expanded = o.OptBool("expanded"),
            Children = o.OptArray<LayerRecord>("children", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, LayerRecord value, JsonSerializerOptions options)
    {
        var f = new JsonFields(options)
            .Uuid("id", value.Id).String("name", value.Name).Value("kind", value.Kind).Bool("visible", value.Visible)
            .Double("opacity", value.Opacity).Value("blendMode", value.BlendMode).Bool("clipped", value.Clipped)
            .Value("vectorMask", value.VectorMask).Value("effects", value.Effects).Value("adjustment", value.Adjustment)
            .Value("fill", value.Fill).Value("text", value.Text).String("formerText", value.FormerText).Bool("expanded", value.Expanded)
            .Array("children", value.Children);
        if (value.Mask is { } mask)
        {
            f.Custom("mask", w => new JsonFields(options).Float("defaultValue", mask.DefaultValue).Bool("enabled", mask.Enabled)
                .Double("density", mask.Density).Double("feather", mask.Feather).WriteTo(w));
        }
        if (value.Smart is { } smart)
        {
            f.Custom("smart", w => new JsonFields(options).Int("width", smart.Width).Int("height", smart.Height)
                .Value("transform", smart.Transform).Array("filters", smart.Filters).WriteTo(w));
        }
        if (value.Raw is { } raw)
        {
            f.Custom("raw", w => new JsonFields(options).String("fileName", raw.FileName).Value("settings", raw.Settings)
                .Int("byteCount", raw.ByteCount).Bool("pixelsChanged", raw.PixelsChanged).WriteTo(w));
        }
        f.WriteTo(writer);
    }
}

public sealed class DocumentManifestJsonConverter : JsonConverter<DocumentManifest>
{
    public override DocumentManifest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(DocumentManifest));
        return new DocumentManifest((int)o.ReqInt("width"), (int)o.ReqInt("height"), o.Req<WorkingColorSpace>("colorSpace", options),
                                    o.Req<BlendSpace>("blendSpace", options), o.ReqArray<LayerRecord>("layers", options), o.OptUuid("activeLayerID"))
        {
            FormatVersion = (int)o.ReqInt("formatVersion"),
            Paths = o.OptArray<VectorPath>("paths", options),
            Aids = o.Opt<ViewAids>("aids", options),
            Link = o.Opt<DocumentLink>("link", options),
            ImportReport = o.Opt<PSDImportReport>("importReport", options),
        };
    }

    public override void Write(Utf8JsonWriter writer, DocumentManifest value, JsonSerializerOptions options) =>
        new JsonFields(options)
            .Int("formatVersion", value.FormatVersion).Int("width", value.Width).Int("height", value.Height)
            .Value("colorSpace", value.ColorSpace).Value("blendSpace", value.BlendSpace)
            .Array("layers", value.Layers).Uuid("activeLayerID", value.ActiveLayerID)
            .Array("paths", value.Paths).Value("aids", value.Aids).Value("link", value.Link).Value("importReport", value.ImportReport)
            .WriteTo(writer);
}
