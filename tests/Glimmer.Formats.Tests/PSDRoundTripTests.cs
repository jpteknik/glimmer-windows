// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/PSDRoundTripTests.swift. The tiles are made from straight
// 8-bit sRGB bytes through ImageImporter, as the Swift makes a CGImage of them; "ImageIO knows what it is" is here
// "Glimmer's image reader opens it as a picture of the right size". The measurements the Swift prints are kept in the
// assertion messages.
#if GLIMMER_PSD
using System.Collections.Immutable;
using Glimmer.Core;

namespace Glimmer.Formats.Tests;

/// <summary>A Glimmer document with one of everything, written as a Photoshop file and read back: what a round trip
/// keeps, field by field and picture by picture.</summary>
public class PSDRoundTripTests
{
    private static readonly PSDFile.Limits Limits = new(maxCanvasPixels: 4_000_000, maxDecodedBytes: 64_000_000);
    private const int Width = 48, Height = 32;

    /// <summary>An opaque or half-transparent picture as tiles at an offset, from sRGB bytes.</summary>
    internal static TileGrid Tiles(int width, int height, (int X, int Y) origin, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var p = pixel(x, y);
                int i = (y * width + x) * 4;
                bytes[i] = p.R; bytes[i + 1] = p.G; bytes[i + 2] = p.B; bytes[i + 3] = p.A;
            }
        }
        return ImageImporter.Tiles(DecodedImage.FromRgba8(width, height, bytes), origin);
    }

    /// <summary>One of everything a Photoshop file from Glimmer can carry. <paramref name="extras"/> adds what it can't.</summary>
    internal static DocumentState Document(BlendSpace blend = BlendSpace.Gamma, bool extras = false)
    {
        var photo = new Layer("Foto åäö", new RasterContent(Tiles(Width, Height, (0, 0), (x, y) => ((byte)(40 + x * 4), (byte)(60 + y * 5), 150, 255))));
        var patch = new Layer("Patch", new RasterContent(Tiles(20, 12, (10, 8), (x, _) => (230, (byte)(120 + x * 4), 90, x < 4 ? (byte)128 : (byte)255))),
                              opacity: 128.0 / 255, blendMode: BlendMode.Multiply,
                              mask: Selection.Rasterize(new PixelRect(0, 0, 256, 256), (x, y) => x < 20 ? 1 : (y < 14 ? 0.5f : 0)).AsMask);
        var hidden = new Layer("Hidden", new RasterContent(Tiles(8, 8, (30, 4), (_, _) => (10, 200, 10, 255))), isVisible: false,
                               mask: new LayerMask(defaultValue: 0, isEnabled: false));
        var clipped = new Layer("Clipped", new RasterContent(Tiles(10, 10, (12, 10), (_, _) => (60, 60, 200, 255))), blendMode: BlendMode.Screen, isClipped: true);
        var fill = new Layer("Color Fill 1", new FillContent(new Fill.Solid(new SRGBColor(1, 128.0 / 255, 0))), opacity: 0.25, blendMode: BlendMode.Overlay);
        var outline = new VectorPath("Rectangle 1", [new Subpath([new PathAnchor(new Vec2(30, 18)), new PathAnchor(new Vec2(44, 18)),
                                                                  new PathAnchor(new Vec2(44, 28)), new PathAnchor(new Vec2(30, 28))], isClosed: true)]);
        var shape = new Layer("Rectangle 1", new FillContent(new Fill.Solid(new SRGBColor(0, 0.4, 0.8))), mask: outline.AsSelection.AsMask, vectorMask: outline);
        var levels = new Adjustment(AdjustmentKind.Levels) with
        {
            Levels = ImmutableDictionary<CurveChannel, LevelsChannel>.Empty
                .Add(CurveChannel.Rgb, new LevelsChannel(InBlack: 10.0 / 255, InWhite: 240.0 / 255, Gamma: 1.2))
                .Add(CurveChannel.Red, new LevelsChannel(OutBlack: 20.0 / 255, OutWhite: 235.0 / 255))
                .Add(CurveChannel.Green, new LevelsChannel()).Add(CurveChannel.Blue, new LevelsChannel()),
        };
        ImmutableArray<CurvePoint> straight = [new CurvePoint(0, 0), new CurvePoint(1, 1)];
        var curves = new Adjustment(AdjustmentKind.Curves) with
        {
            Curves = ImmutableDictionary<CurveChannel, ImmutableArray<CurvePoint>>.Empty
                .Add(CurveChannel.Rgb, [new CurvePoint(0, 0), new CurvePoint(128.0 / 255, 160.0 / 255), new CurvePoint(1, 1)])
                .Add(CurveChannel.Red, straight).Add(CurveChannel.Green, straight).Add(CurveChannel.Blue, straight),
        };
        var group = new Layer("Group 1", new GroupContent(new LayerGroup([patch, clipped, hidden, new Layer("Levels 1", new AdjustmentContent(levels))], isExpanded: false)),
                              opacity: 0.8, blendMode: BlendMode.PassThrough);
        var layers = new List<Layer>
        {
            photo, group, fill, shape, new Layer("Curves 1", new AdjustmentContent(curves), opacity: 0.5),
            new Layer("Invert 1", new AdjustmentContent(new Adjustment(AdjustmentKind.Invert)), isVisible: false),
        };
        if (extras)
        {
            var vibrance = new Adjustment(AdjustmentKind.Vibrance) with
            {
                Numbers = ImmutableDictionary<string, double>.Empty.Add("vibrance", 0.5).Add("saturation", 0),
            };
            ImmutableArray<GradientStop> stops = [new GradientStop(0, new SRGBColor(1, 0, 0)), new GradientStop(1, new SRGBColor(0, 0, 1))];
            layers.Add(new Layer("Vibrance 1", new AdjustmentContent(vibrance)));
            layers.Add(new Layer("Gradient Fill 1", new FillContent(new Fill.Gradient(new GradientFill(new Vec2(0, 0), new Vec2(Width, 0), stops))), opacity: 0.5));
        }
        return new DocumentState(Width, Height, blendSpace: blend, layers: layers);
    }

    internal static List<string> Outline(IEnumerable<Layer> layers, int depth = 0) =>
    [
        .. layers.SelectMany(layer =>
        {
            string line = new string(' ', depth * 2) + $"{layer.Name} | {RawValues<BlendMode>.Of(layer.BlendMode)} | {(int)Math.Round(layer.Opacity * 255, MidpointRounding.AwayFromZero)} | {(layer.IsVisible ? "shown" : "hidden")} | {(layer.IsClipped ? "clipped" : "-")}";
            switch (layer.Content)
            {
                case GroupContent g:
                    return (IEnumerable<string>)[line + $" | group {(g.Group.IsExpanded ? "open" : "closed")}", .. Outline(g.Group.Children, depth + 1)];
                case RasterContent:
                    line += " | pixels";
                    break;
                case AdjustmentContent a:
                    line += $" | {RawValues<AdjustmentKind>.Of(a.Adjustment.Kind)}";
                    break;
                case FillContent { Fill: Fill.Solid s }:
                    line += $" | fill {Byte(s.Color.R)},{Byte(s.Color.G)},{Byte(s.Color.B)}{(layer.VectorMask is null ? "" : " with outline")}";
                    break;
                default:
                    line += " | other";
                    break;
            }
            if (layer.Mask is { } mask && layer.VectorMask is null)
                line += $" | mask default {(int)Math.Round(mask.DefaultValue * 255, MidpointRounding.AwayFromZero)} {(mask.IsEnabled ? "on" : "off")}";
            return [line];
        }),
    ];

    private static int Byte(double v) => (int)Math.Round(v * 255, MidpointRounding.AwayFromZero);

    private static Layer? Find(string name, IEnumerable<Layer> layers)
    {
        foreach (var l in layers)
        {
            if (l.Name == name) return l;
            if (l.Content is GroupContent g && Find(name, g.Group.Children) is { } hit) return hit;
        }
        return null;
    }

    private static Adjustment? AdjustmentNamed(string name, DocumentState state) => Find(name, state.Layers)?.Content is AdjustmentContent a ? a.Adjustment : null;

    private static byte[] OnWhite(DocumentState state)
    {
        using var image = ImageExporter.FlattenedSRGB(state);
        return PSDAppearance.OnWhite(image)!;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryFieldComesBack(int depth)
    {
        var before = Document();
        var (data, report) = PSDExport.Data(before, depth);
        // The one note: the Curves layer made in Glimmer is written with points added along its curve.
        Assert.True(report.Notes.Select(n => n.Kind).SequenceEqual([PSDExportReport.Kind.CurveDrawnDifferently]) && report.LayersWritten == 10,
                    string.Join(" / ", report.Notes.Select(n => n.What)));
        var (after, imported) = PSDImport.Document(data, Limits);
        Assert.Equal(Outline(before.Layers), Outline(after.Layers));
        Assert.True(after.Width == Width && after.Height == Height && after.BlendSpace == BlendSpace.Gamma);
        // What the import says about a file Glimmer wrote: only the Curves note.
        Assert.True(imported.Notes.IsEmpty && imported.LayersKept == 10 && imported.LayersInFile == 10);
        // The outline, to the fixed-point step.
        var a = Find("Rectangle 1", after.Layers)!.VectorMask!.Subpaths[0];
        var b = Find("Rectangle 1", before.Layers)!.VectorMask!.Subpaths[0];
        Assert.True(a.IsClosed && a.Anchors.Length == b.Anchors.Length);
        foreach (var (p, q) in a.Anchors.Zip(b.Anchors))
            Assert.True(Math.Abs(p.Point.X - q.Point.X) < 0.001 && Math.Abs(p.Point.Y - q.Point.Y) < 0.001 && Math.Abs(p.HandleIn.X - q.HandleIn.X) < 0.001);
        // Levels and Curves, to the file's steps.
        var l1 = AdjustmentNamed("Levels 1", after)!;
        var l0 = AdjustmentNamed("Levels 1", before)!;
        foreach (var channel in Enum.GetValues<CurveChannel>())
        {
            var x = l1.Levels[channel];
            var y = l0.Levels[channel];
            Assert.True(Math.Abs(x.InBlack - y.InBlack) < 0.002 && Math.Abs(x.InWhite - y.InWhite) < 0.002 && Math.Abs(x.Gamma - y.Gamma) < 0.006
                        && Math.Abs(x.OutBlack - y.OutBlack) < 0.002 && Math.Abs(x.OutWhite - y.OutWhite) < 0.002, $"{channel}");
        }
        // The curve comes back with more points, drawn as other programs draw it, and follows the same line.
        var c1 = AdjustmentNamed("Curves 1", after)!;
        var c0 = AdjustmentNamed("Curves 1", before)!;
        Assert.True(c1.UsesNaturalCurve && c1.Curves[CurveChannel.Rgb].Length > 3);
        double worstCurve = 0;
        for (int i = 0; i <= 255; i++)
        {
            double t = i / 255.0;
            worstCurve = Math.Max(worstCurve, Math.Abs(CurveMath.Evaluate(c1.Curve(CurveChannel.Rgb), t, natural: true) - CurveMath.Evaluate(c0.Curve(CurveChannel.Rgb), t)));
        }
        Assert.True(worstCurve * 255 < 1.5, $"the curve read back is at most {worstCurve * 255} of 255 from the original");
        // The picture: the document before, the document after, and the file's own flattened picture.
        var trip = PSDAppearance.Difference(OnWhite(before), OnWhite(after));
        var outside = PSDAppearance.Difference(before, data)!.Value;
        Assert.True(trip.Mean < 1 && trip.ShareVisible == 0, $"before against after: mean {trip.Mean}, visibly different {trip.ShareVisible}");
        Assert.True(outside.Mean < 1 && outside.ShareVisible == 0, $"against the file's picture: mean {outside.Mean}, visibly different {outside.ShareVisible}");
        // A second trip changes nothing more: what came back is what goes out.
        var (again, _) = PSDExport.Data(after, depth);
        var (twice, _) = PSDImport.Document(again, Limits);
        Assert.Equal(Outline(after.Layers), Outline(twice.Layers));
        // The written file goes through the reader's limits like any other, and an image reader knows what it is.
        Assert.Equal(11, new PSDFile(data, Limits).Layers.Length);   // ten layers and the group's end mark
        var picture = ImageImporter.Image(data);
        Assert.Equal((Width, Height), (picture.Width, picture.Height));
    }

    [Fact]
    public void WhatTheFileCannotCarryIsSaidAndTheFlattenedPictureStillShowsIt()
    {
        var before = Document(extras: true);
        var (data, report) = PSDExport.Data(before);
        Assert.Equal(new[] { "Curves 1: curveDrawnDifferently ", "Vibrance 1: adjustmentLeftOut Vibrance", "Gradient Fill 1: writtenAsPixels a gradient fill" },
                     report.Notes.Select(n => $"{n.Layer ?? "-"}: {RawValues<PSDExportReport.Kind>.Of(n.Kind)} {n.Detail ?? ""}"));
        Assert.Equal(11, report.LayersWritten);
        var (after, _) = PSDImport.Document(data, Limits);
        Assert.Equal("Gradient Fill 1", after.Layers[^1].Name);
        Assert.DoesNotContain(after.Layers, l => l.Name == "Vibrance 1");
        // The flattened picture in the file is the document's own, Vibrance included.
        var outside = PSDAppearance.Difference(before, data)!.Value;
        Assert.True(outside.Mean < 1 && outside.ShareVisible == 0, $"mean {outside.Mean}, visibly different {outside.ShareVisible}");
        // Without Vibrance the layers that came back are close to the document without it: the gradient fill as pixels looks the same.
        var without = before with { Layers = [.. before.Layers.Where(l => l.Name != "Vibrance 1")] };
        var d = PSDAppearance.Difference(OnWhite(without), OnWhite(after));
        Assert.True(d.Mean < 1 && d.ShareVisible == 0, $"mean {d.Mean}, visibly different {d.ShareVisible}");
    }

    [Fact]
    public void ADocumentInLinearLightSaysHowDifferentItsLayersWillLookElsewhere()
    {
        var linear = Document(BlendSpace.Linear);
        var (_, report) = PSDExport.Data(linear);
        Assert.Equal(new[] { PSDExportReport.Kind.LinearBlending, PSDExportReport.Kind.CurveDrawnDifferently }, report.Notes.Select(n => n.Kind));
        var difference = report.GammaDifference!.Value;
        Assert.True(difference.Mean > 0, $"mean {difference.Mean}, visibly on {difference.ShareVisible}");
        Assert.Null(PSDExport.Data(Document()).Report.GammaDifference);
    }

    /// <summary>The other choice for a linear-light document: one merged layer, which looks right in any program.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void AMergedFileHoldsOneLayerThatLooksLikeTheDocument(int depth)
    {
        var linear = Document(BlendSpace.Linear);
        var (data, report) = PSDExport.Data(linear, depth, flattened: true);
        Assert.True(report.Notes.Select(n => n.Kind).SequenceEqual([PSDExportReport.Kind.FlattenedByChoice]) && report.LayersWritten == 1 && report.GammaDifference is null);
        var (after, imported) = PSDImport.Document(data, Limits);
        Assert.True(after.Layers.Select(l => l.Name).SequenceEqual(["Merged"]) && imported.Notes.IsEmpty);
        var trip = PSDAppearance.Difference(OnWhite(linear), OnWhite(after));
        var outside = PSDAppearance.Difference(linear, data)!.Value;
        Assert.True(trip.Mean < 1 && trip.ShareVisible == 0 && outside.Mean < 1 && outside.ShareVisible == 0,
                    $"the document against the layer read back: mean {trip.Mean}, visibly different {trip.ShareVisible}; against the flattened picture: mean {outside.Mean}, visibly different {outside.ShareVisible}");
    }

    [Fact]
    public void TheFlattenedPictureLiesOnWhiteAndTheLayersKeepTheirTransparency()
    {
        // New: half-transparent red on nothing. The file's picture is that over white, mixed in linear light; the layer
        // keeps its own pixels, straight, with their transparency.
        var red = new Layer("Red", new RasterContent(TileGrid.Solid(new System.Numerics.Vector4(0.5f, 0, 0, 0.5f), new PixelRect(0, 0, 16, 16))));
        var state = new DocumentState(300, 16, layers: [red]);
        var file = PSDExport.File(state).File;
        int at = 10;                                          // a pixel of the red tile
        int clear = 280;                                      // a pixel of no tile at all
        Assert.Equal(new byte[] { 255, 188, 188 }, file.Composite.Select(p => p.Span[at]).ToArray());   // 0.5 + 0.5 white: 1, 0.5 → 188
        Assert.Equal(new byte[] { 255, 255, 255 }, file.Composite.Select(p => p.Span[clear]).ToArray());
        var layer = file.Layers.Single();
        Assert.Equal(new PSDFile.Rect(0, 0, 16, 256), layer.Rect);   // the box around what isn't transparent: the whole tile
        Assert.Equal(new byte[] { 128, 255, 0, 0 }, new[] { -1, 0, 1, 2 }.Select(id => layer.Planes[id].Span[at]).ToArray());
        // 16 bits: the same, at 16 bits.
        var deep = PSDExport.File(state, depth: 16).File;
        Assert.Equal(0xFFFF, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(deep.Composite[0].Span[(at * 2)..]));
        Assert.InRange(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(deep.Composite[1].Span[(at * 2)..]), 48190, 48194);
    }

    [Fact]
    public void ADocumentTooLargeForTheFormatIsRefusedWithWords()
    {
        var huge = new DocumentState(30_001, 10, layers: []);
        var error = Assert.Throws<PSDFile.Problem>(() => PSDExport.Data(huge));
        Assert.Equal("A Photoshop file can be at most 30,000 pixels on a side. This document is 30001 × 10.", error.Message);
    }

    [Fact]
    public void TheNotesSayWhatTheyMeanAndTheReportComparesAsTheSwift()
    {
        Assert.Equal("It is text in Glimmer and is written as a layer of pixels that looks the same.",
                     new PSDExportReport.Note(PSDExportReport.Kind.WrittenAsPixels, "T", "text").What);
        Assert.Equal("All layers are merged into one, as chosen: the file looks right everywhere and has no layers to edit.",
                     new PSDExportReport.Note(PSDExportReport.Kind.FlattenedByChoice).What);
        var a = new PSDExportReport { LayersWritten = 2, Notes = [new(PSDExportReport.Kind.MaskSimplified, "M", ChangesLook: true)], GammaDifference = (1.5, 0.2) };
        var b = new PSDExportReport { LayersWritten = 2, Notes = [new(PSDExportReport.Kind.MaskSimplified, "M", ChangesLook: true)], GammaDifference = (1.5, 0.9) };
        Assert.Equal(a, b);   // the share isn't compared, as in Swift
        Assert.NotEqual(a, b with { LayersWritten = 3 });
    }
}
#endif
