// Ported from Packages/GlimmerKit/Sources/GlimmerCore/CommandRegistry.swift

using System.Collections.Immutable;
using System.Text;

namespace Glimmer.Core;

/// <summary>
/// Stable identifier for a user-facing command.
/// </summary>
/// <remarks>
/// One registry drives the menu bar, the Ctrl+K palette, the omnibar, keyboard shortcuts and the AI agent's tool list.
/// </remarks>
public readonly record struct CommandID(string RawValue)
{
    public static implicit operator CommandID(string rawValue) => new(rawValue);

    public override string ToString() => RawValue;

    public static CommandID Undo => "edit.undo";
    public static CommandID Redo => "edit.redo";

    public static CommandID ZoomIn => "view.zoomIn";
    public static CommandID ZoomOut => "view.zoomOut";
    public static CommandID FitToWindow => "view.fit";
    public static CommandID ActualSize => "view.actualSize";
    public static CommandID RotateViewLeft => "view.rotateLeft";
    public static CommandID RotateViewRight => "view.rotateRight";
    public static CommandID ResetViewRotation => "view.resetRotation";
    public static CommandID ToggleLayersPanel => "view.toggleLayers";

    public static CommandID PlaceImage => "file.placeImage";
    public static CommandID Export => "file.export";

    public static CommandID NewLayer => "layer.new";
    public static CommandID DuplicateLayer => "layer.duplicate";
    public static CommandID DeleteLayer => "layer.delete";
    public static CommandID BringLayerForward => "layer.forward";
    public static CommandID SendLayerBackward => "layer.backward";
    public static CommandID BringLayerToFront => "layer.front";
    public static CommandID SendLayerToBack => "layer.back";
    public static CommandID NewGroup => "layer.newGroup";
    public static CommandID GroupLayers => "layer.group";
    public static CommandID UngroupLayers => "layer.ungroup";
    public static CommandID ToggleClipping => "layer.clip";
    public static CommandID AddMaskRevealAll => "layer.mask.revealAll";
    public static CommandID AddMaskHideAll => "layer.mask.hideAll";
    public static CommandID ToggleMask => "layer.mask.toggle";
    public static CommandID DeleteMask => "layer.mask.delete";
    public static CommandID EditMask => "layer.mask.edit";
    public static CommandID ConvertToSmartObject => "layer.smart.convert";
    public static CommandID RasterizeLayer => "layer.rasterize";
    public static CommandID LayerStyle => "layer.style";
    public static CommandID ToggleGammaBlending => "image.gammaBlending";

    public static CommandID SelectAll => "select.all";
    public static CommandID Deselect => "select.none";
    public static CommandID InvertSelection => "select.invert";
    public static CommandID SelectSubject => "select.subject";
    public static CommandID FeatherSelection => "select.feather";
    public static CommandID GrowSelection => "select.grow";
    public static CommandID ShrinkSelection => "select.shrink";
    public static CommandID Copy => "edit.copy";
    public static CommandID Cut => "edit.cut";
    public static CommandID LayerViaCopy => "layer.viaCopy";
    public static CommandID ClearPixels => "edit.clear";
    /// <summary>Heal Selection (only in builds with GLIMMER_HEAL).</summary>
    public static CommandID HealSelection => "edit.heal";
    public static CommandID FreeTransform => "edit.transform";
    public static CommandID Crop => "image.crop";
    public static CommandID ImageSize => "image.size";
    public static CommandID CanvasSize => "image.canvasSize";
    public static CommandID FlipHorizontal => "image.flipHorizontal";
    public static CommandID FlipVertical => "image.flipVertical";
    public static CommandID RotateRight => "image.rotateRight";
    public static CommandID RotateLeft => "image.rotateLeft";
    public static CommandID Rotate180 => "image.rotate180";
    public static CommandID AutoEnhance => "image.autoEnhance";
    public static CommandID AutoStraighten => "image.autoStraighten";
    public static CommandID SuggestCrop => "image.suggestCrop";
    public static CommandID BatchProcess => "file.batch";
    public static CommandID AutoColor => "image.autoColor";
    public static CommandID SelectPeople => "select.people";
    public static CommandID CopyImageText => "edit.copyImageText";
    public static CommandID TextToLayers => "layer.textToLayers";
    public static CommandID ExportForWeb => "file.exportForWeb";
    public static CommandID SelectSky => "select.sky";
    public static CommandID StraightenDocument => "image.straightenDocument";
    public static CommandID ToggleRulers => "view.rulers";
    public static CommandID ToggleGrid => "view.grid";
    public static CommandID ToggleSnap => "view.snap";
    public static CommandID ClearGuides => "view.clearGuides";

    public static CommandID AiGenerate => "ai.generate";
    public static CommandID AiEditImage => "ai.edit";
    public static CommandID AiGenerativeFill => "ai.fill";
    public static CommandID AiRemoveObject => "ai.remove";
    public static CommandID AiExpand => "ai.expand";
    public static CommandID AiRemoveBackground => "ai.cutout";
    public static CommandID AiColorMatch => "ai.colorMatch";
    public static CommandID ShowAssistant => "ai.assistant";
    public static CommandID AiRelight => "ai.relight";
    public static CommandID AiReflections => "ai.reflections";
    public static CommandID AiHarmonize => "ai.harmonize";
    public static CommandID AiUpscale => "ai.upscale";
    public static CommandID AiDistractions => "ai.distractions";
    public static CommandID AiPresets => "ai.presets";
    public static CommandID AiLensBlur => "ai.lensBlur";

    public static CommandID Filter(FilterKind kind) => new($"filter.{kind.RawValue()}");
    public static CommandID Liquify => "filter.liquify";
    public static CommandID CommandPalette => "view.palette";
    public static CommandID ToggleNavigator => "view.navigator";
    public static CommandID ToggleClassicLayout => "view.classicLayout";
    public static CommandID RepeatFilter => "filter.repeat";
    public static CommandID HideInterface => "view.hideInterface";
    public static CommandID FillForeground => "edit.fillForeground";
    public static CommandID FillBackground => "edit.fillBackground";
    public static CommandID FillPattern => "edit.fillPattern";
    public static CommandID ExportWebProject => "file.exportWebProject";
    public static CommandID ImportReport => "file.importReport";
    public static CommandID DevelopRaw => "layer.developRaw";
    public static CommandID ExportPNG => "file.exportPNG";
    public static CommandID NewDocument => "file.new";
    public static CommandID OpenDocument => "file.open";
    public static CommandID ShowLayers => "view.showLayers";
    public static CommandID ShowPaths => "view.showPaths";
    public static CommandID ShowChannels => "view.showChannels";
    public static CommandID ShowHistory => "view.showHistory";

    /// <summary>The filter for a <see cref="Filter(Glimmer.Core.FilterKind)"/> id.</summary>
    public FilterKind? FilterKind =>
        RawValue.StartsWith("filter.", StringComparison.Ordinal) ? RawValues<FilterKind>.Parse(RawValue["filter.".Length..]) : null;

    public static CommandID NewAdjustment(AdjustmentKind kind) => new($"layer.adjustment.{kind.RawValue()}");
    public static CommandID NewSolidFill => "layer.fill.solid";
    public static CommandID NewGradientFill => "layer.fill.gradient";
    public static CommandID NewPatternFill => "layer.fill.pattern";

    /// <summary>The adjustment kind for a <see cref="NewAdjustment"/> id.</summary>
    public AdjustmentKind? AdjustmentKind =>
        RawValue.StartsWith("layer.adjustment.", StringComparison.Ordinal)
            ? RawValues<AdjustmentKind>.Parse(RawValue["layer.adjustment.".Length..])
            : null;
}

public enum CommandCategory
{
    [RawValue("file")] File,
    [RawValue("edit")] Edit,
    [RawValue("view")] View,
    [RawValue("image")] Image,
    [RawValue("layer")] Layer,
    [RawValue("select")] Select,
    [RawValue("filter")] Filter,
    [RawValue("ai")] AI,
}

public static class CommandCategoryExtensions
{
    extension(CommandCategory)
    {
        public static IReadOnlyList<CommandCategory> AllCases => RawValues<CommandCategory>.AllCases;
    }

    public static string RawValue(this CommandCategory category) => RawValues<CommandCategory>.Of(category);
}

/// <summary>Modifier keys of a Windows shortcut.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
}

/// <summary>
/// A keyboard shortcut as the Windows app shows and handles it. The Mac's shortcuts are mapped the way Photoshop maps
/// its own between the two: ⌘ → Ctrl, ⌥ → Alt, ⇧ → Shift (see the table in <see cref="CommandRegistry.Standard"/>).
/// </summary>
public readonly record struct KeyShortcut(char Key, KeyModifiers Modifiers = KeyModifiers.Control)
{
    /// <summary>"Ctrl+Alt+Shift+W": the modifiers in Windows order, then the key (letters in upper case).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        sb.Append(char.ToUpperInvariant(Key));
        return sb.ToString();
    }
}

public sealed record CommandDescriptor
{
    public CommandDescriptor(CommandID id, string title, CommandCategory category, KeyShortcut? shortcut = null,
                             IEnumerable<string>? keywords = null, bool isSystemProvided = false)
    {
        Id = id;
        Title = title;
        Category = category;
        Shortcut = shortcut;
        Keywords = keywords is null ? [] : [.. keywords];
        IsSystemProvided = isSystemProvided;
    }

    public CommandID Id { get; }
    public string Title { get; }
    public CommandCategory Category { get; }
    public KeyShortcut? Shortcut { get; }
    /// <summary>Extra search terms for the palette.</summary>
    public ImmutableArray<string> Keywords { get; }
    /// <summary>
    /// Provided by the Mac's system menus (e.g. Undo in the Edit menu), so the Mac app doesn't add it to its own. Kept as
    /// the Mac has it; the Windows app has no system menus and builds these items itself.
    /// </summary>
    public bool IsSystemProvided { get; }

    public bool Equals(CommandDescriptor? other) =>
        other is not null && Id == other.Id && Title == other.Title && Category == other.Category && Shortcut == other.Shortcut
        && Seq.Equal(Keywords, other.Keywords) && IsSystemProvided == other.IsSystemProvided;

    public override int GetHashCode() => HashCode.Combine(Id, Title, Category, Shortcut, Seq.Hash(Keywords), IsSystemProvided);
}

public sealed class CommandRegistry
{
    public CommandRegistry(IEnumerable<CommandDescriptor> descriptors) => Descriptors = [.. descriptors];

    public ImmutableArray<CommandDescriptor> Descriptors { get; }

    public CommandDescriptor? Descriptor(CommandID id) => Descriptors.FirstOrDefault(d => d.Id == id);

    public IEnumerable<CommandDescriptor> DescriptorsIn(CommandCategory category) => Descriptors.Where(d => d.Category == category);

    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift;
    private const KeyModifiers CtrlAlt = KeyModifiers.Control | KeyModifiers.Alt;
    private const KeyModifiers CtrlAltShift = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;

    private static KeyShortcut K(char key, KeyModifiers modifiers = Ctrl) => new(key, modifiers);

    /// <summary>The app's commands.</summary>
    /// <remarks>
    /// The Mac's shortcuts, mapped for Windows. ⌘ → Ctrl, ⌥ → Alt and ⇧ → Shift throughout; the one ⌃ combination
    /// follows Photoshop for Windows. No two shortcuts collide after the mapping, and Ctrl+K stays the command palette.
    /// <code>
    /// Command                      Mac        Windows
    /// Undo                         ⌘Z         Ctrl+Z
    /// Redo                         ⇧⌘Z        Ctrl+Shift+Z      (Photoshop's; Ctrl+Y is not added)
    /// Zoom In / Out                ⌘+ / ⌘-    Ctrl++ / Ctrl+-
    /// Fit on Screen                ⌘0         Ctrl+0
    /// Actual Pixels                ⌘1         Ctrl+1
    /// Rulers                       ⌘R         Ctrl+R
    /// Grid                         ⌘'         Ctrl+'
    /// Snap                         ⇧⌘;        Ctrl+Shift+;
    /// Command Palette              ⌘K         Ctrl+K
    /// Navigator                    ⌥⌘N        Ctrl+Alt+N
    /// Show/Hide Layers             ⌥⌘L        Ctrl+Alt+L
    /// Place Image                  ⌥⌘P        Ctrl+Alt+P
    /// Export                       ⌥⇧⌘W       Ctrl+Alt+Shift+W  (Photoshop's Export As)
    /// New / Open                   ⌘N / ⌘O    Ctrl+N / Ctrl+O
    /// New Layer                    ⇧⌘N        Ctrl+Shift+N
    /// Bring Forward / Backward     ⌘] / ⌘[    Ctrl+] / Ctrl+[
    /// Bring to Front / Back        ⇧⌘] / ⇧⌘[  Ctrl+Shift+] / Ctrl+Shift+[
    /// Group / Ungroup              ⌘G / ⇧⌘G   Ctrl+G / Ctrl+Shift+G
    /// Clipping Mask                ⌥⌘G        Ctrl+Alt+G
    /// Edit Layer Mask              ⌘\         Ctrl+\
    /// All / Deselect               ⌘A / ⌘D    Ctrl+A / Ctrl+D
    /// Inverse                      ⇧⌘I        Ctrl+Shift+I
    /// Copy / Cut                   ⌘C / ⌘X    Ctrl+C / Ctrl+X
    /// New Layer via Copy           ⌘J         Ctrl+J
    /// Assistant                    ⇧⌘K        Ctrl+Shift+K
    /// Free Transform               ⌘T         Ctrl+T
    /// Canvas Size                  ⌥⌘C        Ctrl+Alt+C
    /// Image Size                   ⌥⌘I        Ctrl+Alt+I
    /// Auto Enhance                 ⇧⌘L        Ctrl+Shift+L
    /// Auto Color                   ⇧⌘B        Ctrl+Shift+B
    /// Repeat Last Filter           ⌃⌘F        Ctrl+Alt+F        (⌃ → Alt, as Photoshop maps Last Filter)
    /// Liquify                      ⇧⌘X        Ctrl+Shift+X
    /// </code>
    /// </remarks>
    public static CommandRegistry Standard { get; } = new(BuildStandard());

    private static IEnumerable<CommandDescriptor> BuildStandard()
    {
        var list = new List<CommandDescriptor>
        {
            new(CommandID.Undo, "Undo", CommandCategory.Edit, K('z'), isSystemProvided: true),
            new(CommandID.Redo, "Redo", CommandCategory.Edit, K('z', CtrlShift), isSystemProvided: true),

            new(CommandID.ZoomIn, "Zoom In", CommandCategory.View, K('+'), ["magnify", "bigger"]),
            new(CommandID.ZoomOut, "Zoom Out", CommandCategory.View, K('-'), ["smaller"]),
            new(CommandID.FitToWindow, "Fit on Screen", CommandCategory.View, K('0'), ["zoom", "fit"]),
            new(CommandID.ActualSize, "Actual Pixels (100%)", CommandCategory.View, K('1'), ["zoom", "100"]),
            new(CommandID.RotateViewLeft, "Rotate View Left 15°", CommandCategory.View, keywords: ["rotate", "canvas"]),
            new(CommandID.RotateViewRight, "Rotate View Right 15°", CommandCategory.View, keywords: ["rotate", "canvas"]),
            new(CommandID.ResetViewRotation, "Reset View Rotation", CommandCategory.View, keywords: ["rotate", "straighten"]),
            new(CommandID.ToggleRulers, "Rulers", CommandCategory.View, K('r'), ["ruler", "guides"]),
            new(CommandID.ToggleGrid, "Grid", CommandCategory.View, K('\''), ["grid"]),
            new(CommandID.ToggleSnap, "Snap", CommandCategory.View, K(';', CtrlShift), ["snap"]),
            new(CommandID.ClearGuides, "Clear Guides", CommandCategory.View, keywords: ["guides", "remove"]),
            new(CommandID.CommandPalette, "Command Palette…", CommandCategory.View, K('k'), ["search", "omnibar", "find command", "ask"]),
            new(CommandID.ToggleClassicLayout, "Classic Layout", CommandCategory.View, keywords: ["docked", "panels", "sidebar", "omni"]),
            new(CommandID.ShowLayers, "Layers Panel", CommandCategory.View, keywords: ["show layers", "panel"], isSystemProvided: true),
            new(CommandID.ShowPaths, "Paths Panel", CommandCategory.View, keywords: ["show paths", "pen", "panel"], isSystemProvided: true),
            new(CommandID.ShowChannels, "Channels Panel", CommandCategory.View, keywords: ["show channels", "rgb", "alpha", "panel"],
                isSystemProvided: true),
            new(CommandID.ShowHistory, "History Panel", CommandCategory.View, keywords: ["show history", "undo", "panel"],
                isSystemProvided: true),
            new(CommandID.HideInterface, "Hide Interface", CommandCategory.View,
                keywords: ["tab", "full screen", "panels", "chrome", "clean"]),
            new(CommandID.ToggleNavigator, "Navigator", CommandCategory.View, K('n', CtrlAlt), ["minimap", "overview", "zoom"]),
            new(CommandID.ToggleLayersPanel, "Show/Hide Layers", CommandCategory.View, K('l', CtrlAlt), ["panel", "inspector"]),

            new(CommandID.PlaceImage, "Place Image…", CommandCategory.File, K('p', CtrlAlt), ["import", "open", "layer"]),
            new(CommandID.Export, "Export…", CommandCategory.File, K('w', CtrlAltShift), ["png", "jpeg", "heic", "tiff", "save as"]),

            new(CommandID.ExportPNG, "Export PNG…", CommandCategory.File, keywords: ["png", "save image", "quick export"]),
            new(CommandID.NewDocument, "New…", CommandCategory.File, K('n'), ["new document", "blank", "create"], isSystemProvided: true),
            new(CommandID.OpenDocument, "Open…", CommandCategory.File, K('o'), ["open image", "open project", "file"], isSystemProvided: true),
            new(CommandID.ExportForWeb, "Export for Web…", CommandCategory.File,
                keywords: ["web", "small file", "compress", "file size", "kb", "email", "share", "optimize"]),
            new(CommandID.BatchProcess, "Batch Process…", CommandCategory.File,
                keywords: ["batch", "folder", "many images", "bulk", "convert", "droplet", "automate"]),
            new(CommandID.ExportWebProject, "Export Web Project (.aips)…", CommandCategory.File,
                keywords: ["aips", "glimmer web", "save project", "browser"]),

            new(CommandID.NewLayer, "New Layer", CommandCategory.Layer, K('n', CtrlShift), ["add"]),
            new(CommandID.DuplicateLayer, "Duplicate Layer", CommandCategory.Layer, keywords: ["copy"]),
            new(CommandID.DeleteLayer, "Delete Layer", CommandCategory.Layer, keywords: ["remove"]),
            new(CommandID.BringLayerForward, "Bring Forward", CommandCategory.Layer, K(']'), ["arrange", "up"]),
            new(CommandID.SendLayerBackward, "Send Backward", CommandCategory.Layer, K('['), ["arrange", "down"]),
            new(CommandID.BringLayerToFront, "Bring to Front", CommandCategory.Layer, K(']', CtrlShift), ["arrange", "top"]),
            new(CommandID.SendLayerToBack, "Send to Back", CommandCategory.Layer, K('[', CtrlShift), ["arrange", "bottom"]),
            new(CommandID.NewGroup, "New Group", CommandCategory.Layer, keywords: ["folder"]),
            new(CommandID.GroupLayers, "Group Layer", CommandCategory.Layer, K('g'), ["folder"]),
            new(CommandID.UngroupLayers, "Ungroup", CommandCategory.Layer, K('g', CtrlShift)),
            new(CommandID.ToggleClipping, "Create/Release Clipping Mask", CommandCategory.Layer, K('g', CtrlAlt), ["clip"]),
            new(CommandID.AddMaskRevealAll, "Add Layer Mask (Reveal All)", CommandCategory.Layer, keywords: ["mask"]),
            new(CommandID.AddMaskHideAll, "Add Layer Mask (Hide All)", CommandCategory.Layer, keywords: ["mask"]),
            new(CommandID.ToggleMask, "Enable/Disable Layer Mask", CommandCategory.Layer, keywords: ["mask"]),
            new(CommandID.DeleteMask, "Delete Layer Mask", CommandCategory.Layer, keywords: ["mask"]),
            // Painting goes to the mask instead of the pixels: the keyboard way to click the mask thumbnail.
            new(CommandID.EditMask, "Edit Layer Mask", CommandCategory.Layer, K('\\'), ["mask", "target", "paint mask"]),
            new(CommandID.ConvertToSmartObject, "Convert to Smart Object", CommandCategory.Layer, keywords: ["smart"]),
            new(CommandID.RasterizeLayer, "Rasterize Layer", CommandCategory.Layer, keywords: ["flatten", "merge group"]),
            new(CommandID.LayerStyle, "Layer Style…", CommandCategory.Layer,
                keywords: ["effects", "fx", "shadow", "stroke", "glow", "overlay"]),
            new(CommandID.NewSolidFill, "Solid Color…", CommandCategory.Layer, keywords: ["fill"]),
            new(CommandID.NewGradientFill, "Gradient…", CommandCategory.Layer, keywords: ["fill"]),
            new(CommandID.NewPatternFill, "Pattern…", CommandCategory.Layer, keywords: ["fill"]),
            new(CommandID.SelectAll, "All", CommandCategory.Select, K('a'), ["select all"]),
            new(CommandID.Deselect, "Deselect", CommandCategory.Select, K('d'), ["select none"]),
            new(CommandID.InvertSelection, "Inverse", CommandCategory.Select, K('i', CtrlShift), ["invert selection"]),
            new(CommandID.SelectSubject, "Subject", CommandCategory.Select, keywords: ["select subject", "person", "object", "ai"]),
            new(CommandID.SelectPeople, "People", CommandCategory.Select,
                keywords: ["select people", "person", "persons", "human", "portrait", "figure"]),
            new(CommandID.SelectSky, "Sky", CommandCategory.Select,
                keywords: ["select sky", "sky replacement", "clouds", "heaven", "himmel"]),
            new(CommandID.FeatherSelection, "Feather…", CommandCategory.Select, keywords: ["soften", "blur selection"]),
            new(CommandID.GrowSelection, "Expand…", CommandCategory.Select, keywords: ["grow", "dilate"]),
            new(CommandID.ShrinkSelection, "Contract…", CommandCategory.Select, keywords: ["shrink", "erode"]),
            new(CommandID.Copy, "Copy", CommandCategory.Edit, K('c'), isSystemProvided: true),
            new(CommandID.Cut, "Cut", CommandCategory.Edit, K('x'), isSystemProvided: true),
            new(CommandID.ClearPixels, "Clear", CommandCategory.Edit, keywords: ["delete pixels", "erase selection"], isSystemProvided: true),
            new(CommandID.FillForeground, "Fill with Foreground Color", CommandCategory.Edit,
                keywords: ["fill selection", "paint bucket", "flood"]),
            new(CommandID.FillBackground, "Fill with Background Color", CommandCategory.Edit, keywords: ["fill selection"]),
            new(CommandID.FillPattern, "Fill with Pattern", CommandCategory.Edit, keywords: ["fill selection", "texture", "tile"]),
            new(CommandID.CopyImageText, "Copy Text in Image", CommandCategory.Edit,
                keywords: ["ocr", "live text", "read text", "extract text", "recognize"]),
            new(CommandID.TextToLayers, "Image Text to Layers", CommandCategory.Layer,
                keywords: ["ocr", "editable text", "live text", "convert text", "recognize", "replace text"]),
            new(CommandID.LayerViaCopy, "New Layer via Copy", CommandCategory.Layer, K('j'), ["copy selection to layer"]),

            new(CommandID.ShowAssistant, "Assistant", CommandCategory.AI, K('k', CtrlShift), ["chat", "ai", "prompt"]),
            new(CommandID.AiGenerate, "Generate Image…", CommandCategory.AI, keywords: ["text to image", "create"]),
            new(CommandID.AiEditImage, "Edit Image with Prompt…", CommandCategory.AI, keywords: ["describe", "change"]),
            new(CommandID.AiGenerativeFill, "Generative Fill…", CommandCategory.AI, keywords: ["inpaint", "replace", "selection"]),
            new(CommandID.AiRemoveObject, "Remove Object", CommandCategory.AI, keywords: ["content-aware fill", "erase", "delete", "cleanup"]),
            new(CommandID.AiExpand, "Expand…", CommandCategory.AI, keywords: ["outpaint", "extend", "uncrop"]),
            new(CommandID.AiRemoveBackground, "Remove Background", CommandCategory.AI, keywords: ["cutout", "mask", "subject"]),
            new(CommandID.AiColorMatch, "Match Color…", CommandCategory.AI, keywords: ["grade", "transfer", "reference"]),
            new(CommandID.AiRelight, "Relight…", CommandCategory.AI, keywords: ["light", "lighting", "shadow"]),
            new(CommandID.AiReflections, "Remove Reflections…", CommandCategory.AI, keywords: ["glare", "glass", "window"]),
            new(CommandID.AiHarmonize, "Harmonize Layer…", CommandCategory.AI, keywords: ["blend", "composite", "match lighting"]),
            new(CommandID.AiUpscale, "Upscale…", CommandCategory.AI, keywords: ["enlarge", "super resolution", "enhance"]),
            new(CommandID.AiDistractions, "Find Distractions…", CommandCategory.AI, keywords: ["cleanup", "clean up", "remove clutter"]),
            new(CommandID.AiPresets, "Presets…", CommandCategory.AI, keywords: ["look", "grade", "filter", "style"]),
            new(CommandID.AiLensBlur, "Lens Blur…", CommandCategory.AI, keywords: ["depth", "bokeh", "background blur", "portrait"]),

            new(CommandID.FreeTransform, "Free Transform", CommandCategory.Image, K('t'), ["scale", "rotate", "resize layer"]),
            new(CommandID.Crop, "Crop", CommandCategory.Image, keywords: ["trim"]),
            new(CommandID.CanvasSize, "Canvas Size…", CommandCategory.Image, K('c', CtrlAlt),
                ["extend", "border", "margin", "more room", "add space", "resize canvas"]),
            new(CommandID.ImageSize, "Image Size…", CommandCategory.Image, K('i', CtrlAlt),
                ["resize", "scale image", "dimensions", "pixels", "smaller", "larger", "resample"]),
            new(CommandID.AutoEnhance, "Auto Enhance", CommandCategory.Image, K('l', CtrlShift),
                ["auto tone", "auto color", "auto contrast", "fix", "improve", "magic", "levels"]),
            new(CommandID.AutoColor, "Auto Color", CommandCategory.Image, K('b', CtrlShift),
                ["white balance", "color cast", "neutral", "tint", "temperature", "fix colors", "auto white"]),
            new(CommandID.AutoStraighten, "Auto Straighten", CommandCategory.Image,
                keywords: ["level", "horizon", "tilt", "crooked", "rotate", "fix"]),
            new(CommandID.StraightenDocument, "Straighten Document", CommandCategory.Image,
                keywords: ["perspective", "keystone", "scan", "whiteboard", "receipt", "sign", "screen", "deskew"]),
            new(CommandID.SuggestCrop, "Suggest Crop", CommandCategory.Image,
                keywords: ["smart crop", "auto crop", "frame", "composition", "subject", "reframe"]),
            new(CommandID.FlipHorizontal, "Flip Horizontal", CommandCategory.Image, keywords: ["mirror", "flip canvas", "reverse"]),
            new(CommandID.FlipVertical, "Flip Vertical", CommandCategory.Image, keywords: ["mirror", "flip canvas", "upside down"]),
            new(CommandID.RotateRight, "Rotate 90° Clockwise", CommandCategory.Image, keywords: ["turn", "rotate image", "right"]),
            new(CommandID.RotateLeft, "Rotate 90° Counterclockwise", CommandCategory.Image, keywords: ["turn", "rotate image", "left"]),
            new(CommandID.Rotate180, "Rotate 180°", CommandCategory.Image, keywords: ["turn", "rotate image", "upside down"]),
            new(CommandID.ToggleGammaBlending, "Blend Colors in Gamma Space", CommandCategory.Image,
                keywords: ["photoshop", "linear", "color space"]),
            new(CommandID.RepeatFilter, "Repeat Last Filter", CommandCategory.Filter, K('f', CtrlAlt), ["again", "filter"]),
            new(CommandID.Liquify, "Liquify…", CommandCategory.Filter, K('x', CtrlShift),
                ["warp", "push", "bloat", "pucker", "twirl", "reshape"]),
        };
        list.AddRange(OptionalCommands());
        list.AddRange(RawValues<AdjustmentKind>.AllCases.Select(kind =>
            new CommandDescriptor(CommandID.NewAdjustment(kind), kind.Title(), CommandCategory.Image, keywords: ["adjustment", "layer"])));
        list.AddRange(RawValues<FilterKind>.AllCases.Select(kind =>
            new CommandDescriptor(CommandID.Filter(kind), kind.Fields().IsEmpty ? kind.Title() : $"{kind.Title()}…", CommandCategory.Filter,
                                  keywords: ["filter", kind.Group().ToLowerInvariant()])));
        return list;
    }

    /// <summary>Commands of features a release can be built without (Directory.Build.props, as Config/Features.xcconfig on the Mac).</summary>
    private static IEnumerable<CommandDescriptor> OptionalCommands()
    {
#if GLIMMER_PSD
        yield return new CommandDescriptor(CommandID.ImportReport, "Import Report…", CommandCategory.File,
                                           keywords: ["psd", "photoshop", "what was lost", "layers", "import"]);
#endif
#if GLIMMER_RAW
        yield return new CommandDescriptor(CommandID.DevelopRaw, "Develop…", CommandCategory.Layer,
                                           keywords: ["raw", "camera", "exposure", "white balance", "develop again", "dng"]);
#endif
#if GLIMMER_HEAL
        yield return new CommandDescriptor(CommandID.HealSelection, "Heal Selection", CommandCategory.Edit,
                                           keywords: ["remove object", "content-aware fill", "erase", "retouch", "spot", "dust", "wire", "cleanup",
                                                      "fill from surroundings"]);
#endif
        yield break;
    }
}
