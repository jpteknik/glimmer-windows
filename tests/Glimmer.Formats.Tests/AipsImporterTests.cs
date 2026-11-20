// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/AipsImporterTests.swift. The tests after the first three are
// new in the port: they pin the Mac's reading of the JSON (Foundation's casts) and the refusals the port adds where
// the Mac would stop the program.
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

public class AipsImporterTests
{
    /// <summary>A 10×10 opaque red PNG as a data URL.</summary>
    private static string RedPng()
    {
        var px = new byte[10 * 10 * 4];
        for (int i = 0; i < 100; i++) { px[i * 4] = 255; px[i * 4 + 3] = 255; }
        return "data:image/png;base64," + Convert.ToBase64String(HostileFiles.Encoded(px, 10, 10, SKEncodedImageFormat.Png));
    }

    private static JsonObject Point(double x, double y) => new() { ["x"] = x, ["y"] = y };

    private static JsonObject SampleJson()
    {
        string mask = Convert.ToBase64String([.. Enumerable.Range(0, 100).Select(i => i < 50 ? (byte)0 : (byte)255)]);
        return new JsonObject
        {
            ["magic"] = "aips", ["version"] = 1, ["width"] = 300, ["height"] = 200, ["activeLayerId"] = "r1",
            ["rootOrder"] = new JsonArray("g1", "a1"),
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "a1", ["kind"] = "adjustment", ["name"] = "Curves", ["visible"] = true, ["opacity"] = 0.5,
                    ["blendMode"] = "overlay", ["parentId"] = null, ["adjustmentType"] = "curves", ["clipping"] = false,
                    ["params"] = new JsonObject { ["rgb"] = new JsonArray(Point(0, 0), Point(0.5, 0.7), Point(1, 1)) },
                },
                new JsonObject
                {
                    ["id"] = "g1", ["kind"] = "group", ["name"] = "Group", ["visible"] = true, ["opacity"] = 1,
                    ["blendMode"] = "normal", ["parentId"] = null, ["collapsed"] = true, ["childrenIds"] = new JsonArray("r1", "s1", "t1", "e1"),
                },
                new JsonObject
                {
                    ["id"] = "r1", ["kind"] = "raster", ["name"] = "Red", ["visible"] = true, ["opacity"] = 1, ["blendMode"] = "multiply",
                    ["parentId"] = "g1", ["x"] = 20, ["y"] = 30, ["width"] = 10, ["height"] = 10, ["clipping"] = false,
                    ["pixels"] = RedPng(),
                    ["mask"] = new JsonObject { ["width"] = 10, ["height"] = 10, ["enabled"] = true, ["data"] = mask },
                    ["effects"] = new JsonObject
                    {
                        ["dropShadow"] = new JsonObject
                        {
                            ["enabled"] = true, ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 1 },
                            ["opacity"] = 0.6, ["angle"] = 45, ["distance"] = 4, ["size"] = 2,
                        },
                        ["stroke"] = new JsonObject
                        {
                            ["enabled"] = true, ["color"] = new JsonObject { ["r"] = 1, ["g"] = 1, ["b"] = 1, ["a"] = 1 },
                            ["width"] = 2, ["position"] = "inside",
                        },
                    },
                },
                new JsonObject
                {
                    ["id"] = "s1", ["kind"] = "smart", ["name"] = "Smart", ["visible"] = true, ["opacity"] = 1, ["blendMode"] = "normal",
                    ["parentId"] = "g1", ["x"] = 0, ["y"] = 0, ["width"] = 20, ["height"] = 20, ["naturalWidth"] = 10, ["naturalHeight"] = 10,
                    ["transform"] = new JsonObject { ["tx"] = 50, ["ty"] = 60, ["sx"] = 2, ["sy"] = 2, ["rot"] = 0 }, ["clipping"] = true, ["pixels"] = RedPng(),
                },
                new JsonObject
                {
                    ["id"] = "t1", ["kind"] = "text", ["name"] = "Title", ["visible"] = true, ["opacity"] = 1, ["blendMode"] = "normal",
                    ["parentId"] = "g1", ["x"] = 5, ["y"] = 5, ["clipping"] = false,
                    ["text"] = new JsonObject
                    {
                        ["text"] = "Hi", ["fontFamily"] = "Inter, system-ui", ["fontSize"] = 24,
                        ["color"] = new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 1, ["a"] = 1 }, ["align"] = "left", ["bold"] = true,
                        ["italic"] = false, ["lineHeight"] = 1.2,
                    },
                },
                new JsonObject
                {
                    ["id"] = "e1", ["kind"] = "adjustment", ["name"] = "Exposure", ["visible"] = false, ["opacity"] = 1,
                    ["blendMode"] = "normal", ["parentId"] = "g1", ["adjustmentType"] = "exposure", ["clipping"] = false,
                    ["params"] = new JsonObject { ["exposure"] = 1, ["offset"] = 0, ["gamma"] = 1 },
                }),
        };
    }

    private static byte[] Bytes(JsonNode json) => Encoding.UTF8.GetBytes(json.ToJsonString());

    private static DocumentState Import(JsonNode json) => AipsImporter.ImportProject(Bytes(json));

    [Fact]
    public void ImportsTreeAndProperties()
    {
        var state = Import(SampleJson());
        Assert.True(state.Width == 300 && state.Height == 200);
        Assert.Equal(new[] { "Group", "Curves" }, state.Layers.Select(l => l.Name));

        var curves = state.Layers[1];
        Assert.True(curves.Opacity == 0.5 && curves.BlendMode == BlendMode.Overlay);
        Assert.Equal(3, Assert.IsType<AdjustmentContent>(curves.Content).Adjustment.Curve(CurveChannel.Rgb).Length);

        var group = state.Layers[0];
        var g = Assert.IsType<GroupContent>(group.Content).Group;
        Assert.False(g.IsExpanded);
        Assert.Equal(new[] { "Red", "Smart", "Title", "Exposure" }, g.Children.Select(l => l.Name));

        var red = g.Children[0];
        Assert.Equal(BlendMode.Multiply, red.BlendMode);
        Assert.Equal(red.Id, state.ActiveLayerID);
        Assert.NotNull(red.Tiles[new TileCoord(0, 0)]);
        Assert.Equal(0.6, red.Effects.DropShadow?.Opacity);
        Assert.Equal(StrokePosition.Inside, red.Effects.Stroke?.Position);
        Assert.NotNull(red.Mask?.Tiles[new TileCoord(0, 0)]);

        var smart = Assert.IsType<SmartContent>(g.Children[1].Content).Smart;
        Assert.True(g.Children[1].IsClipped);
        Assert.Equal(new Vec2(50, 60), smart.Transform.Apply(new Vec2(0, 0)));
        Assert.Equal(new Vec2(70, 80), smart.Transform.Apply(new Vec2(10, 10)));

        var text = Assert.IsType<TextContent>(g.Children[2].Content).Text;
        Assert.True(text.String == "Hi" && text.Size == 24 && text.FontName.Contains("Bold"), text.FontName);

        var exposure = Assert.IsType<AdjustmentContent>(g.Children[3].Content).Adjustment;
        Assert.Equal(1, exposure.Number("exposure"));   // a number, not mistaken for a Bool
        Assert.False(g.Children[3].IsVisible);
    }

    [Fact]
    public void MaskValues()
    {
        var state = Import(SampleJson());
        var g = Assert.IsType<GroupContent>(state.Layers[0].Content).Group;
        var tiles = g.Children[0].Mask!.Tiles;
        Assert.NotNull(tiles[new TileCoord(0, 0)]);
        float Value(int x, int y) => TilePixels.At(tiles, x, y).W;
        Assert.Equal(0f, Value(25, 32));    // first half of the mask rows: hidden
        Assert.Equal(1f, Value(25, 37));    // second half: visible
        Assert.Equal(1f, Value(100, 100));  // outside the layer: reveal
    }

    [Fact]
    public void RejectsOtherJson()
    {
        Assert.Throws<AipsImportError>(() => AipsImporter.ImportProject("""{"magic": "nope"}"""u8.ToArray()));
    }

    // MARK: New in the port

    private static JsonObject Project(JsonArray nodes, JsonArray rootOrder, int width = 100, int height = 100) => new()
    {
        ["magic"] = "aips", ["width"] = width, ["height"] = height, ["nodes"] = nodes, ["rootOrder"] = rootOrder,
    };

    /// <summary>Foundation's casts, as the Mac reads the file: a number is a Bool only when it is exactly 1 or 0, a
    /// Bool is a number (1 or 0), a string is neither, and numbers are rounded half away from zero.</summary>
    [Fact]
    public void NumbersAndFlagsAreReadAsTheMacReadsThem()
    {
        var state = Import(Project(
        [
            new JsonObject { ["id"] = "a", ["kind"] = "raster", ["visible"] = 0, ["clipping"] = 1.0, ["opacity"] = true },
            new JsonObject { ["id"] = "b", ["kind"] = "raster", ["visible"] = 2, ["clipping"] = "yes", ["opacity"] = 7, ["blendMode"] = "pass-through" },
            new JsonObject { ["id"] = "c", ["kind"] = "text", ["x"] = 2.5, ["y"] = -2.5, ["text"] = new JsonObject { ["fontSize"] = 40 } },
        ], ["a", "b", "c"]));
        var (a, b, c) = (state.Layers[0], state.Layers[1], state.Layers[2]);
        Assert.True(!a.IsVisible && a.IsClipped && a.Opacity == 1 && a.Name == "Layer");
        Assert.True(b.IsVisible && !b.IsClipped && b.Opacity == 1 && b.BlendMode == BlendMode.Normal);   // pass-through is for groups
        var text = Assert.IsType<TextContent>(c.Content).Text;
        Assert.Equal(new Vec2(3 + 40 * 0.25, -3 + 40 * 0.1), text.Origin);
        // A size that is not a whole number is rounded too, and checked after.
        var json = Project([], []);
        json["width"] = 299.5;
        Assert.Equal(300, Import(json).Width);
    }

    [Fact]
    public void Base64IsReadAsLeniently()
    {
        // Line breaks and characters outside the alphabet are skipped, as Foundation's .ignoreUnknownCharacters does.
        string mask = Convert.ToBase64String([.. Enumerable.Range(0, 100).Select(i => i < 50 ? (byte)0 : (byte)255)]);
        string broken = string.Join("\r\n", mask.Chunk(4).Select(c => new string(c))) + "…";
        string pixels = RedPng();
        string spaced = pixels[..30] + " \n" + pixels[30..];
        var state = Import(Project(
        [
            new JsonObject
            {
                ["id"] = "r", ["kind"] = "raster", ["pixels"] = spaced,
                ["mask"] = new JsonObject { ["width"] = 10, ["height"] = 10, ["data"] = broken },
            },
        ], ["r"]));
        var layer = state.Layers[0];
        Assert.NotNull(layer.Tiles[new TileCoord(0, 0)]);
        Assert.Equal(0f, TilePixels.MaskAt(layer.Mask!, 5, 2));
        Assert.Equal(1f, TilePixels.MaskAt(layer.Mask!, 5, 7));
        // Groups of four must be whole: without its padding the picture can't be read.
        var unpadded = Project([new JsonObject { ["id"] = "r", ["kind"] = "raster", ["pixels"] = "data:image/png;base64,iVBORw0KGgo" }], ["r"]);
        var error = Assert.Throws<AipsImportError>(() => Import(unpadded));
        Assert.Equal("The .aips project is damaged: unreadable pixels", error.Message);
        // A mask whose bytes can't be read is an empty mask, as on the Mac.
        var noBytes = Project([new JsonObject { ["id"] = "r", ["kind"] = "raster", ["mask"] = new JsonObject { ["width"] = 4, ["height"] = 4, ["data"] = "AAAA", ["enabled"] = false } }], ["r"]);
        var empty = Import(noBytes).Layers[0].Mask!;
        Assert.True(empty.Tiles.IsEmpty && !empty.IsEnabled && empty.DefaultValue == 1);
    }

    [Fact]
    public void PathsTextOnAPathGuidesAndTheGrid()
    {
        var json = Project(
        [
            new JsonObject { ["id"] = "t", ["kind"] = "text", ["text"] = new JsonObject { ["text"] = "Along", ["pathId"] = "p1" } },
        ], ["t"]);
        json["paths"] = new JsonArray(new JsonObject
        {
            ["id"] = "p1", ["name"] = "Curve",
            ["subpaths"] = new JsonArray(new JsonObject
            {
                ["closed"] = false,
                ["anchors"] = new JsonArray(new JsonObject { ["x"] = 10, ["y"] = 20 }, new JsonObject { ["x"] = 110, ["y"] = 20, ["inX"] = 100, ["inY"] = 10 }),
            }),
        });
        json["guides"] = new JsonArray(new JsonObject { ["axis"] = "v", ["pos"] = 12 }, new JsonObject { ["axis"] = "h", ["pos"] = 7.5 });
        json["grid"] = new JsonObject { ["visible"] = true, ["size"] = 0.5, ["subdivisions"] = 0 };
        json["rulersVisible"] = true;
        json["snapEnabled"] = false;
        var state = Import(json);
        var path = Assert.Single(state.Paths);
        Assert.Equal("Curve", path.Name);
        var anchors = Assert.Single(path.Subpaths).Anchors;
        Assert.Equal(new PathAnchor(new Vec2(10, 20)), anchors[0]);   // handles default to the point
        Assert.Equal(new PathAnchor(new Vec2(110, 20), handleIn: new Vec2(100, 10)), anchors[1]);
        var text = Assert.IsType<TextContent>(state.Layers[0].Content).Text;
        Assert.Equal(path.Id, text.OnPathID);
        Assert.Equal(path.Polygons()[0], text.OnPath!.Value.ToArray());
        Assert.Equal(new[] { (GuideAxis.Vertical, 12.0), (GuideAxis.Horizontal, 7.5) }, state.Aids.Guides.Select(g => (g.Axis, g.Position)));
        Assert.True(state.Aids.Grid.IsVisible && state.Aids.Grid.Spacing == 1 && state.Aids.Grid.Subdivisions == 1);
        Assert.True(state.Aids.RulersVisible && !state.Aids.Snap);
        // A grid that names none of its numbers keeps the defaults.
        json["grid"] = new JsonObject { ["visible"] = false };
        var grid = Import(json).Aids.Grid;
        Assert.True(!grid.IsVisible && grid.Spacing == 100 && grid.Subdivisions == 4);
    }

    [Fact]
    public void EffectsComeAcrossWithTheWebAppsDefaults()
    {
        var state = Import(Project(
        [
            new JsonObject
            {
                ["id"] = "r", ["kind"] = "raster",
                ["effects"] = new JsonObject
                {
                    ["dropShadow"] = new JsonObject(), ["outerGlow"] = new JsonObject(), ["stroke"] = new JsonObject { ["position"] = "center" },
                    ["colorOverlay"] = new JsonObject { ["blendMode"] = "multiply", ["enabled"] = false },
                },
            },
        ], ["r"]));
        var fx = state.Layers[0].Effects;
        Assert.Equal(new ShadowEffect { BlendMode = BlendMode.Normal }, fx.DropShadow);   // the web composites effects with normal "over"
        Assert.Null(fx.InnerShadow);
        Assert.Equal(new GlowEffect { Color = new SRGBColor(0, 0, 0), BlendMode = BlendMode.Normal }, fx.OuterGlow);
        Assert.Equal(new StrokeEffect { Color = new SRGBColor(0, 0, 0), Position = StrokePosition.Center }, fx.Stroke);
        Assert.Equal(new ColorOverlayEffect { Color = new SRGBColor(0, 0, 0), BlendMode = BlendMode.Multiply, IsEnabled = false }, fx.ColorOverlay);
    }

    /// <summary>The hidden-tile finding of the Photoshop import does not apply here: the Mac's .aips import already keeps a
    /// fully hidden tile, as one colour.</summary>
    [Fact]
    public void AFullyHiddenTileOfAMaskIsKept()
    {
        var mask = new JsonObject { ["width"] = 256, ["height"] = 256, ["data"] = Convert.ToBase64String(new byte[256 * 256]) };
        var layer = Import(Project([new JsonObject { ["id"] = "r", ["kind"] = "raster", ["mask"] = mask }], ["r"], width: 512)).Layers[0];
        var tile = layer.Mask!.Tiles[new TileCoord(0, 0)];
        Assert.True(tile is { IsSolid: true } && tile.SolidColor == Vector4.Zero);
        Assert.Null(layer.Mask.Tiles[new TileCoord(1, 0)]);   // beyond the mask: the default, shown
    }

    [Fact]
    public void AHostileProjectIsRefusedOrReadButNeverStopsTheProgram()
    {
        AipsImportError Refused(byte[] data) => Assert.Throws<AipsImportError>(() => AipsImporter.ImportProject(data));
        // No JSON, or JSON that is no project.
        foreach (var data in new[] { ""u8.ToArray(), "{"u8.ToArray(), "[]"u8.ToArray(), "\"aips\""u8.ToArray(), new byte[] { 0xFF, 0xFE, 0 } })
            Assert.Equal(AipsImportError.Kind.NotAnAipsFile, Refused(data).Reason);
        // A byte order mark is skipped; something after the project is no JSON.
        Assert.Equal(100, AipsImporter.ImportProject((byte[])[0xEF, 0xBB, 0xBF, .. Bytes(Project([], []))]).Width);
        Assert.Equal(AipsImportError.Kind.NotAnAipsFile, Refused([.. Bytes(Project([], [])), .. "x"u8]).Reason);
        // A key given twice keeps its last value, as in the dictionary JSONSerialization makes (the JSON reader's own
        // objects throw an ArgumentException on it).
        var twice = """{"magic":"aips","width":1,"width":100,"height":100,"nodes":[{"id":"a","kind":"raster","name":"x","name":"y"}],"rootOrder":["a"]}"""u8.ToArray();
        var once = AipsImporter.ImportProject(twice);
        Assert.True(once.Width == 100 && once.Layers[0].Name == "y");
        // Sizes outside what a document can be, including ones the Mac's integer conversion would stop at.
        Assert.Equal("The .aips project is damaged: document size 0×100", Refused(Bytes(Project([], [], width: 0))).Message);
        var vast = Project([], []);
        vast["height"] = 1e300;
        Assert.Equal("The .aips project is damaged: document size 100×9223372036854775807", Refused(Bytes(vast)).Message);
        Assert.Equal(AipsImportError.Kind.Invalid, Refused(Bytes(Project([], [], width: DocumentManifest.MaxDimension + 1))).Reason);
        // A kind of layer the format doesn't have.
        Assert.Equal("The .aips project is damaged: unknown layer kind “blob”",
                     Refused(Bytes(Project([new JsonObject { ["id"] = "x", ["kind"] = "blob" }], ["x"]))).Message);
        // Pixels placed outside the space a document has.
        var far = Project([new JsonObject { ["id"] = "r", ["kind"] = "raster", ["x"] = 2147483648.0, ["pixels"] = RedPng() }], ["r"]);
        Assert.Equal("The .aips project is damaged: layer position 2147483648, 0", Refused(Bytes(far)).Message);
        // A root order that is not all names is no root order (Foundation's [String] cast).
        Assert.Empty(Import(Project([new JsonObject { ["id"] = "r", ["kind"] = "raster" }], ["r", 7])).Layers);
        // A mask wider than a document can be is not read, as a mask with missing bytes isn't.
        var wide = new JsonObject { ["width"] = DocumentManifest.MaxDimension + 1, ["height"] = 1, ["data"] = Convert.ToBase64String(new byte[DocumentManifest.MaxDimension + 1]) };
        Assert.True(Import(Project([new JsonObject { ["id"] = "r", ["kind"] = "raster", ["mask"] = wide }], ["r"])).Layers[0].Mask!.Tiles.IsEmpty);
    }

    [Fact]
    public void GroupsNestedTooDeepOrInACircleAreNotFollowedForever()
    {
        JsonArray Chain(int depth)
        {
            var nodes = new JsonArray();
            for (int i = 0; i < depth; i++)
                nodes.Add(new JsonObject { ["id"] = $"g{i}", ["kind"] = "group", ["childrenIds"] = new JsonArray($"g{i + 1}") });
            nodes.Add(new JsonObject { ["id"] = $"g{depth}", ["kind"] = "raster", ["name"] = "Bottom" });
            return nodes;
        }
        // Layers 64 deep are read (63 groups and what the last holds); 65 deep are refused. The Mac follows any depth
        // until its stack runs out.
        var deepest = Import(Project(Chain(63), ["g0"])).Layers[0];
        for (int i = 0; i < 63; i++) deepest = Assert.IsType<GroupContent>(deepest.Content).Group.Children[0];
        Assert.Equal("Bottom", deepest.Name);
        Assert.Equal("The .aips project is damaged: layers nested more than 64 deep", Assert.Throws<AipsImportError>(() => Import(Project(Chain(64), ["g0"]))).Message);
        // A group that holds itself holds it once.
        var circle = Import(Project([new JsonObject { ["id"] = "g", ["kind"] = "group", ["childrenIds"] = new JsonArray("g") }], ["g"]));
        Assert.Empty(Assert.IsType<GroupContent>(circle.Layers[0].Content).Group.Children);
    }

    /// <summary>The font is the installed one that Core Text would find for the CSS family, made bold and italic when
    /// the family has those members; checked on whatever family this computer has with all four.</summary>
    [Fact]
    public void TheFontIsAnInstalledOneWithTheStyleAsked()
    {
        string name = AipsImporter.FontName("No Such Family, sans-serif", bold: false, italic: false);
        Assert.True(FontResolver.Resolve(name).IsExact, name);   // what is stored names an installed font
        var faces = FontResolver.InstalledPostScriptNames.Select(FontResolver.Resolve).ToList();
        var family = faces.GroupBy(f => f.Family)
            .FirstOrDefault(g => g.Any(f => f.Weight < 600 && !f.Italic) && g.Any(f => f.Weight >= 600 && f.Italic))?.Key;
        if (family is null) return;   // no family here has a bold italic to check against
        var styled = FontResolver.Resolve(AipsImporter.FontName($"'{family}', serif", bold: true, italic: true));
        Assert.True(styled.Family == family && styled.Weight >= 600 && styled.Italic, $"{family}: {styled}");
        var plain = FontResolver.Resolve(AipsImporter.FontName(family, bold: false, italic: false));
        Assert.True(plain.Family == family && plain.Weight < 600 && !plain.Italic, $"{family}: {plain}");
    }
}
