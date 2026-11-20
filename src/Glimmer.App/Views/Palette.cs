// Ported from App/CommandPalette.swift (PaletteRow, Palette, PaletteRowView's text). What the palette and the omnibar
// share: the searchable items, the rows for a query, and running a row.
using System.Text.Json;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.App.Shell;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views;

/// <summary>One line in the command palette or under the omnibar: an item, or "ask the assistant".</summary>
public sealed record PaletteRow(PaletteItem? Item, string? Ask)
{
    public string Id => Item?.Id ?? "ask";
    public bool IsAsk => Ask is not null;

    /// <summary>The category in small capitals (as the web app shows it).</summary>
    public string Category => IsAsk ? Loc.T("ASSISTANT") : Loc.T(Item!.Subtitle).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    public string Title => IsAsk ? Loc.F("Ask the assistant: “{0}”", Ask!) : Loc.T(Item!.Title);
    public string Symbol => IsAsk ? "sparkles" : Palette.Symbol(Item!);
    public string Shortcut => Item?.Action is PaletteAction.Command c && WindowsCommands.Registry.Descriptor(c.Id)?.Shortcut is { } s ? Shortcuts.Display(s) : "";

    /// <summary>One element per row for a screen reader: the command, its kind and shortcut.</summary>
    public string AccessibleName => IsAsk ? Title : string.Join(", ", new[] { Title, Loc.T(Item!.Subtitle), Shortcut }.Where(p => p.Length > 0));
}

public static class Palette
{
    private static IReadOnlyList<PaletteItem>? allItems;

    /// <summary>The commands, tools and presets (the editor's Palette.AllItems), each title also in the person's language
    /// as a keyword, so typing Swedish ("webben", "himmel") finds it too.</summary>
    public static IReadOnlyList<PaletteItem> AllItems => allItems ??= Glimmer.Editor.Palette.AllItems(Loc.T);

    /// <summary>Commands plus the saved assistant actions (the editor's Palette.Items, for any session).</summary>
    public static IReadOnlyList<PaletteItem> Items(IEditorSession session) =>
    [
        .. AllItems,
        .. session.AI.SavedActions.Select(a => new PaletteItem($"action.{a.Id}", a.Name, "Action", new PaletteAction.SavedAction(a.Id.ToString().ToUpperInvariant()),
                                                               ["action", "saved", "macro"])),
    ];

    public static IReadOnlyList<PaletteRow> Rows(string query, IEditorSession session, int limit = 9, bool recentsWhenEmpty = true)
    {
        var q = query.Trim();
        var items = Items(session);
        if (q.Length == 0)
        {
            // Nothing typed yet: what you used last.
            if (!recentsWhenEmpty) return [];
            return Recents.Select(id => items.FirstOrDefault(i => i.Id == id)).OfType<PaletteItem>().Select(i => new PaletteRow(i, null)).ToList();
        }
        var results = CommandSearch.Search(q, items, limit, Usage);
        var rows = results.Select(r => new PaletteRow(r.Item, null)).ToList();
        if (CommandSearch.LooksLikeAssistantRequest(q, results.Count > 0 ? results[0].Score : null)) rows.Insert(0, new PaletteRow(null, q));
        else rows.Add(new PaletteRow(null, q));
        return rows;
    }

    public static void Ask(string text, IEditorSession session)
    {
        session.ShowLayersPanel = true;
        session.InspectorTab = InspectorTab.AI;
        session.AI.Ask(text);
    }

    public static void Run(PaletteRow row, IEditorSession session)
    {
        if (row.Ask is { } q) { Ask(q, session); return; }
        if (!session.RunPaletteItem(row.Item!)) { System.Media.SystemSounds.Beep.Play(); return; }
        RecordUse(row.Item!.Id);
    }

    private static List<string> Recents => AppServices.Settings.Get(AppPreferences.PaletteRecentsKey, new List<string>());

    /// <summary>How often each item was run from the palette or the omnibar.</summary>
    private static Dictionary<string, CommandUsage> Usage
    {
        get
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, CommandUsage>>(AppServices.Settings.Get(AppPreferences.PaletteUsageKey, "{}")) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public static void RecordUse(string id)
    {
        var recents = Recents;
        recents.Remove(id);
        recents.Insert(0, id);
        AppServices.Settings.Set(AppPreferences.PaletteRecentsKey, recents.Take(6).ToList());
        var usage = Usage;
        CommandUsage.Record(id, usage);
        AppServices.Settings.Set(AppPreferences.PaletteUsageKey, JsonSerializer.Serialize(usage));
    }

    public static string Symbol(PaletteItem item) => item.Action switch
    {
        PaletteAction.Tool t => ToolIDExtensions.FromRawValue(t.Name) is { } tool ? tool.SymbolName() : "hammer",
        PaletteAction.Preset => "camera.filters",
        PaletteAction.SavedAction => "bookmark",
        PaletteAction.Command c => c.Id.FilterKind is not null ? "camera.aperture" : c.Id.AdjustmentKind is not null ? "slider.horizontal.3" : "command",
        _ => "command",
    };
}
