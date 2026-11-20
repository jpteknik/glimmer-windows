// Ported from Packages/GlimmerKit/Sources/GlimmerCore/PSD/PSDImportReport.swift

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// What an import of a Photoshop file kept, changed and lost. It is made by the same call that makes the document,
/// so there is no way to import without one, and it is saved with the document: it is the record of what the file lost.
/// </summary>
/// <remarks>
/// The notes are kinds with their particulars, not sentences, so the app can say them in the person's language. (This
/// type is in every build; the import itself is behind GLIMMER_PSD.)
/// </remarks>
[JsonConverter(typeof(PSDImportReportJsonConverter))]
public sealed record PSDImportReport
{
    [JsonConverter(typeof(RawValueEnumConverter<Kind>))]
    public enum Kind
    {
        [RawValue("wasText")] WasText,
        [RawValue("wasSmartObject")] WasSmartObject,
        [RawValue("adjustmentLeftOut")] AdjustmentLeftOut,
        [RawValue("fillLeftOut")] FillLeftOut,
        [RawValue("effectsNotRead")] EffectsNotRead,
        [RawValue("vectorMaskNotRead")] VectorMaskNotRead,
        [RawValue("blendModeBecameNormal")] BlendModeBecameNormal,
        [RawValue("pixelsIncomplete")] PixelsIncomplete,
        [RawValue("groupEndMissing")] GroupEndMissing,
        [RawValue("groupRecordMissing")] GroupRecordMissing,
        [RawValue("layersNotRead")] LayersNotRead,
        [RawValue("curvesMayDiffer")] CurvesMayDiffer,
        [RawValue("masksMerged")] MasksMerged,
    }

    [JsonConverter(typeof(PSDImportNoteJsonConverter))]
    public sealed record Note(Kind Kind, string? Layer = null, string? Detail = null, bool ChangesLook = false)
    {
#if GLIMMER_PSD
        /// <summary>The note in English, for the agent API and the logs. The app says it in the person's language.</summary>
        public string What
        {
            get
            {
                var detail = Detail ?? "";
                return Kind switch
                {
                    Kind.WasText => detail.Length == 0
                        ? "It was text and is now pixels: the words can't be edited as text."
                        : $"It was text (“{detail}”) and is now pixels: the words can't be edited as text.",
                    Kind.CurvesMayDiffer => "It is a Curves adjustment with the same points. Between the points the curve may run a little differently than in Photoshop.",
                    Kind.WasSmartObject => "It was a smart object and is now pixels. The file placed in it is not kept in this version.",
                    Kind.AdjustmentLeftOut => $"It was a {detail} adjustment layer. That kind is not read in this version, so it was left out.",
                    Kind.FillLeftOut => $"It was a {detail} fill or shape layer. That kind is not read in this version, so it was left out.",
                    Kind.EffectsNotRead => "Its layer effects (shadows, strokes, glows) were not read.",
                    Kind.VectorMaskNotRead => "Its vector mask was not read; the layer's pixels are shown whole.",
                    Kind.BlendModeBecameNormal => $"The blend mode {detail} became Normal: Glimmer doesn't have it.",
                    Kind.PixelsIncomplete => "Its pixels are incomplete in the file, so the layer came in empty.",
                    Kind.GroupEndMissing => "The group's end is missing in the file; it came in empty.",
                    Kind.GroupRecordMissing => "A group in the file has no record of its own; its layers came in without the group.",
                    Kind.MasksMerged => "It had an outline and a painted mask. The two are now one mask, so the outline can't be edited as a path.",
                    Kind.LayersNotRead => $"Glimmer reads the layers of RGB and grayscale files with 8 or 16 bits per channel. This file is {detail}. Its layers were not read; the picture is the one saved in the file.",
                    _ => throw new InvalidOperationException(),
                };
            }
        }
#endif
    }

    public PSDImportReport(string source) => Source = source;

    /// <summary>"RGB, 8 bits per channel" and the like, as the file says.</summary>
    public string Source { get; init; }
    /// <summary>How many layers and groups the file had, and how many of them came in as layers of their own.</summary>
    public int LayersInFile { get; init; }
    public int LayersKept { get; init; }
    public int GroupsKept { get; init; }
    /// <summary>True when only the file's flattened picture was used.</summary>
    public bool Flattened { get; init; }
    public ImmutableArray<Note> Notes { get; init; } = [];
    /// <summary>
    /// How far Glimmer's picture of the layers is from the picture the file carries: the mean difference per channel on
    /// a scale of 0 to 255, and the share of pixels that differ by more than 8. Filled in by the app, which can draw;
    /// null when it wasn't measured.
    /// </summary>
    public double? MeanDifference { get; init; }
    public double? ShareVisiblyDifferent { get; init; }
    /// <summary>
    /// True when the file says it carries no flattened picture of its own (saved with Maximize Compatibility off): there
    /// is then nothing to compare Glimmer's picture with.
    /// </summary>
    public bool? FileHasNoPicture { get; init; }

    public bool LooksTheSame => !Notes.Any(n => n.ChangesLook);

    /// <summary>Whether the measured picture matches the file's own (null when it wasn't measured).</summary>
    public bool? MatchesTheFile =>
        MeanDifference is { } mean && ShareVisiblyDifferent is { } share ? share < 0.001 && mean < 1 : null;

#if GLIMMER_PSD
    /// <summary>The report as lines in English, the summary first.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            var output = new List<string>();
            if (Flattened)
                output.Add($"Opened as one layer: the file's own flattened picture ({Source}).");
            else if (Notes.IsEmpty)
                output.Add($"Everything came through: {LayersKept} {(LayersKept == 1 ? "layer" : "layers")}"
                           + (GroupsKept > 0 ? $" in {GroupsKept} {(GroupsKept == 1 ? "group" : "groups")}" : "") + $" ({Source}).");
            else
                output.Add($"{LayersKept} of {LayersInFile} layers came through as layers ({Source}). {Notes.Length} "
                           + $"{(Notes.Length == 1 ? "thing" : "things")} did not come through as it was:");
            output.AddRange(Notes.Select(n => (n.Layer is { } layer ? $"“{layer}”: " : "") + n.What));
            if (FileHasNoPicture == true)
            {
                output.Add("The file carries no flattened picture of its own (it was saved with Maximize Compatibility off), so Glimmer's picture could not be compared with it.");
            }
            else if (MatchesTheFile is { } matches && MeanDifference is { } mean && ShareVisiblyDifferent is { } share)
            {
                output.Add(matches
                    ? "Glimmer's picture matches the one saved in the file."
                    : string.Format(CultureInfo.InvariantCulture,
                        "Glimmer's picture differs from the one saved in the file: {0:F1} % of the pixels visibly, {1:F1} of 255 on average.",
                        share * 100, mean));
            }
            return output;
        }
    }
#endif

    public bool Equals(PSDImportReport? other) =>
        other is not null && Source == other.Source && LayersInFile == other.LayersInFile && LayersKept == other.LayersKept
        && GroupsKept == other.GroupsKept && Flattened == other.Flattened && Seq.Equal(Notes, other.Notes)
        && Nullable.Equals(MeanDifference, other.MeanDifference) && Nullable.Equals(ShareVisiblyDifferent, other.ShareVisiblyDifferent)
        && FileHasNoPicture == other.FileHasNoPicture;

    public override int GetHashCode() =>
        HashCode.Combine(Source, LayersInFile, LayersKept, GroupsKept, Flattened, Seq.Hash(Notes), MeanDifference, FileHasNoPicture);
}

/// <summary>Synthesized <c>Codable</c>: <c>kind</c> and <c>changesLook</c> are required.</summary>
public sealed class PSDImportNoteJsonConverter : JsonConverter<PSDImportReport.Note>
{
    public override PSDImportReport.Note Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), "PSDImportReport.Note");
        return new PSDImportReport.Note(o.Req<PSDImportReport.Kind>("kind", options), o.OptString("layer"), o.OptString("detail"),
                                        o.ReqBool("changesLook"));
    }

    public override void Write(Utf8JsonWriter writer, PSDImportReport.Note value, JsonSerializerOptions options) =>
        new JsonFields(options).Value("kind", value.Kind).String("layer", value.Layer).String("detail", value.Detail)
            .Bool("changesLook", value.ChangesLook).WriteTo(writer);
}

/// <summary>Synthesized <c>Codable</c>: every non-optional field is required, the property defaults notwithstanding.</summary>
public sealed class PSDImportReportJsonConverter : JsonConverter<PSDImportReport>
{
    public override PSDImportReport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(PSDImportReport));
        return new PSDImportReport(o.ReqString("source"))
        {
            LayersInFile = (int)o.ReqInt("layersInFile"),
            LayersKept = (int)o.ReqInt("layersKept"),
            GroupsKept = (int)o.ReqInt("groupsKept"),
            Flattened = o.ReqBool("flattened"),
            Notes = o.ReqArray<PSDImportReport.Note>("notes", options),
            MeanDifference = o.OptDouble("meanDifference"),
            ShareVisiblyDifferent = o.OptDouble("shareVisiblyDifferent"),
            FileHasNoPicture = o.OptBool("fileHasNoPicture"),
        };
    }

    public override void Write(Utf8JsonWriter writer, PSDImportReport value, JsonSerializerOptions options) =>
        new JsonFields(options).String("source", value.Source).Int("layersInFile", value.LayersInFile).Int("layersKept", value.LayersKept)
            .Int("groupsKept", value.GroupsKept).Bool("flattened", value.Flattened).Array("notes", value.Notes)
            .Double("meanDifference", value.MeanDifference).Double("shareVisiblyDifferent", value.ShareVisiblyDifferent)
            .Bool("fileHasNoPicture", value.FileHasNoPicture).WriteTo(writer);
}
