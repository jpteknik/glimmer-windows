// Ported from App/AppCommands.swift
//
// The Mac builds its menus from CommandRegistry.standard and sends them to the focused window's session
// (perform/canPerform/isChecked). The menu bar's own items (Save, Close, Undo with the step's name, Paste) come from the
// system there. Windows has no such menus, so the commands the Mac takes from the system are listed here, with the
// same shape as the registry's descriptors, and the session performs them like any other. The menu structure itself
// (which commands go in which menu, in what order) is the Mac's GlimmerCommands.body, kept below as data.
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Commands the Windows app has that the Mac takes from its system menus (not in CommandRegistry.Standard).</summary>
public static class WindowsCommands
{
    public static CommandID Save => "file.save";
    public static CommandID SaveAs => "file.saveAs";
    /// <summary>The document as a package folder (<c>name.glimmer</c> as a directory), which a Mac opens as a document.</summary>
    public static CommandID SaveCopyForMac => "file.saveCopyForMac";
    /// <summary>A linked document's layered copy (the Mac's "Save a Glimmer Copy…", ⇧⌘S while linked).</summary>
    public static CommandID SaveGlimmerCopy => "file.saveGlimmerCopy";
    public static CommandID Close => "file.close";
    /// <summary>Edits of files from Flimmer or Jämmer that are kept and not open now (closed with Keep for Later).</summary>
    public static CommandID LinkedFilesWaiting => "file.linkedWaiting";
    /// <summary>A Glimmer document from a Mac: a folder named <c>name.glimmer</c>, which the Open dialog can't pick.</summary>
    public static CommandID OpenPackageFolder => "file.openPackageFolder";
    public static CommandID Paste => "edit.paste";

    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers CtrlShift = KeyModifiers.Control | KeyModifiers.Shift;

    /// <summary>Their descriptors, for the menus and the palette (titles are catalogue keys).</summary>
    public static IReadOnlyList<CommandDescriptor> Descriptors { get; } =
    [
        new(Save, "Save", CommandCategory.File, new KeyShortcut('s', Ctrl), ["save document", "write"]),
        new(SaveAs, "Save As…", CommandCategory.File, new KeyShortcut('s', CtrlShift), ["save copy", "rename", "glimmer file"]),
        new(SaveCopyForMac, "Save a Copy for Mac…", CommandCategory.File, keywords: ["mac", "package", "folder", "glimmer native"]),
        new(SaveGlimmerCopy, "Save a Glimmer Copy…", CommandCategory.File, keywords: ["linked", "layers", "copy"]),
        new(Close, "Close", CommandCategory.File, new KeyShortcut('w', Ctrl), isSystemProvided: true),
        new(LinkedFilesWaiting, "Linked Files Waiting…", CommandCategory.File, keywords: ["flimmer", "jämmer", "kept", "linked"]),
        new(OpenPackageFolder, "Open Package Folder…", CommandCategory.File, keywords: ["mac", "package", "folder", "glimmer native"]),
        new(Paste, "Paste", CommandCategory.Edit, new KeyShortcut('v', Ctrl), isSystemProvided: true),
    ];

    /// <summary>The registry with these added: what the Windows menus and the palette search over.</summary>
    public static CommandRegistry Registry { get; } = new([.. CommandRegistry.Standard.Descriptors, .. Descriptors]);
}

/// <summary>The Mac's menus (GlimmerCommands.body), as data the WPF menu bar is built from. Each menu is a list of entries:
/// a command, a toggle (checkmark from IsChecked), a submenu, or a separator. Titles are catalogue keys.</summary>
public static class AppMenus
{
    public abstract record Entry
    {
        public sealed record Command(CommandID Id) : Entry;
        public sealed record Toggle(CommandID Id) : Entry;
        public sealed record Submenu(string Title, IReadOnlyList<Entry> Entries) : Entry;
        public sealed record Separator : Entry;
    }

    private static Entry C(CommandID id) => new Entry.Command(id);
    private static Entry T(CommandID id) => new Entry.Toggle(id);
    private static readonly Entry Sep = new Entry.Separator();
    private static IEnumerable<Entry> Items(IEnumerable<CommandID> ids) => ids.Select(C);

    /// <summary>Menu title → entries, in menu bar order.</summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<Entry> Entries)> Menus { get; } = Build();

    private static List<(string, IReadOnlyList<Entry>)> Build()
    {
        var registry = CommandRegistry.Standard;
        var adjustments = RawValuesOf<AdjustmentKind>().Select(CommandID.NewAdjustment).ToList();
        IEnumerable<CommandID> In(CommandCategory c) => registry.DescriptorsIn(c).Where(d => !d.IsSystemProvided).Select(d => d.Id);

        var file = new List<Entry> { C(CommandID.NewDocument), C(CommandID.OpenDocument), C(WindowsCommands.OpenPackageFolder), Sep };
        file.AddRange(Items(In(CommandCategory.File)));
        file.AddRange([Sep, C(WindowsCommands.Close), C(WindowsCommands.Save), C(WindowsCommands.SaveAs), C(WindowsCommands.SaveCopyForMac),
                       C(WindowsCommands.SaveGlimmerCopy), Sep, C(WindowsCommands.LinkedFilesWaiting)]);

        var edit = new List<Entry> { C(CommandID.Undo), C(CommandID.Redo), Sep, C(CommandID.Cut), C(CommandID.Copy), C(WindowsCommands.Paste),
                                     C(CommandID.ClearPixels), Sep, C(CommandID.FillForeground), C(CommandID.FillBackground), C(CommandID.FillPattern) };
#if GLIMMER_HEAL
        edit.Add(C(CommandID.HealSelection));
#endif
        edit.AddRange([Sep, C(CommandID.CopyImageText)]);

        var hiddenToggles = new[] { CommandID.ToggleRulers, CommandID.ToggleGrid, CommandID.ToggleSnap, CommandID.ToggleNavigator,
                                    CommandID.ToggleClassicLayout, CommandID.HideInterface };
        var view = new List<Entry>(Items(In(CommandCategory.View).Where(id => !hiddenToggles.Contains(id))));
        view.AddRange([Sep, T(CommandID.HideInterface), T(CommandID.ToggleClassicLayout), T(CommandID.ToggleNavigator), T(CommandID.ToggleRulers),
                       T(CommandID.ToggleGrid), T(CommandID.ToggleSnap), Sep, C(CommandID.ShowLayers), C(CommandID.ShowPaths),
                       C(CommandID.ShowChannels), C(CommandID.ShowHistory)]);

        var image = new List<Entry>
        {
            new Entry.Submenu("Adjustments", [.. Items(adjustments)]),
            C(CommandID.AutoEnhance), C(CommandID.AutoColor), C(CommandID.AutoStraighten), Sep,
            C(CommandID.ImageSize), C(CommandID.CanvasSize), Sep,
            C(CommandID.FreeTransform), C(CommandID.Crop), C(CommandID.SuggestCrop), C(CommandID.StraightenDocument), Sep,
            C(CommandID.RotateRight), C(CommandID.RotateLeft), C(CommandID.Rotate180), C(CommandID.FlipHorizontal), C(CommandID.FlipVertical), Sep,
            T(CommandID.ToggleGammaBlending),
        };

        var filter = new List<Entry> { C(CommandID.RepeatFilter), Sep, C(CommandID.Liquify), Sep };
        foreach (var group in new[] { "Blur", "Sharpen", "Noise", "Pixelate", "Stylize", "Lens" })
            filter.Add(new Entry.Submenu(group, [.. RawValuesOf<FilterKind>().Where(k => k.Group() == group).Select(k => C(CommandID.Filter(k)))]));

        var select = new List<Entry>(Items(In(CommandCategory.Select)));
        var ai = new List<Entry>(Items(In(CommandCategory.AI)));

        var layer = new List<Entry>
        {
            new Entry.Submenu("New", [C(CommandID.NewLayer), C(CommandID.NewGroup)]),
            new Entry.Submenu("New Adjustment Layer", [.. Items(adjustments)]),
            new Entry.Submenu("New Fill Layer", [C(CommandID.NewSolidFill), C(CommandID.NewGradientFill), C(CommandID.NewPatternFill)]),
            Sep, C(CommandID.DuplicateLayer), C(CommandID.LayerViaCopy), C(CommandID.DeleteLayer), Sep,
            C(CommandID.GroupLayers), C(CommandID.UngroupLayers),
            new Entry.Submenu("Arrange", [C(CommandID.BringLayerToFront), C(CommandID.BringLayerForward), C(CommandID.SendLayerBackward), C(CommandID.SendLayerToBack)]),
            Sep, T(CommandID.ToggleClipping),
            new Entry.Submenu("Layer Mask", [C(CommandID.AddMaskRevealAll), C(CommandID.AddMaskHideAll), Sep, T(CommandID.ToggleMask), T(CommandID.EditMask), C(CommandID.DeleteMask)]),
            new Entry.Submenu("Smart Objects", [C(CommandID.ConvertToSmartObject)]),
            C(CommandID.TextToLayers), C(CommandID.RasterizeLayer),
        };
#if GLIMMER_RAW
        layer.Add(C(CommandID.DevelopRaw));
#endif
        layer.AddRange([Sep, C(CommandID.LayerStyle)]);

        return
        [
            ("File", file), ("Edit", edit), ("Image", image), ("Layer", layer), ("Select", select), ("Filter", filter), ("AI", ai), ("View", view),
        ];
    }

    private static IEnumerable<T> RawValuesOf<T>() where T : struct, Enum => Enum.GetValues<T>();
}
