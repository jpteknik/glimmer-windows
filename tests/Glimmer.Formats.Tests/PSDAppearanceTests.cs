// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/PSDAppearanceTests.swift: PSDAppearanceTests,
// PSDSettingsAppearanceTests and the opt-in PSDSampleFiles (GLIMMER_PSD_OUT). The PSDRealFiles suite of the same file is
// in PSDRealFilesTests. The measurements the Swift prints are kept in the assertion messages.
#if GLIMMER_PSD
using Glimmer.Core;
using L = Glimmer.Formats.Tests.PSDTestWriter.Layer;

namespace Glimmer.Formats.Tests;

/// <summary>An imported file is compared with the flattened picture the file itself carries.</summary>
public class PSDAppearanceTests
{
    internal static readonly PSDFile.Limits Limits = new(maxCanvasPixels: 1_000_000, maxDecodedBytes: 16_000_000);

    /// <summary>An 8 × 8 document: an opaque gradient, and over it a half-transparent red square in Multiply.
    /// <paramref name="composite"/> is what the file claims the result looks like.</summary>
    internal static PSDTestWriter Writer(int depth, bool top, string blend = "mul ", byte opacity = 255, Func<int, int, (int R, int G, int B)>? composite = null)
    {
        composite ??= (x, y) => Expected(x, y, top);
        var w = new PSDTestWriter { Depth = depth, Width = 8, Height = 8 };
        ushort Scale(int v) => depth == 16 ? (ushort)(v * 257) : (ushort)v;
        var full = new PSDFile.Rect(0, 0, 8, 8);
        ushort[] Plane(Func<int, int, int> value) => [.. Enumerable.Range(0, 64).Select(i => Scale(value(i % 8, i / 8)))];
        var layers = new List<L> { new(name: "Gradient", rect: full, planes: [(0, Plane((x, _) => 40 + x * 25)), (1, Plane((_, y) => 40 + y * 25)), (2, Plane((_, _) => 128))], compression: 1) };
        if (top)
        {
            var square = new PSDFile.Rect(2, 2, 6, 6);
            layers.Add(new L(name: "Red", rect: square, blendKey: blend, opacity: opacity,
                             planes: [(-1, Enumerable.Repeat(Scale(255), 16).ToArray()), (0, Enumerable.Repeat(Scale(255), 16).ToArray()),
                                      (1, Enumerable.Repeat(Scale(128), 16).ToArray()), (2, Enumerable.Repeat(Scale(128), 16).ToArray())], compression: 2));
        }
        w.Layers = layers;
        w.Composite = [Plane((x, y) => composite(x, y).R), Plane((x, y) => composite(x, y).G), Plane((x, y) => composite(x, y).B)];
        return w;
    }

    /// <summary>What Photoshop's Multiply gives, in gamma-encoded values: base × blend / 255.</summary>
    internal static (int R, int G, int B) Expected(int x, int y, bool top)
    {
        int r = 40 + x * 25, g = 40 + y * 25, b = 128;
        if (!top || x is < 2 or >= 6 || y is < 2 or >= 6) return (r, g, b);
        return (r, g * 128 / 255, b * 128 / 255);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void APlainLayerMatchesTheFilesOwnPicture(int depth)
    {
        var data = Writer(depth, top: false).Data();
        var (state, _) = PSDImport.Document(data, Limits);
        var difference = PSDAppearance.Difference(state, data)!.Value;
        Assert.True(difference.Mean < 1 && difference.ShareVisible == 0, $"one layer, {depth} bit: mean {difference.Mean}, visibly different {difference.ShareVisible}");
    }

    /// <summary>Multiply in a file is gamma-encoded arithmetic; the imported document blends the same way.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void ABlendedLayerMatchesWhatTheWritingProgramSaw(int depth)
    {
        var data = Writer(depth, top: true).Data();
        var (state, report) = PSDImport.Document(data, Limits);
        Assert.True(report.Notes.IsEmpty && state.BlendSpace == BlendSpace.Gamma);
        var difference = PSDAppearance.Difference(state, data)!.Value;
        Assert.True(difference.Mean < 1.5 && difference.ShareVisible == 0, $"Multiply over a gradient, {depth} bit: mean {difference.Mean}, visibly different {difference.ShareVisible}");
    }

    /// <summary>Photoshop's "version info" resource (1057) put into a generated file: its fifth byte says whether the
    /// flattened picture is real. (Seen in files from Photoshop, 2026-11-16: a file saved with Maximize Compatibility off
    /// holds a blank picture, and the report said "everything came through" beside a 100 % difference.)</summary>
    internal static byte[] Saying(byte[] data, bool hasPicture)
    {
        var output = data.ToList();
        int colour = data[26] << 24 | data[27] << 16 | data[28] << 8 | data[29];
        int at = 30 + colour;
        int old = data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
        byte[] block = [.. "8BIM"u8.ToArray(), 0x04, 0x21, 0, 0, 0, 0, 0, 6, 0, 0, 0, 1, hasPicture ? (byte)1 : (byte)0, 0];
        int length = old + block.Length;
        output.RemoveRange(at, 4);
        output.InsertRange(at, [(byte)(length >> 24), (byte)(length >> 16 & 0xFF), (byte)(length >> 8 & 0xFF), (byte)(length & 0xFF)]);
        output.InsertRange(at + 4, block);
        return [.. output];
    }

    [Fact]
    public void AFileThatSaysItHasNoPictureOfItsOwnIsNotComparedWithOne()
    {
        var plain = Writer(8, top: false).Data();
        Assert.Null(PSDFile.HasFlattenedPicture(plain));   // the tests' own files don't say
        var without = Saying(plain, hasPicture: false);
        var with = Saying(plain, hasPicture: true);
        Assert.True(PSDFile.HasFlattenedPicture(without) == false && PSDFile.HasFlattenedPicture(with) == true);
        var (state, report) = PSDImport.Document(without, Limits);
        Assert.True(report.FileHasNoPicture == true && PSDAppearance.Difference(state, without) is null);
        Assert.Equal("The file carries no flattened picture of its own (it was saved with Maximize Compatibility off), so Glimmer's picture could not be compared with it.",
                     report.Lines[^1]);
        var (stateWith, reportWith) = PSDImport.Document(with, Limits);
        Assert.True(reportWith.FileHasNoPicture is null && PSDAppearance.Difference(stateWith, with) is not null);
        // Cut anywhere in the resources, the question is answered or left open; never a crash.
        for (int length = 0; length < Math.Min(without.Length, 120); length++) _ = PSDFile.HasFlattenedPicture(without.AsMemory(0, length));
    }

    /// <summary>A half-transparent layer is where gamma and linear blending part: the imported document is set to blend
    /// as the writing program did, and that setting is what keeps the look.</summary>
    [Fact]
    public void AHalfTransparentLayerLooksAsItDidOnlyBecauseTheDocumentBlendsInGamma()
    {
        static (int, int, int) Mixed(int x, int y)
        {
            var (r, g, b) = Expected(x, y, top: false);
            if (x is < 2 or >= 6 || y is < 2 or >= 6) return (r, g, b);
            static int Mix(int under, int over) => under + (over - under) * 128 / 255;
            return (Mix(r, 255), Mix(g, 128), Mix(b, 128));
        }
        var data = Writer(8, top: true, blend: "norm", opacity: 128, composite: Mixed).Data();
        var (state, _) = PSDImport.Document(data, Limits);
        var same = PSDAppearance.Difference(state, data)!.Value;
        var other = PSDAppearance.Difference(state with { BlendSpace = BlendSpace.Linear }, data)!.Value;
        string measured = $"in gamma mean {same.Mean}, visibly different {same.ShareVisible}; in linear light mean {other.Mean}, visibly different {other.ShareVisible}";
        Assert.True(same.Mean < 1.5 && same.ShareVisible == 0, measured);
        Assert.True(other.ShareVisible > 0.1, measured);
    }

    [Fact]
    public void AFileWhosePictureDiffersFromItsLayersIsMeasuredAsDifferent()
    {
        // The file claims a flat grey picture; its layers say otherwise.
        var data = Writer(8, top: true, composite: (_, _) => (128, 128, 128)).Data();
        var (state, _) = PSDImport.Document(data, Limits);
        var difference = PSDAppearance.Difference(state, data)!.Value;
        Assert.True(difference.ShareVisible > 0.5 && difference.Mean > 10, $"mean {difference.Mean}, visibly different {difference.ShareVisible}");
        var report = new PSDImportReport("RGB, 8 bits per channel") { MeanDifference = difference.Mean, ShareVisiblyDifferent = difference.ShareVisible };
        Assert.StartsWith("Glimmer's picture differs from the one saved in the file:", report.Lines[^1]);
        report = report with { MeanDifference = 0.2, ShareVisiblyDifferent = 0 };
        Assert.Equal("Glimmer's picture matches the one saved in the file.", report.Lines[^1]);
    }

    [Fact]
    public void TheMeasureIsTheMeanAndTheShareOverEight()
    {
        byte[] a = [10, 20, 30, 255, 0, 0, 0, 255];
        byte[] b = [19, 20, 30, 255, 0, 8, 0, 255];
        var (mean, share) = PSDAppearance.Difference(a, b);
        Assert.Equal((9 + 8) / 6.0, mean, 12);
        Assert.Equal(0.5, share);   // 9 is visible, 8 is not
        Assert.Equal((0.0, 0.0), PSDAppearance.Difference([], []));
        // Laid on white: a half-transparent black is mid grey there, in 8-bit sRGB values as CoreGraphics draws it.
        using var bitmap = ExportedFiles.Srgb8([0, 0, 0, 128, 255, 0, 0, 0], 2, 1);
        var white = PSDAppearance.OnWhite(bitmap)!;
        Assert.Equal(new byte[] { 127, 127, 127, 255, 255, 255, 255, 255 }, white);
        Assert.Null(PSDAppearance.Difference(new DocumentState(PSDAppearance.LargestCompared / 1000 + 1, 1000, layers: []), Writer(8, top: false).Data()));
    }
}

/// <summary>Shapes and adjustment layers drawn by Glimmer, against what the same settings give by the book.</summary>
public class PSDSettingsAppearanceTests
{
    private static byte[] WithSettings(string name, List<(string Key, byte[] Body)> blocks, Func<int, int, (int R, int G, int B)> composite)
    {
        var w = PSDAppearanceTests.Writer(8, top: false, composite: composite);
        w.Layers.Add(new L(name: name, rect: new PSDFile.Rect(0, 0, 0, 0)) { ExtraBlocks = blocks });
        return w.Data();
    }

    [Fact]
    public void AShapeLooksAsItsOutlineAndColourSay()
    {
        (double X, double Y)[] corners = [(0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75)];
        var data = WithSettings("Rectangle 1", [("SoCo", PSDTestWriter.SolidColor(red: 255, green: 128, blue: 0)), ("vmsk", PSDTestWriter.VectorMask(corners))],
            (x, y) => x is >= 2 and < 6 && y is >= 2 and < 6 ? (255, 128, 0) : PSDAppearanceTests.Expected(x, y, top: false));
        var (state, report) = PSDImport.Document(data, PSDAppearanceTests.Limits);
        Assert.Empty(report.Notes);
        var difference = PSDAppearance.Difference(state, data)!.Value;
        Assert.True(difference.Mean < 1 && difference.ShareVisible == 0, $"a shape over a gradient: mean {difference.Mean}, visibly different {difference.ShareVisible}");
    }

    [Fact]
    public void LevelsAndInvertLookAsTheirArithmeticSays()
    {
        static int Level(int v) => (int)Math.Round(Math.Pow(Math.Clamp((v - 10) / 230.0, 0, 1), 1 / 1.2) * 255, MidpointRounding.AwayFromZero);
        var levels = WithSettings("Levels 1", [("levl", PSDTestWriter.Levels([(10, 240, 0, 255, 120)]))], (x, y) =>
        {
            var (r, g, b) = PSDAppearanceTests.Expected(x, y, top: false);
            return (Level(r), Level(g), Level(b));
        });
        var (a, _) = PSDImport.Document(levels, PSDAppearanceTests.Limits);
        var first = PSDAppearance.Difference(a, levels)!.Value;
        Assert.True(first.Mean < 1.5 && first.ShareVisible == 0, $"Levels (10, 240, gamma 1.2): mean {first.Mean}, visibly different {first.ShareVisible}");

        var invert = WithSettings("Invert 1", [("nvrt", [])], (x, y) =>
        {
            var (r, g, b) = PSDAppearanceTests.Expected(x, y, top: false);
            return (255 - r, 255 - g, 255 - b);
        });
        var (b, _) = PSDImport.Document(invert, PSDAppearanceTests.Limits);
        var second = PSDAppearance.Difference(b, invert)!.Value;
        Assert.True(second.Mean < 1.5 && second.ShareVisible == 0, $"Invert: mean {second.Mean}, visibly different {second.ShareVisible}");
    }
}

/// <summary>Writes sample Photoshop files for trying the import in the running app: GLIMMER_PSD_OUT=&lt;folder&gt;.</summary>
public class PSDSampleFiles
{
    [OptInFact("GLIMMER_PSD_OUT")]
    public void Write()
    {
        string folder = Environment.GetEnvironmentVariable("GLIMMER_PSD_OUT")!;
        Directory.CreateDirectory(folder);
        // Everything comes through: a gradient with a half-transparent red square over it.
        var clean = PSDAppearanceTests.Writer(8, top: true, blend: "norm", opacity: 128, composite: (x, y) =>
        {
            var (r, g, b) = PSDAppearanceTests.Expected(x, y, top: false);
            if (x is < 2 or >= 6 || y is < 2 or >= 6) return (r, g, b);
            return (r + (255 - r) * 128 / 255, g + (128 - g) * 128 / 255, b);
        });
        File.WriteAllBytes(Path.Combine(folder, "clean.psd"), clean.Data());
        // Things that do not come through: text, an adjustment layer, a blend mode Glimmer lacks, in a group.
        File.WriteAllBytes(Path.Combine(folder, "mixed.psd"), PSDTestWriter.Mixed().Data());
    }
}
#endif
