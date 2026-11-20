// Ported from App/Intents.swift (HeadlessEditor: the document operations Shortcuts and Batch run without a window)
using System.Globalization;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>Document operations without a window: open an image or a .glimmer folder, edit, export. Batch Process and the
/// command line both go through here. Everything runs on the calling thread (never the UI thread: the on-device models
/// take a while).</summary>
public static class HeadlessEditor
{
    /// <summary>Why a file couldn't be processed, in words for the person.</summary>
    public sealed class Failure(string message) : Exception(message);

    /// <summary>A document from a file on disk: an image, or a .glimmer folder (a document from a Mac).</summary>
    public static DocumentState Document(string path)
    {
        if (string.Equals(Path.GetExtension(path.TrimEnd('/', '\\')), ".glimmer", StringComparison.OrdinalIgnoreCase))
            return GlimmerPackage.Load(path);
        return Document(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static DocumentState Document(byte[] imageData, string fileName)
    {
        var image = ImageImporter.Image(imageData);
        var tiles = ImageImporter.Tiles(image);
        var name = Path.GetFileNameWithoutExtension(fileName);
        return new DocumentState(image.Width, image.Height, layers: [new Layer(name.Length == 0 ? "Image" : name, new RasterContent(tiles))]);
    }

    /// <summary>Auto Color: a Color Balance layer that removes the cast, if there is one.</summary>
    public static DocumentState AutoColor(DocumentState state)
    {
        var image = AIController.Composite(state);
        if (SmartAnalysis.ColorCast(image) is not { } cast || !SmartAnalysis.IsNoticeableCast(cast, threshold: 0.012)) return state;
        return (state with { ActiveLayerID = state.Layers.LastOrDefault()?.Id })
            .InsertLayerAboveActive(new Layer("Auto Color", new AdjustmentContent(SmartAnalysis.CastCorrection(cast))));
    }

    /// <summary>Auto Enhance as editable adjustment layers. Returns the state unchanged when the image needs nothing.</summary>
    public static DocumentState AutoEnhance(DocumentState state)
    {
        var image = AIController.Composite(state);
        var recipe = SmartAnalysis.EnhanceRecipeFor(image);
        if (!recipe.IsNoticeable) return state;
        var children = recipe.Adjustments.Select(a => new Layer(a.Kind.Title(), new AdjustmentContent(a)));
        return (state with { ActiveLayerID = state.Layers.LastOrDefault()?.Id })
            .InsertLayerAboveActive(new Layer("Auto Enhance", new GroupContent(new LayerGroup(children, isExpanded: false)), blendMode: BlendMode.PassThrough));
    }

    /// <summary>The topmost visible pixel layer.</summary>
    public static Layer PixelLayer(DocumentState state) =>
        state.AllLayers.Reverse().FirstOrDefault(l => l.IsRaster && l.IsVisible && !l.Tiles.IsEmpty)
        ?? throw new Failure("The image has no pixel layer to work on.");

    public static DocumentState RemoveBackground(DocumentState state)
    {
        var layer = PixelLayer(state);
        // The model it needs is never downloaded here: what is missing is said.
        var missing = ModelStore.Shared.Missing(AIController.ModelsFor("remove_background"));
        if (missing.Count > 0) throw new ModelMissingError(missing);
        var image = AIController.LayerImage(layer, state);
        var mask = SubjectSegmentation.SubjectMaskAsync(image).GetAwaiter().GetResult();
        var selection = Selection.From(AIController.Unit(mask), state.Bounds);
        return state.UpdateLayer(layer.Id, l => l with { Mask = selection.AsMask });
    }

    public static DocumentState ApplyFilter(FilterSettings settings, DocumentState state)
    {
        var layer = PixelLayer(state);
        var result = new FilterEngine().Apply(settings, layer.Tiles, state.Bounds, null);
        return state.UpdateLayer(layer.Id, l => l with { Tiles = result });
    }

    public static DocumentState ApplyPreset(Preset preset, DocumentState state)
    {
        foreach (var (id, parameters) in preset.FilterSteps)
        {
            if (FilterSettings.FromId(id, parameters) is { } f) state = ApplyFilter(f, state);
        }
        return (state with { ActiveLayerID = state.Layers.LastOrDefault()?.Id }).InsertLayerAboveActive(preset.MakeGroup());
    }

    /// <summary>Runs a saved assistant action without a window. Steps that need the interactive editor or a paid AI job
    /// aren't available here.</summary>
    public static DocumentState Run(SavedAction action, DocumentState state)
    {
        foreach (var step in action.Steps)
        {
            switch (step.Op)
            {
                case "add_adjustment":
                {
                    if (AdjustmentKind.FromRawValue(step.String("type") ?? "") is not { } kind) throw new Failure("Unknown adjustment.");
                    state = (state with { ActiveLayerID = state.Layers.LastOrDefault()?.Id })
                        .InsertLayerAboveActive(new Layer(kind.Title(), new AdjustmentContent(AIController.AdjustmentFrom(kind, step.Object("params")))));
                    break;
                }
                case "apply_filter":
                {
                    if (FilterKind.FromRawValue(step.String("type") ?? "") is not { } kind) throw new Failure("Unknown filter.");
                    var values = step.Object("params").Where(p => p.Value.AsNumber is not null).Select(p => KeyValuePair.Create(p.Key, p.Value.AsNumber!.Value));
                    state = ApplyFilter(new FilterSettings(kind, values), state);
                    break;
                }
                case "apply_preset":
                {
                    var name = (step.String("name") ?? "").ToLowerInvariant();
                    var preset = Preset.All.FirstOrDefault(p => p.Name.ToLowerInvariant() == name || p.Id == name) ?? throw new Failure("Unknown preset.");
                    state = ApplyPreset(preset, state);
                    break;
                }
                case "add_layer_effect":
                {
                    var layer = PixelLayer(state);
                    var effects = AIController.Patched(layer.Effects, step.String("type") ?? "", step.Object("params"));
                    state = state.UpdateLayer(layer.Id, l => l with { Effects = effects });
                    break;
                }
                case "add_text":
                {
                    double size = step.Number("size") ?? Math.Max(state.Height * 0.08, 12);
                    var text = new TextLayer(step.String("text") ?? "Text", default, size: size,
                                             color: HexColors.Parse(step.String("color")) ?? SRGBColor.Black, alignment: TextAlignment.Center);
                    var layout = text.LayoutSize;
                    double y = step.String("position") switch
                    {
                        "top" => state.Height * 0.08,
                        "bottom" => state.Height * 0.92 - layout.Height,
                        _ => (state.Height - layout.Height) / 2,
                    };
                    text = text with { Origin = new Vec2((state.Width - layout.Width) / 2, y) };
                    var info = new StringInfo(text.String);
                    var layerName = info.LengthInTextElements > 32 ? info.SubstringByTextElements(0, 32) : text.String;
                    state = (state with { ActiveLayerID = state.Layers.LastOrDefault()?.Id }).InsertLayerAboveActive(new Layer(layerName, new TextContent(text)));
                    break;
                }
                case "auto_enhance":
                    state = AutoEnhance(state);
                    break;
                case "auto_color":
                    state = AutoColor(state);
                    break;
                case "remove_background":
                    state = RemoveBackground(state);
                    break;
                case "select_layer":
                {
                    var key = step.String("name") ?? "";
                    if (state.AllLayers.Reverse().FirstOrDefault(l => string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase)) is { } layer)
                        state = state with { ActiveLayerID = layer.Id };
                    break;
                }
                case "select_all" or "deselect" or "set_foreground" or "new_layer":
                    continue;   // no effect on the exported image
                default:
                    throw new Failure($"The step “{step.Op}” needs the Glimmer window (or a paid AI job) and can't run without one.");
            }
        }
        return state;
    }

    /// <summary>The document flattened and encoded as <paramref name="options"/> say.</summary>
    public static byte[] Encoded(DocumentState state, ExportOptions options)
    {
        using var flat = ImageExporter.Flatten(state);
        return ImageExporter.Encode(flat, options);
    }

    /// <summary>The usual file name extension of a format (jpg for JPEG, as the Mac writes it).</summary>
    public static string Extension(ExportFormat format) => format == ExportFormat.Jpeg ? "jpg" : format.FileExtension();
}
