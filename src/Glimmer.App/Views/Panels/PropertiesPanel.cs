// Ported from App/PropertiesPanel.swift (PropertiesPanel, AdjustmentEditor, FillEditor, MaskEditor). The editors are
// built once per layer and then only updated, so a slider being dragged is never replaced under the pointer.
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

/// <summary>Properties of the active layer: adjustment settings, fill settings, mask.</summary>
public sealed class PropertiesPanel : StackPanel
{
    private Guid? shownLayer;
    private Type? shownContent;
    private AdjustmentEditor? adjustment;
    private FillEditor? fill;
    private MaskEditor? mask;

    public static bool HasProperties(Layer layer) =>
        layer.Content is AdjustmentContent or FillContent || layer.Mask is not null;

    public void Show(IEditorSession session, Layer layer)
    {
        if (shownLayer != layer.Id || shownContent != layer.Content.GetType() || (mask is null) != (layer.Mask is null))
        {
            Children.Clear();
            adjustment = null;
            fill = null;
            mask = null;
            shownLayer = layer.Id;
            shownContent = layer.Content.GetType();
            switch (layer.Content)
            {
                case AdjustmentContent a:
                    adjustment = new AdjustmentEditor(session, a.Adjustment.Kind);
                    Children.Add(adjustment);
                    break;
                case FillContent f:
                    fill = new FillEditor(session, f.Fill);
                    Children.Add(fill);
                    break;
            }
            if (layer.Mask is not null)
            {
                mask = new MaskEditor(session) { Margin = new Thickness(0, Children.Count > 0 ? 14 : 0, 0, 0) };
                Children.Add(mask);
            }
        }
        if (layer.Content is AdjustmentContent adj) adjustment?.Update(adj.Adjustment);
        if (layer.Content is FillContent fl) fill?.Update(fl.Fill);
        if (layer.Mask is { } m) mask?.Update(m);
    }
}

/// <summary>Shared pieces of the editors.</summary>
internal static class EditorParts
{
    public static TextBlock Headline(string key) => new() { Text = Loc.T(key), Style = (Style)Application.Current.FindResource("Glimmer.Text.Headline"), VerticalAlignment = VerticalAlignment.Center };

    public static DockPanel Header(string titleKey, string? symbol, UIElement? trailing)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (trailing is not null) { DockPanel.SetDock(trailing, Dock.Right); header.Children.Add(trailing); }
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol is not null) label.Children.Add(new Icon { Symbol = symbol, Size = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(Headline(titleKey));
        header.Children.Add(label);
        return header;
    }

    public static Button SmallButton(string key, RoutedEventHandler click, string? help = null)
    {
        var b = new Button { Content = Loc.T(key), Style = (Style)Application.Current.FindResource("Glimmer.Compact.Button"), ToolTip = help is null ? null : Loc.T(help) };
        b.Click += click;
        return b;
    }

    public static ListBox Segmented<T>(IEnumerable<T> values, Func<T, string> title, Action<T> select) where T : notnull
    {
        var list = new ListBox { Style = (Style)Application.Current.FindResource("Glimmer.Segmented"), Margin = new Thickness(0, 0, 0, 10) };
        foreach (var v in values) list.Items.Add(new ListBoxItem { Content = Loc.T(title(v)), Tag = v });
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: T v }) select(v); };
        return list;
    }

    public static void SelectTag<T>(ListBox list, T value) =>
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));
}

public sealed class AdjustmentEditor : StackPanel
{
    private readonly IEditorSession session;
    private readonly AdjustmentKind kind;
    private Adjustment current;
    private CurveChannel channel = CurveChannel.Rgb;
    private readonly Dictionary<string, ParameterSlider> sliders = [];
    private readonly Dictionary<string, CheckBox> toggles = [];
    private readonly Dictionary<string, ColorWell> colors = [];
    private readonly ParameterSlider[] levelsSliders = [];
    private readonly CurvesEditor? curves;
    private readonly LevelsHistogram? levels;
    private readonly GradientStopsEditor? gradient;
    private bool updating;

    public AdjustmentEditor(IEditorSession session, AdjustmentKind kind)
    {
        this.session = session;
        this.kind = kind;
        current = new Adjustment(kind);
        var reset = EditorParts.SmallButton("Reset", (_, _) => Reset(), kind == AdjustmentKind.Curves ? "Reset this channel's curve" : "Reset the adjustment");
        Children.Add(EditorParts.Header(kind.Title(), kind.SymbolName(), reset));
        if (kind is AdjustmentKind.Curves or AdjustmentKind.Levels)
        {
            var picker = EditorParts.Segmented(Enum.GetValues<CurveChannel>(), c => c.Title(), c => { channel = c; Update(current); });
            AutomationProperties.SetName(picker, Loc.T("Channel"));
            EditorParts.SelectTag(picker, CurveChannel.Rgb);
            Children.Add(picker);
        }
        if (kind == AdjustmentKind.Curves)
        {
            curves = new CurvesEditor { Height = 220, Margin = new Thickness(0, 0, 0, 10) };
            curves.Changed += (_, points) => Push(current with { Curves = current.Curves.SetItem(channel, points) });
            curves.Committed += (_, _) => session.CommitLiveEdit();
            Children.Add(curves);
        }
        if (kind == AdjustmentKind.Levels)
        {
            levels = new LevelsHistogram { Height = 150, Margin = new Thickness(0, 0, 0, 10) };
            levels.Changed += (_, l) => Push(current with { Levels = current.Levels.SetItem(channel, l) });
            levels.Committed += (_, _) => session.CommitLiveEdit();
            Children.Add(levels);
            (string Label, double Min, double Max, Func<LevelsChannel, double> Get, Func<LevelsChannel, double, LevelsChannel> Set)[] fields =
            [
                ("Input Black", 0, 1, l => l.InBlack, (l, v) => l with { InBlack = v }), ("Gamma", 0.1, 9.99, l => l.Gamma, (l, v) => l with { Gamma = v }),
                ("Input White", 0, 1, l => l.InWhite, (l, v) => l with { InWhite = v }), ("Output Black", 0, 1, l => l.OutBlack, (l, v) => l with { OutBlack = v }),
                ("Output White", 0, 1, l => l.OutWhite, (l, v) => l with { OutWhite = v }),
            ];
            levelsSliders = fields.Select(f =>
            {
                var s = new ParameterSlider(f.Label, f.Min, f.Max) { Margin = new Thickness(0, 0, 0, 8), Tag = f.Get };
                s.Committed += (_, _) => session.CommitLiveEdit();
                var set = f.Set;
                ValueChangedHook(s, v => { if (!updating) Push(current with { Levels = current.Levels.SetItem(channel, set(current.LevelsChannelFor(channel), v)) }); });
                Children.Add(s);
                return s;
            }).ToArray();
        }
        if (kind == AdjustmentKind.GradientMap)
        {
            gradient = new GradientStopsEditor { Margin = new Thickness(0, 0, 0, 10) };
            gradient.Changed += (_, stops) => Push(current with { Gradient = stops });
            gradient.Committed += (_, _) => session.CommitLiveEdit();
            Children.Add(gradient);
        }
        foreach (var s in kind.Sliders())
        {
            var slider = new ParameterSlider(s.Label, s.Range.LowerBound, s.Range.UpperBound, s.Step, v => s.Step >= 1 ? ((int)v).ToString() : v.ToString("0.00")) { Margin = new Thickness(0, 0, 0, 8) };
            slider.Committed += (_, _) => session.CommitLiveEdit();
            var key = s.Key;
            ValueChangedHook(slider, v => { if (!updating) Push(current with { Numbers = current.Numbers.SetItem(key, v) }); });
            sliders[key] = slider;
            Children.Add(slider);
        }
        foreach (var t in kind.Toggles())
        {
            var box = new CheckBox { Content = Loc.T(t.Label), Style = (Style)Application.Current.FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 0, 0, 8) };
            var key = t.Key;
            box.Click += (_, _) => { Push(current with { Flags = current.Flags.SetItem(key, box.IsChecked == true) }); session.CommitLiveEdit(); };
            toggles[key] = box;
            Children.Add(box);
        }
        foreach (var c in kind.Colors())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var well = new ColorWell { Label = c.Label };
            DockPanel.SetDock(well, Dock.Right);
            var key = c.Key;
            well.Committed += (_, _) => { Push(current with { Colors = current.Colors.SetItem(key, well.Color) }); session.CommitLiveEdit(); };
            row.Children.Add(well);
            row.Children.Add(new TextBlock { Text = Loc.T(c.Label), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            colors[key] = well;
            Children.Add(row);
        }
    }

    private static void ValueChangedHook(ParameterSlider slider, Action<double> changed)
    {
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
        descriptor.AddValueChanged(slider, (_, _) => changed(slider.Value));
    }

    private void Push(Adjustment next)
    {
        current = next;
        // The step's name is English, as Swift's liveLayer("Edit \(adjustment.kind.title)"); the history looks it up.
        session.LiveLayer("Edit " + kind.Title(), l => l with { Content = new AdjustmentContent(next) });
    }

    private void Reset()
    {
        // Curves resets the channel on screen, as in the web app.
        if (kind == AdjustmentKind.Curves && (channel != CurveChannel.Rgb || current.Curves.Count > 1))
            Push(current with { Curves = current.Curves.Remove(channel) });
        else
            Push(new Adjustment(kind));
        session.CommitLiveEdit();
        Update(current);
    }

    public void Update(Adjustment adjustment)
    {
        updating = true;
        current = adjustment;
        foreach (var (key, slider) in sliders) slider.Value = adjustment.Number(key);
        foreach (var (key, box) in toggles) box.IsChecked = adjustment.Flag(key);
        foreach (var (key, well) in colors) well.Color = adjustment.Color(key);
        if (curves is not null)
        {
            curves.Channel = channel;
            curves.Points = adjustment.Curve(channel);
            curves.Others = Enum.GetValues<CurveChannel>().Where(c => c != channel && adjustment.Curves.ContainsKey(c)).Select(c => (c, (IReadOnlyList<CurvePoint>)adjustment.Curve(c))).ToList();
            curves.Natural = adjustment.UsesNaturalCurve;
        }
        if (levels is not null)
        {
            levels.Channel = channel;
            levels.Levels = adjustment.LevelsChannelFor(channel);
            var histogram = session.LevelsHistogram;
            levels.Bins = histogram is not null && session.ActiveLayer?.Id == histogram.LayerId ? histogram.Histogram.Bins(channel) : null;
            foreach (var s in levelsSliders) s.Value = ((Func<LevelsChannel, double>)s.Tag)(adjustment.LevelsChannelFor(channel));
        }
        if (gradient is not null) gradient.Stops = adjustment.Gradient;
        updating = false;
    }
}

/// <summary>A fill layer's colour, gradient stops or pattern (Glimmer.Core's Fill: Solid, Gradient or Pattern).</summary>
public sealed class FillEditor : StackPanel
{
    private readonly IEditorSession session;
    private Fill current;
    private readonly ColorWell? solid;
    private readonly GradientStopsEditor? stops;
    private readonly ListBox? patterns;
    private bool updating;

    public FillEditor(IEditorSession session, Fill fill)
    {
        this.session = session;
        current = fill;
        Children.Add(EditorParts.Header(fill.Title, null, null));
        switch (fill)
        {
            case Fill.Solid:
            {
                var row = new DockPanel();
                solid = new ColorWell { Label = "Color" };
                DockPanel.SetDock(solid, Dock.Right);
                solid.Committed += (_, _) => { Push(new Fill.Solid(solid.Color)); session.CommitLiveEdit(); };
                row.Children.Add(solid);
                row.Children.Add(new TextBlock { Text = Loc.T("Color"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                Children.Add(row);
                break;
            }
            case Fill.Gradient:
                stops = new GradientStopsEditor();
                stops.Changed += (_, s) => { if (!updating && current is Fill.Gradient g) Push(new Fill.Gradient(g.Value with { Stops = s })); };
                stops.Committed += (_, _) => session.CommitLiveEdit();
                Children.Add(stops);
                break;
            case Fill.Pattern:
                patterns = EditorParts.Segmented(Enum.GetValues<PatternKind>(), p => p.Title(), p =>
                {
                    if (updating || current is not Fill.Pattern pattern) return;
                    Push(new Fill.Pattern(pattern.Value with { Kind = p }));
                    session.CommitLiveEdit();
                });
                AutomationProperties.SetName(patterns, Loc.T("Pattern"));
                Children.Add(patterns);
                break;
        }
    }

    private void Push(Fill next)
    {
        current = next;
        session.LiveLayer("Edit Fill", l => l with { Content = new FillContent(next) });
    }

    public void Update(Fill value)
    {
        updating = true;
        current = value;
        if (solid is not null && value is Fill.Solid s) solid.Color = s.Color;
        if (stops is not null && value is Fill.Gradient g) stops.Stops = g.Value.Stops;
        if (patterns is not null && value is Fill.Pattern p) EditorParts.SelectTag(patterns, p.Value.Kind);
        updating = false;
    }
}

public sealed class MaskEditor : StackPanel
{
    private readonly IEditorSession session;
    private readonly CheckBox enabled;
    private readonly ParameterSlider density, feather;
    private bool updating;

    public MaskEditor(IEditorSession session)
    {
        this.session = session;
        enabled = new CheckBox { Style = (Style)Application.Current.FindResource("Glimmer.Compact.CheckBox"), ToolTip = Loc.T("Enable or disable the mask") };
        AutomationProperties.SetName(enabled, Loc.T("Enabled"));
        enabled.Click += (_, _) => session.Perform(CommandID.ToggleMask);
        Children.Add(EditorParts.Header("Layer Mask", "rectangle.dashed", enabled));
        density = new ParameterSlider("Density", 0, 100, 1, v => $"{(int)v} %") { Margin = new Thickness(0, 0, 0, 8) };
        density.Committed += (_, _) => session.CommitLiveEdit();
        Hook(density, v => session.LiveLayer("Mask Density", l => l with { Mask = l.Mask! with { Density = v / 100 } }));
        feather = new ParameterSlider("Feather", 0, 250, 0.5, v => $"{v:0.0} px") { Margin = new Thickness(0, 0, 0, 8) };
        feather.Committed += (_, _) => session.CommitLiveEdit();
        Hook(feather, v => session.LiveLayer("Mask Feather", l => l with { Mask = l.Mask! with { Feather = v } }));
        Children.Add(density);
        Children.Add(feather);
        var buttons = new DockPanel();
        var invert = EditorParts.SmallButton("Invert Default", (_, _) =>
        {
            session.LiveLayer("Invert Mask", l => l.Mask is { } m ? l with { Mask = m with { DefaultValue = 1 - m.DefaultValue } } : l);
            session.CommitLiveEdit();
        }, "Swap Reveal All and Hide All for unpainted areas");
        var delete = EditorParts.SmallButton("Delete Mask", (_, _) => session.Perform(CommandID.DeleteMask));
        DockPanel.SetDock(delete, Dock.Right);
        buttons.Children.Add(delete);
        buttons.Children.Add(invert);
        invert.HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(buttons);
    }

    private void Hook(ParameterSlider slider, Action<double> changed)
    {
        var d = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
        d.AddValueChanged(slider, (_, _) => { if (!updating) changed(slider.Value); });
    }

    public void Update(LayerMask mask)
    {
        updating = true;
        enabled.IsChecked = mask.IsEnabled;
        density.Value = mask.Density * 100;
        feather.Value = mask.Feather;
        updating = false;
    }
}
