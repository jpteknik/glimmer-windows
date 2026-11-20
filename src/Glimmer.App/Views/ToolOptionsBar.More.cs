// Ported from App/ToolOptionsBar.swift (TextOptions, WarpTextPanel, BrushPresetsMenu, BrushPresetStore,
// BrushDynamicsButton, GradientToolOptions).
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.App.Shell;
using Glimmer.Core;
using Glimmer.Editor;
using GradientStop = Glimmer.Core.GradientStop;
using TextAlignment = Glimmer.Core.TextAlignment;

namespace Glimmer.App.Views;

public sealed partial class ToolOptionsBar
{
    // ------------------------------------------------------------------------------------------------- brush presets

    private Button BrushPresetsMenu(IEditorSession s)
    {
        var b = new Button
        {
            Style = (Style)FindResource("Glimmer.IconButton"), Foreground = (Brush)FindResource("Glimmer.AccentBrush"),
            Content = new TextBlock { Text = Loc.T("Presets") + " ▾" },
        };
        AutomationProperties.SetName(b, Loc.T("Presets"));
        b.Click += (_, _) =>
        {
            var store = BrushPresetStore.Shared;
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var (name, preset) in BrushSettings.Presets) menu.Items.Add(MenuItem(Loc.T(name), () => s.Brush = preset));
            if (store.Presets.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var p in store.Presets) menu.Items.Add(MenuItem(p.Name, () => s.Brush = p.Settings));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(Loc.T("Save Current Brush…"), () => SaveBrushPopover(b, s)));
            if (store.Presets.Count > 0)
            {
                var delete = new MenuItem { Header = Loc.T("Delete") };
                foreach (var p in store.Presets) delete.Items.Add(MenuItem(p.Name, () => store.Remove(p.Id)));
                menu.Items.Add(delete);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    private static MenuItem MenuItem(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    private void SaveBrushPopover(FrameworkElement anchor, IEditorSession s)
    {
        var name = new TextBox { Width = 180, Style = (Style)FindResource("Glimmer.Compact.TextBox"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(name, Loc.T("Brush name"));
        var save = new Button { Content = Loc.T("Save"), Style = (Style)FindResource("Glimmer.Compact.AccentButton"), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        name.TextChanged += (_, _) => save.IsEnabled = name.Text.Trim().Length > 0;
        Popup? popup = null;
        void Save()
        {
            var trimmed = name.Text.Trim();
            if (trimmed.Length == 0) return;
            BrushPresetStore.Shared.Save(s.Brush, trimmed);
            if (popup is not null) popup.IsOpen = false;
        }
        save.Click += (_, _) => Save();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12), Children = { name, save } };
        popup = Popover.Show(anchor, row);
        name.Focus();
    }

    // ------------------------------------------------------------------------------------------------- dynamics

    private Button DynamicsButton(IEditorSession s)
    {
        var button = Button("Dynamics", () => { }, accent: true);
        button.Click += (_, _) =>
        {
            var form = new FormPanel();
            var tip = form.Section("Tip");
            DynamicsRow(tip, s, "Spacing", 0.01, 2, b => b.Spacing, (b, v) => b with { Spacing = v }, Percent);
            DynamicsRow(tip, s, "Roundness", 0.05, 1, b => b.Roundness, (b, v) => b with { Roundness = v }, Percent);
            DynamicsRow(tip, s, "Angle", -180, 180, b => b.Angle, (b, v) => b with { Angle = v }, v => $"{(int)Math.Round(v)}°");
            DynamicsRow(tip, s, "Scatter", 0, 1, b => b.Scatter, (b, v) => b with { Scatter = v }, Percent);
            DynamicsRow(tip, s, "Size Jitter", 0, 1, b => b.SizeJitter, (b, v) => b with { SizeJitter = v }, Percent);
            tip.Toggle("Textured", s.Brush.Textured, v => s.Brush = s.Brush with { Textured = v });
            var pen = form.Section("Pen");
            pen.Toggle("Pressure → Size", s.Brush.PressureSize, v => s.Brush = s.Brush with { PressureSize = v });
            pen.Toggle("Pressure → Flow", s.Brush.PressureOpacity, v => s.Brush = s.Brush with { PressureOpacity = v });
            pen.Toggle("Tilt → Angle", s.Brush.TiltAngle, v => s.Brush = s.Brush with { TiltAngle = v });
            pen.Toggle("Tilt → Roundness", s.Brush.TiltRoundness, v => s.Brush = s.Brush with { TiltRoundness = v });
            pen.Toggle("Rotation → Angle", s.Brush.RotationAngle, v => s.Brush = s.Brush with { RotationAngle = v });
            Popover.Show(button, form, width: 340);
        };
        return button;
    }

    private static void DynamicsRow(FormSection section, IEditorSession s, string label, double min, double max,
                                    Func<BrushSettings, double> get, Func<BrushSettings, double, BrushSettings> set, Func<double, string> format)
    {
        var slider = new ValueSlider(label, min, max, get(s.Brush), format, width: 150);
        slider.Changed += (_, v) => s.Brush = set(s.Brush, v);
        section.Row(label, slider);
    }

    // ------------------------------------------------------------------------------------------------- gradient

    private static readonly (string Name, GradientStop[] Stops)[] GradientPresets =
    [
        ("Black to White", [new(0, new SRGBColor(0, 0, 0)), new(1, new SRGBColor(1, 1, 1))]),
        ("Sunset", [new(0, new SRGBColor(0.16, 0.07, 0.36)), new(0.5, new SRGBColor(0.91, 0.29, 0.37)), new(1, new SRGBColor(1, 0.8, 0.4))]),
        ("Ocean", [new(0, new SRGBColor(0.02, 0.1, 0.3)), new(0.6, new SRGBColor(0.05, 0.55, 0.75)), new(1, new SRGBColor(0.7, 0.95, 0.9))]),
        ("Spectrum", [new(0, new SRGBColor(1, 0, 0)), new(0.2, new SRGBColor(1, 1, 0)), new(0.4, new SRGBColor(0, 1, 0)),
                      new(0.6, new SRGBColor(0, 1, 1)), new(0.8, new SRGBColor(0, 0, 1)), new(1, new SRGBColor(1, 0, 1))]),
    ];

    private void GradientOptions(IEditorSession s)
    {
        Add(Segmented(Enum.GetValues<GradientStyle>(), g => g.Title(), () => s.GradientStyle, g => s.GradientStyle = g, "Style"));
        var items = new List<(string, Action)>
        {
            ("Foreground to Background", () => { s.GradientStops = null; s.GradientToTransparent = false; }),
            ("Foreground to Transparent", () => { s.GradientStops = null; s.GradientToTransparent = true; }),
        };
        items.AddRange(GradientPresets.Select(p => (p.Name, (Action)(() => s.GradientStops = [.. p.Stops]))));
        Add(Menu(s.GradientStops is not null ? "Custom" : s.GradientToTransparent ? "Foreground to Transparent" : "Foreground to Background", [.. items]));
        var edit = Button("Edit Stops", () => { });
        edit.Click += (_, _) =>
        {
            if (s.GradientStops is null) s.GradientStops = s.CurrentGradientStops;
            var editor = new GradientStopsEditor { Stops = s.CurrentGradientStops, Margin = new Thickness(14) };
            editor.Changed += (_, stops) => s.GradientStops = stops;
            Popover.Show(edit, editor, width: 320);
        };
        Add(edit);
        Add(Check("Reverse", () => s.GradientReverse, v => s.GradientReverse = v));
    }

    // ------------------------------------------------------------------------------------------------- text

    /// <summary>What decides the text row's parts: a text layer or not, on a path, a path to choose.</summary>
    private static string TextShape(IEditorSession s) => s.Tool != ToolID.Type ? "" : string.Join(",",
        s.ActiveLayer?.Content is TextContent, (s.ActiveLayer?.Content as TextContent)?.Text.OnPath is not null,
        (s.ActiveLayer?.Content as TextContent)?.Text.OnPathID, s.State.Paths.Length);

    private void TextOptions(IEditorSession s)
    {
        TextLayer Current()
        {
            if (s.ActiveLayer is { Content: TextContent text } layer)
            {
                var t = text.Text;
                // While typing: the font, size and colour at the caret or of the selected words.
                if (s.EditingText == layer.Id && s.TextSelectionStyle is { } style) t = t with { FontName = style.FontName, Size = style.Size, Color = style.Color };
                return t;
            }
            var d = s.TextDefaults;
            return new TextLayer("", Vec2.Zero, d.FontName, d.Size, s.Foreground, d.Alignment);
        }
        void Apply(Func<TextLayer, TextLayer> change)
        {
            s.UpdateText(change);
            s.CommitLiveEdit();
        }

        var compact = (Style)FindResource("Glimmer.Compact.ComboBox");
        var family = new ComboBox { Style = compact, Width = 160, ItemsSource = FontCatalog.Families };
        AutomationProperties.SetName(family, Loc.T("Font"));
        family.SelectionChanged += (_, _) =>
        {
            if (updating || family.SelectedItem is not string f || f == FontCatalog.FamilyOf(Current().FontName)) return;
            Apply(t => t with { FontName = FontCatalog.Members(f)[0].PostScriptName });
        };
        refreshers.Add(() => family.SelectedItem = FontCatalog.FamilyOf(Current().FontName));
        Add(family);

        var face = new ComboBox { Style = compact, Width = 110 };
        AutomationProperties.SetName(face, Loc.T("Style"));
        face.SelectionChanged += (_, _) =>
        {
            if (!updating && face.SelectedItem is ComboBoxItem { Tag: string name } && name != Current().FontName) Apply(t => t with { FontName = name });
        };
        refreshers.Add(() =>
        {
            var current = Current().FontName;
            face.Items.Clear();
            foreach (var m in FontCatalog.Members(FontCatalog.FamilyOf(current))) face.Items.Add(new ComboBoxItem { Content = m.StyleName, Tag = m.PostScriptName });
            face.SelectedItem = face.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
        });
        Add(face);

        var size = new TextBox { Width = 50, Style = (Style)FindResource("Glimmer.Compact.TextBox"), HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(size, Loc.T("Font size (points)"));
        void ApplySize()
        {
            if (double.TryParse(size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && Math.Abs(v - Current().Size) > 1e-9)
                Apply(t => t with { Size = Math.Max(v, 1) });
        }
        size.LostKeyboardFocus += (_, _) => ApplySize();
        size.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(); };
        refreshers.Add(() => { if (!size.IsKeyboardFocused) size.Text = (Math.Round(Current().Size * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture); });
        Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { size, new TextBlock { Text = Loc.T("pt"), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Glimmer.MutedBrush") } },
        });

        var align = new ListBox { Style = (Style)FindResource("Glimmer.Segmented"), Width = 96 };
        AutomationProperties.SetName(align, Loc.T("Align"));
        foreach (var (a, symbol, name) in new[] { (TextAlignment.Left, "text.alignleft", "Left"), (TextAlignment.Center, "text.aligncenter", "Center"), (TextAlignment.Right, "text.alignright", "Right") })
        {
            var item = new ListBoxItem { Content = new Icon { Symbol = symbol, Size = 13 }, Tag = a };
            AutomationProperties.SetName(item, Loc.T(name));
            align.Items.Add(item);
        }
        align.SelectionChanged += (_, _) =>
        {
            if (!updating && align.SelectedItem is ListBoxItem { Tag: TextAlignment a } && a != Current().Alignment) Apply(t => t with { Alignment = a });
        };
        refreshers.Add(() => align.SelectedItem = align.Items.OfType<ListBoxItem>().FirstOrDefault(i => (TextAlignment)i.Tag == Current().Alignment));
        Add(align);

        Add(Well("Color", () => Current().Color, c => Apply(t => t with { Color = c }), null));

        if (s.ActiveLayer?.Content is TextContent { Text: var text } && (s.State.Paths.Length > 0 || text.OnPath is not null))
        {
            // On a path that no longer exists (or from an older document).
            if (text.OnPath is not null && text.OnPathID is null) Add(Button("Take Off Path", () => s.SetTextPath(null)));
            if (s.State.Paths.Length > 0)
            {
                var path = new ComboBox { Style = compact, MinWidth = 110, ToolTip = Loc.T("Run this text along a path, or take it off again") };
                AutomationProperties.SetName(path, Loc.T("On path"));
                path.Items.Add(new ComboBoxItem { Content = Loc.T("No Path"), Tag = null });
                foreach (var p in s.State.Paths) path.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
                path.SelectionChanged += (_, _) =>
                {
                    if (updating || path.SelectedItem is not ComboBoxItem item) return;
                    var id = item.Tag as Guid?;
                    if (id != (s.ActiveLayer?.Content as TextContent)?.Text.OnPathID) s.SetTextPath(id);
                };
                refreshers.Add(() => path.SelectedItem = path.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (i.Tag as Guid?) == (s.ActiveLayer?.Content as TextContent)?.Text.OnPathID));
                Add(path);
            }
        }
        if (s.ActiveLayer?.Content is TextContent { Text.OnPath: null })
        {
            var warp = Button("Warp", () => { }, symbol: "textformat.size.larger", help: "Warp Text");
            warp.Click += (_, _) =>
            {
                if (s.ActiveLayer?.Content is TextContent { Text: var t }) Popover.Show(warp, WarpPanel(s, t.Warp), width: 340);
            };
            Add(warp);
        }
    }

    /// <summary>Style, bend and distortion for the active text layer's warp envelope (WarpTextPanel).</summary>
    private static FrameworkElement WarpPanel(IEditorSession s, TextWarp? initial)
    {
        var warp = initial ?? new TextWarp(TextWarpStyle.None, 0.5);
        var form = new FormPanel();
        var section = form.Section();
        var sliders = new List<ValueSlider>();
        void Push(bool commit)
        {
            s.UpdateTextWarp(warp.Style == TextWarpStyle.None ? null : warp);
            if (commit) s.CommitLiveEdit();
        }
        var style = new ComboBox { Style = (Style)Application.Current.FindResource("Glimmer.Compact.ComboBox"), MinWidth = 140 };
        foreach (var w in Enum.GetValues<TextWarpStyle>()) style.Items.Add(new ComboBoxItem { Content = Loc.T(w.Title()), Tag = w });
        style.SelectedItem = style.Items.OfType<ComboBoxItem>().First(i => (TextWarpStyle)i.Tag == warp.Style);
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is not ComboBoxItem { Tag: TextWarpStyle w }) return;
            warp = warp with { Style = w };
            foreach (var sl in sliders) sl.IsEnabled = w != TextWarpStyle.None;
            Push(commit: true);
        };
        section.Row("Style", style);
        static string Hundredths(double v) => ((int)Math.Round(v * 100)).ToString(CultureInfo.CurrentCulture);
        void Slider(string label, Func<TextWarp, double> get, Func<TextWarp, double, TextWarp> set)
        {
            var slider = new ValueSlider(label, -1, 1, get(warp), Hundredths, width: 150, readoutWidth: 36) { IsEnabled = warp.Style != TextWarpStyle.None };
            slider.Changed += (_, v) => { warp = set(warp, v); Push(commit: false); };
            slider.Committed += (_, _) => s.CommitLiveEdit();
            sliders.Add(slider);
            section.Row(label, slider);
        }
        Slider("Bend", w => w.Bend, (w, v) => w with { Bend = v });
        Slider("Horizontal Distortion", w => w.Horizontal, (w, v) => w with { Horizontal = v });
        Slider("Vertical Distortion", w => w.Vertical, (w, v) => w with { Vertical = v });
        return form;
    }
}

/// <summary>Your own brushes, kept in the app's settings (BrushPresetStore, "brush.userPresets").</summary>
public sealed class BrushPresetStore
{
    public sealed record Preset(Guid Id, string Name, BrushSettings Settings);

    private const string Key = "brush.userPresets";
    public static BrushPresetStore Shared { get; } = new();

    private List<Preset> presets;

    private BrushPresetStore() => presets = AppServices.Settings.Get<List<Preset>>(Key, []);

    public IReadOnlyList<Preset> Presets => presets;
    /// <summary>Bumped on every change, so a view knows to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>Saves the brush; a preset with the same name is replaced.</summary>
    public void Save(BrushSettings settings, string name)
    {
        presets.RemoveAll(p => p.Name == name);
        presets.Add(new Preset(Guid.NewGuid(), name, settings));
        Persist();
    }

    public void Remove(Guid id)
    {
        presets.RemoveAll(p => p.Id == id);
        Persist();
    }

    private void Persist()
    {
        Version++;
        AppServices.Settings.Set(Key, presets);
    }
}

/// <summary>The installed fonts by family, for the Font and Style pickers (the Mac's NSFontManager families and
/// members). Built from the PostScript names the text engine knows, so every choice is a name a layer can store.</summary>
public static class FontCatalog
{
    public sealed record Member(string PostScriptName, string StyleName, int Weight, bool Italic, int Width);

    private static readonly Lazy<Dictionary<string, List<Member>>> ByFamily = new(() =>
    {
        var result = new Dictionary<string, List<Member>>(StringComparer.Ordinal);
        foreach (var name in FontResolver.InstalledPostScriptNames)
        {
            var r = FontResolver.Resolve(name);
            if (!result.TryGetValue(r.Family, out var list)) result[r.Family] = list = [];
            list.Add(new Member(name, StyleName(r.Weight, r.Italic, r.Width), r.Weight, r.Italic, r.Width));
        }
        foreach (var list in result.Values)
            list.Sort((a, b) => (Math.Abs(a.Width - 5), a.Italic, Math.Abs(a.Weight - 400), a.Weight).CompareTo((Math.Abs(b.Width - 5), b.Italic, Math.Abs(b.Weight - 400), b.Weight)));
        return result;
    });

    public static IReadOnlyList<string> Families => [.. ByFamily.Value.Keys.Order(StringComparer.CurrentCultureIgnoreCase)];

    public static string FamilyOf(string postScriptName) => FontResolver.Resolve(postScriptName).Family;

    /// <summary>A family's faces, regular first; a family the catalogue doesn't list gives the name it was asked with.</summary>
    public static IReadOnlyList<Member> Members(string family) =>
        ByFamily.Value.TryGetValue(family, out var list) && list.Count > 0 ? list : [new Member(family, "Regular", 400, false, 5)];

    /// <summary>"Bold Italic", "Light", "Condensed Black": the face as the font menus of Windows and the Mac name it.</summary>
    private static string StyleName(int weight, bool italic, int width)
    {
        string w = weight switch
        {
            <= 150 => "Thin", <= 250 => "Extra Light", <= 350 => "Light", <= 450 => "Regular", <= 550 => "Medium",
            <= 650 => "Semibold", <= 750 => "Bold", <= 850 => "Extra Bold", _ => "Black",
        };
        string stretch = width switch { <= 2 => "Extra Condensed ", <= 4 => "Condensed ", 5 => "", <= 7 => "Expanded ", _ => "Extra Expanded " };
        if (italic) return stretch + (w == "Regular" ? "Italic" : w + " Italic");
        return stretch + w;
    }
}
