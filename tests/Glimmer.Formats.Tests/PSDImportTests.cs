// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/PSDImportTests.swift
#if GLIMMER_PSD
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Glimmer.Core;
using Glimmer.Formats;
using L = Glimmer.Formats.Tests.PSDTestWriter.Layer;

namespace Glimmer.Formats.Tests;

/// <summary>A Photoshop file becomes a Glimmer document, and the report says what did not survive.</summary>
public class PSDImportTests
{
    internal static readonly PSDFile.Limits Limits = new(maxCanvasPixels: 1_000_000, maxDecodedBytes: 4_000_000);
    internal static readonly PSDFile.Rect Unit = new(0, 0, 2, 2);

    internal static L Pixels(string name, string blend = "norm") =>
        new(name: name, rect: Unit, blendKey: blend, planes: [(0, [1, 2, 3, 4]), (1, [1, 2, 3, 4]), (2, [1, 2, 3, 4])]);

    /// <summary>Swift's <c>file(_:)</c>.</summary>
    internal static byte[] FileOf(IEnumerable<L> layers) =>
        new PSDTestWriter { Layers = [.. layers], Composite = [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]] }.Data();

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void LayersGroupsAndMasksComeThrough(int depth)
    {
        var (state, report) = PSDImport.Document(PSDTestWriter.Sample(depth: depth).Data(), Limits);
        Assert.True(state.Width == 4 && state.Height == 3 && state.BlendSpace == BlendSpace.Gamma);
        Assert.Equal(new[] { "Background copy", "Group 1" }, state.Layers.Select(l => l.Name));
        var group = Assert.IsType<GroupContent>(state.Layers[1].Content).Group;
        Assert.True(group.IsExpanded);
        Assert.Equal(new[] { "Blå himmel", "Hidden" }, group.Children.Select(l => l.Name));
        Assert.Equal(BlendMode.PassThrough, state.Layers[1].BlendMode);
        var sky = group.Children[0];
        var hidden = group.Children[1];
        Assert.True(sky.BlendMode == BlendMode.Multiply && Math.Abs(sky.Opacity - 128.0 / 255) < 1e-9 && sky.IsVisible && !sky.IsClipped);
        Assert.True(sky.Mask?.DefaultValue == 1 && sky.Mask?.IsEnabled == true && sky.Mask?.Tiles.IsEmpty == false);
        Assert.True(!hidden.IsVisible && hidden.IsClipped);
        Assert.False(Assert.IsType<RasterContent>(sky.Content).Tiles.IsEmpty);
        // The report: four layers in the file (the group counts, its end mark doesn't), all kept, one note about the text.
        Assert.True(report.LayersInFile == 4 && report.LayersKept == 4 && report.GroupsKept == 1 && !report.Flattened);
        Assert.Equal(new[] { new PSDImportReport.Note(PSDImportReport.Kind.WasText, "Hidden") }, report.Notes);
        Assert.Equal(report, state.ImportReport);   // the report travels in the document
        Assert.True(report.LooksTheSame);
        Assert.Equal($"4 of 4 layers came through as layers (RGB, {depth} bits per channel). 1 thing did not come through as it was:", report.Lines[0]);
        Assert.Equal("“Hidden”: It was text and is now pixels: the words can't be edited as text.", report.Lines[^1]);
        // (New: the mask's values. It was read at the document's depth, docs/UPSTREAM-NOTES.md, "PSD reader", item 1;
        // the Mac refuses the 16-bit file.)
        Assert.Equal(0f, TilePixels.MaskAt(sky.Mask!, 1, 1));
        Assert.Equal((float)(Half)(200 / 255f), TilePixels.MaskAt(sky.Mask!, 2, 1));
        Assert.Equal(1f, TilePixels.MaskAt(sky.Mask!, 1, 2));   // outside the mask's rectangle: its default, shown
    }

    [Fact]
    public void NothingLostIsSaidInOneLine()
    {
        var (_, report) = PSDImport.Document(FileOf([Pixels("One"), Pixels("Two")]), Limits);
        Assert.Empty(report.Notes);
        Assert.Equal(new[] { "Everything came through: 2 layers (RGB, 8 bits per channel)." }, report.Lines);
    }

    [Fact]
    public void WhatGlimmerDoesNotHaveIsNamedNeverDropped()
    {
        var levels = new L(name: "Levels 1", rect: new PSDFile.Rect(0, 0, 0, 0)) { ExtraBlocks = [("levl", [0, 2])] };
        var fill = new L(name: "Color Fill 1", rect: new PSDFile.Rect(0, 0, 0, 0)) { ExtraBlocks = [("SoCo", [0, 0, 0, 16]), ("vmsk", [0, 0, 0, 3])] };
        var glow = Pixels("Glow", blend: "diss");
        glow.ExtraBlocks = [("lfx2", [0, 0]), ("SoLd", [0, 0])];
        var (state, report) = PSDImport.Document(FileOf([Pixels("Photo"), levels, fill, glow, Pixels("Speckle", blend: "diss"), Pixels("Lighter", blend: "lgCl"), Pixels("Darker", blend: "dkCl")]), Limits);
        Assert.Equal(new[] { "Photo", "Glow", "Speckle", "Lighter", "Darker" }, state.Layers.Select(l => l.Name));
        // Lighter Color and Darker Color are Glimmer's own modes now; Dissolve is not.
        Assert.Equal(new[] { BlendMode.Normal, BlendMode.Normal, BlendMode.Normal, BlendMode.LighterColor, BlendMode.DarkerColor }, state.Layers.Select(l => l.BlendMode));
        Assert.True(report.LayersInFile == 7 && report.LayersKept == 5 && !report.LooksTheSame);
        Assert.Equal(new[]
        {
            "Levels 1: It was a Levels adjustment layer. That kind is not read in this version, so it was left out.",
            "Color Fill 1: It was a solid color fill or shape layer. That kind is not read in this version, so it was left out.",
            "Glow: The blend mode Dissolve became Normal: Glimmer doesn't have it.",
            "Glow: Its layer effects (shadows, strokes, glows) were not read.",
            "Glow: It was a smart object and is now pixels. The file placed in it is not kept in this version.",
            "Speckle: The blend mode Dissolve became Normal: Glimmer doesn't have it.",
        }, report.Notes.Select(n => $"{n.Layer ?? "-"}: {n.What}"));
        Assert.Equal("5 of 7 layers came through as layers (RGB, 8 bits per channel). 6 things did not come through as it was:", report.Lines[0]);
    }

    [Fact]
    public void DamagedGroupStructureKeepsTheLayersAndSaysSo()
    {
        // A group record with no end mark below it, and an end mark with no group record above it.
        var folder = new L(name: "Lonely", rect: new PSDFile.Rect(0, 0, 0, 0), blendKey: "pass", group: 1);
        var (a, first) = PSDImport.Document(FileOf([Pixels("A"), folder]), Limits);
        Assert.Equal(new[] { "A", "Lonely" }, a.Layers.Select(l => l.Name));
        Assert.Equal(new[] { "The group's end is missing in the file; it came in empty." }, first.Notes.Select(n => n.What));
        var end = new L(name: "</Layer group>", rect: new PSDFile.Rect(0, 0, 0, 0), group: 3);
        var (b, second) = PSDImport.Document(FileOf([Pixels("A"), end, Pixels("B")]), Limits);
        Assert.Equal(new[] { "A", "B" }, b.Layers.Select(l => l.Name));
        Assert.Null(Assert.Single(second.Notes).Layer);
        // Groups nested without end: refused, not followed down.
        var deep = Enumerable.Repeat(end, 70).Append(Pixels("Bottom"));
        Assert.Throws<PSDFile.Problem>(() => PSDImport.Document(FileOf(deep), Limits));
    }

    [Fact]
    public void ALayerWithMissingPlanesComesInEmptyAndIsNamed()
    {
        var broken = new L(name: "Half", rect: Unit, planes: [(0, [1, 2, 3, 4])]);   // red only
        var (state, report) = PSDImport.Document(FileOf([broken]), Limits);
        Assert.Single(state.Layers);
        Assert.Equal(new[] { "Its pixels are incomplete in the file, so the layer came in empty." }, report.Notes.Select(n => n.What));
        Assert.False(report.LooksTheSame);
    }

    [Fact]
    public void OtherColourModesAreSaidPlainly()
    {
        Assert.Equal("CMYK, 8 bits per channel", PSDImport.Describe(new PSDFile.FileHeader(isLarge: false, channels: 4, width: 1, height: 1, depth: 8, mode: PSDFile.ColorMode.Cmyk)));
        Assert.Equal("RGB, 32 bits per channel, large document format", PSDImport.Describe(new PSDFile.FileHeader(isLarge: true, channels: 3, width: 1, height: 1, depth: 32, mode: PSDFile.ColorMode.Rgb)));
        Assert.Equal("Bitmap, 1 bit per channel", PSDImport.Describe(new PSDFile.FileHeader(isLarge: false, channels: 1, width: 1, height: 1, depth: 1, mode: PSDFile.ColorMode.Bitmap)));
    }

    /// <summary>Whatever is done to the bytes, an import throws or gives a document with a report: no crash.
    /// A CMYK file saved without its flattened picture would open as a blank page: refused, with what to do.</summary>
    [Fact]
    public void AFileWithNeitherReadableLayersNorAPictureIsRefusedNotShownBlank()
    {
        var data = new List<byte>(FileOf([Pixels("Photo")]));
        data[25] = 4;   // the header's colour mode: CMYK
        data.RemoveRange(30, 4);
        data.InsertRange(30, [0, 0, 0, 18]);   // (these files have no colour data and no resources)
        data.InsertRange(34, [.. "8BIM"u8.ToArray(), 0x04, 0x21, 0, 0, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0]);
        var bytes = data.ToArray();
        Assert.False(PSDFile.HasFlattenedPicture(bytes));
        var problem = Assert.Throws<PSDFile.Problem>(() => PSDImport.Document(bytes, Limits));
        Assert.Contains("saved without a flattened picture", problem.Message);
    }

    [Fact]
    public void ADamagedFileNeverCrashesTheImport()
    {
        var good = PSDTestWriter.Sample(compression: 1).Data();
        int imported = 0;
        for (int index = 0; index < good.Length; index++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF })
            {
                if (good[index] == value) continue;
                var bad = (byte[])good.Clone();
                bad[index] = value;
                // (Swift's try? takes any error; here only the import's own refusal is an answer, anything else fails the test.)
                try
                {
                    var (state, _) = PSDImport.Document(bad, Limits);
                    imported += 1;
                    Assert.True(state.Width >= 1 && state.Height >= 1);
                }
                catch (PSDFile.Problem) { }
            }
        }
        Assert.True(imported > 0);   // changes inside pixel data still import
    }

    /// <summary>New in the port, found by the real-file abuse test: a damaged embedded profile is a profile that can't
    /// be read, and the layers are taken as sRGB, as the Mac takes them when CGColorSpace(iccData:) gives nil.
    /// (SkiaSharp's SKColorSpace.CreateIcc throws on it.)</summary>
    [Fact]
    public void AProfileThatCantBeReadLeavesTheColoursAsSrgb()
    {
        var damaged = Enumerable.Repeat((byte)0xAB, 200).ToArray();
        "RGB "u8.CopyTo(damaged.AsSpan(16));
        "acsp"u8.CopyTo(damaged.AsSpan(36));
        var w = new PSDTestWriter { Layers = [Pixels("Photo")], Composite = [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]], IccProfile = damaged };
        var (state, _) = PSDImport.Document(w.Data(), Limits);
        float red = (float)ColorMath.SrgbToLinear(2 / 255.0);
        Assert.Equal((float)(Half)red, TilePixels.At(state.Layers[0].Tiles, 1, 0).X);
        Assert.Null(SourceColor.FromIcc(damaged, gray: false));
    }

    /// <summary>
    /// New in the port. A mask that shows by default and hides one whole tile: the Mac makes the mask's tiles with
    /// Selection.rasterize, which leaves out a tile that comes out all zero, and the left-out tile then takes the default
    /// and shows what the file hides. Here the tile is kept, as one colour.
    /// </summary>
    [Fact]
    public void AMaskThatHidesAWholeTileStillHidesIt()
    {
        const int side = 300;
        ushort[] Plane(ushort v) => Enumerable.Repeat(v, side * side).ToArray();
        var layer = new L(name: "Photo", rect: new PSDFile.Rect(0, 0, side, side), planes: [(0, Plane(200)), (1, Plane(100)), (2, Plane(50))], compression: 1)
        {
            // Hidden over x < 256 of its rows, which are y < 256: exactly tile (0, 0). Shown to its right, and below its
            // rectangle by its default.
            Mask = (new PSDFile.Rect(0, 0, 256, side), 255, [.. Enumerable.Range(0, 256 * side).Select(i => i % side < 256 ? (byte)0 : (byte)255)]),
        };
        var w = new PSDTestWriter { Width = side, Height = side, Layers = [layer], Composite = [Plane(200), Plane(100), Plane(50)] };
        var (state, _) = PSDImport.Document(w.Data(), Limits);
        var mask = state.Layers[0].Mask!;
        Assert.Equal(1f, mask.DefaultValue);
        var hidden = mask.Tiles[new TileCoord(0, 0)];
        Assert.NotNull(hidden);
        Assert.True(hidden.IsSolid && hidden.SolidColor == Vector4.Zero);
        Assert.Equal(0f, TilePixels.MaskAt(mask, 100, 100));
        Assert.Equal(1f, TilePixels.MaskAt(mask, 280, 100));
        Assert.Equal(1f, TilePixels.MaskAt(mask, 100, 280));
    }
}

/// <summary>The report is part of the document: saved with it and read back.</summary>
public class PSDImportReportTests
{
    [Fact]
    public void TheReportIsSavedWithTheDocumentAndReadBack()
    {
        var (state, report) = PSDImport.Document(PSDTestWriter.Sample().Data(), PSDImportTests.Limits);
        report = report with { MeanDifference = 0.5, ShareVisiblyDifferent = 0.25 };
        state = state with { ImportReport = report };
        var back = GlimmerPackage.State(GlimmerPackage.Contents(state));
        Assert.Equal(report, back.ImportReport);
        Assert.Equal(PSDImportReport.Kind.WasText, back.ImportReport?.Notes.FirstOrDefault()?.Kind);
        Assert.False(back.ImportReport?.MatchesTheFile);
        // A document that was never a Photoshop file has none, and its manifest doesn't mention one.
        var plain = GlimmerPackage.Contents(new DocumentState(4, 4));
        Assert.Null(GlimmerPackage.State(plain).ImportReport);
        Assert.DoesNotContain("importReport", Encoding.UTF8.GetString(plain.Files["manifest.json"].Span));
    }
}

/// <summary>Fills, shapes, the adjustment layers with documented data, and a text layer's words.</summary>
public class PSDSettingsTests
{
    private static readonly PSDFile.Rect Empty = new(0, 0, 0, 0);

    private static L Block(string name, List<(string Key, byte[] Body)> blocks) => new(name: name, rect: Empty) { ExtraBlocks = blocks };

    private static Fill? FillOf(Layer layer) => layer.Content is FillContent f ? f.Fill : null;

    private static Adjustment AdjustmentOf(Layer layer) => Assert.IsType<AdjustmentContent>(layer.Content).Adjustment;

    [Fact]
    public void ADescriptorIsReadAsTheSpecificationLaysItOut()
    {
        var inner = PSDTestWriter.Descriptor("RGBC", [("Rd  ", "doub", PSDTestWriter.Double(255)), ("name with length", "TEXT", PSDTestWriter.Unicode("Blå"))]);
        byte[] list = [.. PSDTestWriter.Be32(2), .. "long"u8.ToArray(), .. PSDTestWriter.Be32(7), .. "bool"u8.ToArray(), 1];
        byte[] unit = [.. "#Pxl"u8.ToArray(), .. PSDTestWriter.Double(12.5)];
        byte[] enumerated = [.. PSDTestWriter.Id("Ornt"), .. PSDTestWriter.Id("Hrzn")];
        var data = PSDTestWriter.Descriptor("null", [("Clr ", "Objc", inner), ("List", "VlLs", list), ("Sz  ", "UntF", unit), ("Ornt", "enum", enumerated)]);
        var value = PSDDescriptor.Read(data);
        Assert.Equal(new PSDValue.Double(255), value["Clr "]?["Rd  "]);
        Assert.Equal(new PSDValue.String("Blå"), value["Clr "]?["name with length"]);
        Assert.Equal(new PSDValue.List([new PSDValue.Integer(7), new PSDValue.Boolean(true)]), value["List"]);
        Assert.Equal(new PSDValue.UnitFloat("#Pxl", 12.5), value["Sz  "]);
        Assert.Equal(new PSDValue.Enumerated("Ornt", "Hrzn"), value["Ornt"]);
        // Cut anywhere, or with a type nobody knows: an error, never a crash.
        for (int length = 0; length < data.Length; length++)
        {
            int cut = length;
            Assert.Throws<PSDFile.Problem>(() => PSDDescriptor.Read(data.AsMemory(0, cut)));
        }
        Assert.Throws<PSDFile.Problem>(() => PSDDescriptor.Read(PSDTestWriter.Descriptor("null", [("x   ", "wxyz", [0, 0, 0, 0])])));
        // A descriptor nested without end is refused by its depth.
        var deep = PSDTestWriter.Descriptor("leaf", []);
        for (int i = 0; i < 40; i++) deep = PSDTestWriter.Descriptor("node", [("next", "Objc", deep)]);
        Assert.Throws<PSDFile.Problem>(() => PSDDescriptor.Read(deep));
    }

    [Fact]
    public void AShapeComesInAsARealFillLayerWithItsOutline()
    {
        (double X, double Y)[] corners = [(0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75)];
        var shape = Block("Rectangle 1", [("SoCo", PSDTestWriter.SolidColor(red: 255, green: 128, blue: 0)), ("vmsk", PSDTestWriter.VectorMask(corners))]);
        var plain = Block("Color Fill 1", [("SoCo", PSDTestWriter.SolidColor(red: 0, green: 0, blue: 255))]);
        var w = new PSDTestWriter { Width = 8, Height = 8, Layers = [PSDImportTests.Pixels("Photo"), plain, shape], Composite = [new ushort[64], new ushort[64], new ushort[64]] };
        var (state, report) = PSDImport.Document(w.Data(), PSDImportTests.Limits);
        Assert.Equal(new[] { "Photo", "Color Fill 1", "Rectangle 1" }, state.Layers.Select(l => l.Name));
        Assert.True(report.Notes.IsEmpty && report.LayersKept == 3);
        Assert.Equal(new Fill.Solid(new SRGBColor(0, 0, 1)), FillOf(state.Layers[1]));
        Assert.Null(state.Layers[1].VectorMask);
        var rectangle = state.Layers[2];
        Assert.Equal(new Fill.Solid(new SRGBColor(1, 128.0 / 255, 0)), FillOf(rectangle));
        var outline = rectangle.VectorMask!.Subpaths.First();
        Assert.Equal(new[] { new Vec2(2, 2), new Vec2(6, 2), new Vec2(6, 6), new Vec2(2, 6) }, outline.Anchors.Select(a => a.Point));
        Assert.True(outline.IsClosed);
        Assert.True(rectangle.Mask?.Tiles.IsEmpty == false && rectangle.Mask?.IsEnabled == true);
        // An outline and a painted mask on one shape (found in a file from Photoshop: the painted
        // mask used to be dropped silently). They become one mask, and the report says so.
        var both = Block("Masked shape", [("SoCo", PSDTestWriter.SolidColor(red: 255, green: 0, blue: 0)), ("vmsk", PSDTestWriter.VectorMask(corners))]);
        // The painted mask hides the left half of the canvas (0) and shows the right half (255).
        both.Mask = (new PSDFile.Rect(0, 0, 8, 8), 0, [.. Enumerable.Range(0, 64).Select(i => i % 8 < 4 ? (byte)0 : (byte)255)]);
        w.Layers = [PSDImportTests.Pixels("Photo"), both];
        var (merged, mergedReport) = PSDImport.Document(w.Data(), PSDImportTests.Limits);
        Assert.Equal(new[] { PSDImportReport.Kind.MasksMerged }, mergedReport.Notes.Select(n => n.Kind));
        Assert.True(mergedReport.LooksTheSame);
        var one = merged.Layers[^1].Mask!;
        Assert.Null(merged.Layers[^1].VectorMask);
        Assert.NotNull(one.Tiles[new TileCoord(0, 0)]);
        // Inside the outline (2…6) and in the shown half: there. Inside the outline but hidden by the paint: not. Outside the outline: not.
        Assert.True(TilePixels.MaskAt(one, 5, 4) == 1 && TilePixels.MaskAt(one, 3, 4) == 0 && TilePixels.MaskAt(one, 7, 7) == 0);
        // An inverted outline is something Glimmer's shapes don't have: left out, and said.
        var inverted = Block("Hole", [("SoCo", PSDTestWriter.SolidColor(red: 1, green: 2, blue: 3)), ("vmsk", PSDTestWriter.VectorMask(corners, flags: 1))]);
        w.Layers = [PSDImportTests.Pixels("Photo"), inverted];
        var (other, second) = PSDImport.Document(w.Data(), PSDImportTests.Limits);
        Assert.Equal(new[] { "Photo" }, other.Layers.Select(l => l.Name));
        Assert.Equal(new[] { PSDImportReport.Kind.FillLeftOut }, second.Notes.Select(n => n.Kind));
    }

    [Fact]
    public void InvertLevelsAndCurvesComeInAsAdjustmentLayers()
    {
        var levels = Block("Levels 1", [("levl", PSDTestWriter.Levels([(10, 240, 0, 255, 120), (0, 255, 20, 235, 100)]))]);
        var curves = Block("Curves 1", [("curv", PSDTestWriter.Curves([[(0, 0), (128, 160), (255, 255)]]))]);
        var invert = Block("Invert 1", [("nvrt", [])]);
        var vibrance = Block("Vibrance 1", [("vibA", [0, 0, 0, 16])]);
        var (state, report) = PSDImport.Document(PSDImportTests.FileOf([PSDImportTests.Pixels("Photo"), levels, curves, invert, vibrance]), PSDImportTests.Limits);
        Assert.Equal(new[] { "Photo", "Levels 1", "Curves 1", "Invert 1" }, state.Layers.Select(l => l.Name));
        var l = AdjustmentOf(state.Layers[1]);
        Assert.Equal(AdjustmentKind.Levels, l.Kind);
        Assert.Equal(new LevelsChannel(InBlack: 10.0 / 255, InWhite: 240.0 / 255, Gamma: 1.2, OutBlack: 0, OutWhite: 1), l.Levels[CurveChannel.Rgb]);
        Assert.Equal(new LevelsChannel(InBlack: 0, InWhite: 1, Gamma: 1, OutBlack: 20.0 / 255, OutWhite: 235.0 / 255), l.Levels[CurveChannel.Red]);
        Assert.Equal(new LevelsChannel(), l.Levels[CurveChannel.Blue]);
        var c = AdjustmentOf(state.Layers[2]);
        Assert.Equal(AdjustmentKind.Curves, c.Kind);
        Assert.Equal(new[] { new CurvePoint(0, 0), new CurvePoint(128.0 / 255, 160.0 / 255), new CurvePoint(1, 1) }, c.Curves[CurveChannel.Rgb]);
        Assert.Equal(AdjustmentKind.Invert, AdjustmentOf(state.Layers[3]).Kind);
        Assert.True(c.UsesNaturalCurve);   // drawn between its points as Photoshop draws it
        Assert.Equal(new[] { PSDImportReport.Kind.AdjustmentLeftOut }, report.Notes.Select(n => n.Kind));
        Assert.Equal("Vibrance", report.Notes[0].Detail);
        // Settings outside what the specification allows are not guessed at: the layer is left out and named.
        var bad = Block("Levels 2", [("levl", PSDTestWriter.Levels([(250, 10, 0, 255, 100)]))]);
        var (_, second) = PSDImport.Document(PSDImportTests.FileOf([PSDImportTests.Pixels("Photo"), bad]), PSDImportTests.Limits);
        Assert.Equal(new[] { PSDImportReport.Kind.AdjustmentLeftOut }, second.Notes.Select(n => n.Kind));
    }

    [Fact]
    public void ATextLayersWordsAreInTheReport()
    {
        var title = PSDImportTests.Pixels("Title");
        title.ExtraBlocks = [("TySh", PSDTestWriter.TypeTool("Hello world"))];
        var (state, report) = PSDImport.Document(PSDImportTests.FileOf([title]), PSDImportTests.Limits);
        Assert.Equal(new[] { new PSDImportReport.Note(PSDImportReport.Kind.WasText, "Title", "Hello world") }, report.Notes);
        // The words stay on the layer, and are saved with it.
        Assert.Equal("Hello world", state.Layers.FirstOrDefault()?.FormerText);
        Assert.Equal("Hello world", GlimmerPackage.State(GlimmerPackage.Contents(state)).Layers.FirstOrDefault()?.FormerText);
        Assert.Equal("It was text (“Hello world”) and is now pixels: the words can't be edited as text.", report.Notes[0].What);
    }
}

public class PSDTimeLimitTests
{
    [Fact]
    public void AFileThatTakesTooLongIsGivenUpOn()
    {
        var limits = PSDImportTests.Limits with { Seconds = -1 };   // the time is already up
        var problem = Assert.Throws<PSDFile.Problem>(() => new PSDFile(PSDTestWriter.Sample().Data(), limits));
        Assert.Equal(PSDFile.TookTooLongMessage, problem.Message);
        Assert.Equal("This Photoshop file takes too long to read, so Glimmer gave up on it.", PSDFile.TookTooLongMessage);
    }
}

/// <summary>
/// New in the port: a file whose layers Glimmer doesn't read shows its flattened picture, which the Mac has ImageIO
/// read and convert. Windows has no reader for Photoshop files, so Glimmer reads the picture itself; these check each
/// colour mode against values worked out from the specification.
/// </summary>
public class PSDFlattenedPictureTests
{
    private static (DocumentState State, PSDImportReport Report) Open(PSDTestWriter w)
    {
        var (state, report) = PSDImport.Document(w.Data(), PSDImportTests.Limits);
        Assert.True(report.Flattened);
        Assert.Equal(new[] { "Background" }, state.Layers.Select(l => l.Name));
        return (state, report);
    }

    private static Vector4 At(DocumentState state, int x, int y) => TilePixels.At(state.Layers[0].Tiles, x, y);

    private static void Near(Vector4 expected, Vector4 actual, float tolerance = 0.002f) =>
        Assert.True(Vector4.Abs(expected - actual) is var d && d.X <= tolerance && d.Y <= tolerance && d.Z <= tolerance && d.W <= tolerance,
                    $"expected {expected}, got {actual}");

    private static byte[] Floats(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    [Fact]
    public void ThirtyTwoBitsAreLinearLightAndKeepValuesAboveOne()
    {
        // A fourth channel (transparency or a spot colour) is left alone, as the Mac shows the picture.
        var w = new PSDTestWriter { Depth = 32, Channels = 4, Width = 2, Height = 1, CompositeBytes = [Floats(1, 0.25f), Floats(0, 2), Floats(0.5f, 0), Floats(0, 0)] };
        var (state, report) = Open(w);
        Assert.Equal(new[] { PSDImportReport.Kind.LayersNotRead }, report.Notes.Select(n => n.Kind));
        Assert.Equal("RGB, 32 bits per channel", report.Notes[0].Detail);
        Assert.Equal(new Vector4(1, 0, 0.5f, 1), At(state, 0, 0));
        Assert.Equal(new Vector4(0.25f, 2, 0, 1), At(state, 1, 0));
    }

    [Fact]
    public void LabIsConvertedThroughTheProfileConnectionSpace()
    {
        // L 100, 50.2 and 0 with a = b = 0: white, the grey of that lightness, black. Neutral in, neutral out.
        var w = new PSDTestWriter { Mode = 9, Width = 3, Height = 1, Composite = [[255, 128, 0], [128, 128, 128], [128, 128, 128]] };
        var (state, _) = Open(w);
        double l = 128 / 255.0 * 100, y = Math.Pow((l + 16) / 116, 3);
        Near(Vector4.One, At(state, 0, 0));
        Near(new Vector4((float)y, (float)y, (float)y, 1), At(state, 1, 0));
        Near(new Vector4(0, 0, 0, 1), At(state, 2, 0));
    }

    [Fact]
    public void CmykIsShownWithoutColourManagement()
    {
        // Stored inverted: 255 is no ink. Full cyan alone, then no ink at all, then full black.
        var w = new PSDTestWriter { Mode = 4, Channels = 4, Width = 3, Height = 1, Composite = [[0, 255, 255], [255, 255, 255], [255, 255, 255], [255, 255, 0]] };
        var (state, _) = Open(w);
        Assert.Equal(new Vector4(0, 1, 1, 1), At(state, 0, 0));
        Assert.Equal(Vector4.One, At(state, 1, 0));
        Assert.Equal(new Vector4(0, 0, 0, 1), At(state, 2, 0));
    }

    [Fact]
    public void IndexedColourTakesItsPalette()
    {
        var palette = new byte[768];
        palette[0] = 255;          // entry 0: red
        palette[512 + 1] = 255;    // entry 1: blue
        var w = new PSDTestWriter { Mode = 2, Channels = 1, Width = 2, Height = 1, ColorModeData = palette, Composite = [[1, 0]] };
        var (state, _) = Open(w);
        Assert.Equal(new Vector4(0, 0, 1, 1), At(state, 0, 0));
        Assert.Equal(new Vector4(1, 0, 0, 1), At(state, 1, 0));
    }

    [Fact]
    public void ABitmapsRowsArePaddedToWholeBytesAndASetBitIsBlack()
    {
        // Nine pixels a row: two bytes, the second with one bit used.
        var w = new PSDTestWriter { Mode = 0, Depth = 1, Channels = 1, Width = 9, Height = 2, CompositeBytes = [[0b1010_0000, 0b1000_0000, 0, 0]] };
        var (state, _) = Open(w);
        Assert.Equal(new Vector4(0, 0, 0, 1), At(state, 0, 0));
        Assert.Equal(Vector4.One, At(state, 1, 0));
        Assert.Equal(new Vector4(0, 0, 0, 1), At(state, 2, 0));
        Assert.Equal(new Vector4(0, 0, 0, 1), At(state, 8, 0));
        Assert.Equal(Vector4.One, At(state, 0, 1));
    }

    [Theory]
    [InlineData((ushort)1, 16)]   // grey without layers
    [InlineData((ushort)8, 8)]    // duotone: its first channel, as grey
    [InlineData((ushort)7, 8)]    // multichannel: likewise
    public void GreyDuotoneAndMultichannelShowTheirFirstChannelAsGrey(ushort mode, int depth)
    {
        ushort top = depth == 16 ? (ushort)65535 : (ushort)255, middle = depth == 16 ? (ushort)32768 : (ushort)128;
        int channels = mode == 7 ? 2 : 1;
        var w = new PSDTestWriter { Mode = mode, Depth = depth, Channels = channels, Width = 3, Height = 1 };
        w.Composite = [.. Enumerable.Range(0, channels).Select(c => c == 0 ? new ushort[] { 0, middle, top } : new ushort[] { top, top, 0 })];
        var (state, _) = Open(w);
        // No profile: grey with a gamma of 2.2, as the Mac takes such a grey picture.
        float g = (float)Math.Pow(middle / (double)top, 2.2);
        Assert.Equal(new Vector4(0, 0, 0, 1), At(state, 0, 0));
        Near(new Vector4(g, g, g, 1), At(state, 1, 0));
        Assert.Equal(Vector4.One, At(state, 2, 0));
    }
}
#endif
