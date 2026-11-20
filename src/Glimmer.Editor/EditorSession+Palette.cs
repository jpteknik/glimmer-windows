// Ported from App/CommandPalette.swift (EditorSession.runPaletteItem and Palette's items) and the tool titles of
// App/DocumentView.swift (extension ToolID) and App/CanvasView.swift (CanvasNSView.availableTools).
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor;

public static class ToolTitles
{
    public static string Title(this ToolID tool) => tool switch
    {
        ToolID.Move => "Move",
        ToolID.Hand => "Hand",
        ToolID.RectangleMarquee => "Rectangular Marquee",
        ToolID.EllipseMarquee => "Elliptical Marquee",
        ToolID.Lasso => "Lasso",
        ToolID.PolygonLasso => "Polygonal Lasso",
        ToolID.MagicWand => "Magic Wand",
        ToolID.MagicSelect => "Magic Select",
        ToolID.Brush => "Brush",
        ToolID.Eraser => "Eraser",
        ToolID.PaintBucket => "Paint Bucket",
        ToolID.Gradient => "Gradient",
        ToolID.Eyedropper => "Eyedropper",
        ToolID.Crop => "Crop",
        ToolID.CloneStamp => "Clone Stamp",
        ToolID.PatternStamp => "Pattern Stamp",
        ToolID.HealingBrush => "Healing Brush",
        ToolID.Dodge => "Dodge",
        ToolID.Burn => "Burn",
        ToolID.Blur => "Blur",
        ToolID.Sharpen => "Sharpen",
        ToolID.Smudge => "Smudge",
        ToolID.FreeTransform => "Free Transform",
        ToolID.Type => "Type",
        ToolID.Pen => "Pen",
        _ => "Shape",
    };

    /// <summary>The tools on the rail, in its order (CanvasNSView.availableTools).</summary>
    public static IReadOnlyList<ToolID> Available { get; } =
    [
        ToolID.Move, ToolID.RectangleMarquee, ToolID.EllipseMarquee, ToolID.Lasso, ToolID.PolygonLasso, ToolID.MagicWand,
        ToolID.MagicSelect, ToolID.Crop, ToolID.FreeTransform, ToolID.Brush, ToolID.Eraser, ToolID.PaintBucket, ToolID.Gradient,
        ToolID.CloneStamp, ToolID.HealingBrush, ToolID.PatternStamp, ToolID.Dodge, ToolID.Burn, ToolID.Blur, ToolID.Sharpen, ToolID.Smudge,
        ToolID.Type, ToolID.Pen, ToolID.Shape, ToolID.Eyedropper, ToolID.Hand,
    ];
}

/// <summary>What the palette and the omnibar search: the commands, the tools and the presets (Palette.allItems).</summary>
public static class Palette
{
    /// <summary>Commands the menus already have elsewhere (or don't need) but the palette should find.</summary>
    public static IReadOnlySet<CommandID> PaletteOnly { get; } = new HashSet<CommandID>
    {
        CommandID.NewDocument, CommandID.OpenDocument, CommandID.ShowLayers, CommandID.ShowPaths, CommandID.ShowChannels, CommandID.ShowHistory,
    };

    public const string RecentsKey = "palette.recents";

    /// <summary>The searchable items. <paramref name="localized"/> gives a title in the user's language (the app's table),
    /// added as a keyword when it differs, so typing Swedish ("webben", "himmel") finds the command too.</summary>
    public static IReadOnlyList<PaletteItem> AllItems(Func<string, string>? localized = null)
    {
        IEnumerable<string> Local(string title)
        {
            var local = localized?.Invoke(title);
            return local is null || local == title ? [] : [local];
        }
        var items = new List<PaletteItem>();
        foreach (var d in WindowsCommands.Registry.Descriptors)
        {
            if ((d.IsSystemProvided && !PaletteOnly.Contains(d.Id)) || d.Id == CommandID.CommandPalette) continue;
            string subtitle = d.Id.AdjustmentKind is not null ? "Adjustment"
                : d.Id.FilterKind is { } f ? $"Filter › {f.Group()}"
                : char.ToUpperInvariant(d.Category.RawValue()[0]) + d.Category.RawValue()[1..];
            items.Add(new PaletteItem(d.Id.RawValue, d.Title, subtitle, new PaletteAction.Command(d.Id), [.. d.Keywords, .. Local(d.Title)]));
        }
        foreach (var t in ToolTitles.Available)
        {
            var key = t.ShortcutKey() is { } k ? $" ({char.ToUpperInvariant(k)})" : "";
            items.Add(new PaletteItem($"tool.{t.RawValue()}", t.Title(), "Tool" + key, new PaletteAction.Tool(t.RawValue()), ["tool", .. Local(t.Title())]));
        }
        foreach (var p in Preset.All)
            items.Add(new PaletteItem($"preset.{p.Id}", p.Name, "Preset", new PaletteAction.Preset(p.Id), ["look", "preset", "style", .. Local(p.Name)]));
        return items;
    }

    /// <summary>Commands plus the saved assistant actions.</summary>
    public static IReadOnlyList<PaletteItem> Items(EditorSession session, Func<string, string>? localized = null) =>
    [
        .. AllItems(localized),
        .. session.AI.SavedActions.Select(a => new PaletteItem($"action.{a.Id}", a.Name, "Action", new PaletteAction.SavedAction(a.Id.ToString().ToUpperInvariant()),
                                                               ["action", "saved", "macro"])),
    ];
}

public sealed partial class EditorSession
{
    /// <summary>Runs a palette entry (command, tool, preset or saved action). Returns false when it isn't available now.</summary>
    public bool RunPaletteItem(PaletteItem item)
    {
        switch (item.Action)
        {
            case PaletteAction.Command(var id):
                if (!CanPerform(id)) return false;
                Perform(id);
                break;
            case PaletteAction.Tool(var raw):
                if (ToolIDExtensions.FromRawValue(raw) is not { } t) return false;
                Tool = t;
                break;
            case PaletteAction.Preset(var id):
                if (Preset.All.FirstOrDefault(p => p.Id == id) is not { } preset) return false;
                AI.ApplyPreset(preset);
                break;
            case PaletteAction.SavedAction(var id):
                if (AI.SavedActions.FirstOrDefault(a => string.Equals(a.Id.ToString(), id, StringComparison.OrdinalIgnoreCase)) is not { } action) return false;
                ShowAI(AISection.Assistant);
                AI.Run(action);
                break;
            default:
                return false;
        }
        return true;
    }
}
