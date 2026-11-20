// Ported from App/LayerStyleSheet.swift. Edits the active layer's styles with live preview. Cancel restores the layer;
// OK keeps the changes as one undo step.
using System.Windows;
using System.Windows.Controls;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Sheets;

public sealed class LayerStyleSheet : SheetWindow
{
    private readonly IEditorSession session;
    private readonly DocumentState original;
    private readonly StackPanel form = new();
    private bool keep;

    public LayerStyleSheet(IEditorSession session) : base("Layer Style", 440)
    {
        this.session = session;
        session.CommitLiveEdit();
        original = session.State;
        Body.Children.Add(form);
        Body.Children.Add(Buttons("OK", () => { keep = true; session.CommitLiveEdit(); Close(); }));
        Closed += (_, _) => { if (!keep) session.RevertLiveEdit(original); };
        Build();
    }

    private LayerEffects Effects => session.ActiveLayer?.Effects ?? new LayerEffects();

    private void Change(Func<LayerEffects, LayerEffects> edit) => session.LiveLayer("Layer Style", l => l with { Effects = edit(l.Effects) });

    private void Build()
    {
        form.Children.Clear();
        ShadowSection("Drop Shadow", e => e.DropShadow, (e, v) => e with { DropShadow = v }, new ShadowEffect());
        ShadowSection("Inner Shadow", e => e.InnerShadow, (e, v) => e with { InnerShadow = v }, new ShadowEffect { Distance = 5, Size = 5 });
        var glow = Effects.OuterGlow;
        Section("Outer Glow", glow?.IsEnabled == true, on => Change(e => e with { OuterGlow = (e.OuterGlow ?? new GlowEffect()) with { IsEnabled = on } }), glow is { IsEnabled: true } g ? [
            Well(g.Color, c => Change(e => e with { OuterGlow = e.OuterGlow! with { Color = c } })),
            Mode(g.BlendMode, m => Change(e => e with { OuterGlow = e.OuterGlow! with { BlendMode = m } })),
            Slider("Opacity", g.Opacity, 0, 1, 0.01, v => Change(e => e with { OuterGlow = e.OuterGlow! with { Opacity = v } })),
            Slider("Spread", g.Spread, 0, 1, 0.01, v => Change(e => e with { OuterGlow = e.OuterGlow! with { Spread = v } })),
            Slider("Size", g.Size, 0, 250, 1, v => Change(e => e with { OuterGlow = e.OuterGlow! with { Size = v } })),
        ] : []);
        var stroke = Effects.Stroke;
        Section("Stroke", stroke?.IsEnabled == true, on => Change(e => e with { Stroke = (e.Stroke ?? new StrokeEffect()) with { IsEnabled = on } }), stroke is { IsEnabled: true } s ? [
            Well(s.Color, c => Change(e => e with { Stroke = e.Stroke! with { Color = c } })),
            Row("Position", Picker(Enum.GetValues<StrokePosition>(), p => p.ToString(), s.Position, p => Change(e => e with { Stroke = e.Stroke! with { Position = p } }))),
            Slider("Size", s.Width, 1, 100, 1, v => Change(e => e with { Stroke = e.Stroke! with { Width = v } })),
            Slider("Opacity", s.Opacity, 0, 1, 0.01, v => Change(e => e with { Stroke = e.Stroke! with { Opacity = v } })),
        ] : []);
        var overlay = Effects.ColorOverlay;
        Section("Color Overlay", overlay?.IsEnabled == true, on => Change(e => e with { ColorOverlay = (e.ColorOverlay ?? new ColorOverlayEffect()) with { IsEnabled = on } }), overlay is { IsEnabled: true } o ? [
            Well(o.Color, c => Change(e => e with { ColorOverlay = e.ColorOverlay! with { Color = c } })),
            Mode(o.BlendMode, m => Change(e => e with { ColorOverlay = e.ColorOverlay! with { BlendMode = m } })),
            Slider("Opacity", o.Opacity, 0, 1, 0.01, v => Change(e => e with { ColorOverlay = e.ColorOverlay! with { Opacity = v } })),
        ] : []);
    }

    private void ShadowSection(string title, Func<LayerEffects, ShadowEffect?> get, Func<LayerEffects, ShadowEffect?, LayerEffects> set, ShadowEffect make)
    {
        var shadow = get(Effects);
        Section(title, shadow?.IsEnabled == true, on => Change(e => set(e, (get(e) ?? make) with { IsEnabled = on })), shadow is { IsEnabled: true } s ? [
            Well(s.Color, c => Change(e => set(e, get(e)! with { Color = c }))),
            Mode(s.BlendMode, m => Change(e => set(e, get(e)! with { BlendMode = m }))),
            Slider("Opacity", s.Opacity, 0, 1, 0.01, v => Change(e => set(e, get(e)! with { Opacity = v }))),
            Slider("Angle", s.Angle, -180, 180, 1, v => Change(e => set(e, get(e)! with { Angle = v })), "°"),
            Slider("Distance", s.Distance, 0, 250, 1, v => Change(e => set(e, get(e)! with { Distance = v }))),
            Slider("Spread", s.Spread, 0, 1, 0.01, v => Change(e => set(e, get(e)! with { Spread = v }))),
            Slider("Size", s.Size, 0, 250, 1, v => Change(e => set(e, get(e)! with { Size = v }))),
        ] : []);
    }

    /// <summary>A section: its toggle as the heading, then its settings while it is on.</summary>
    private void Section(string title, bool on, Action<bool> toggle, UIElement[] rows)
    {
        var box = new CheckBox { Content = new TextBlock { Text = Loc.T(title), Style = (Style)FindResource("Glimmer.Text.Headline") }, IsChecked = on,
                                 Style = (Style)FindResource("Glimmer.Compact.CheckBox") };
        System.Windows.Automation.AutomationProperties.SetName(box, Loc.T(title));
        box.Click += (_, _) => { toggle(box.IsChecked == true); Build(); };
        form.Children.Add(Section([box, .. rows]));
    }

    private DockPanel Well(SRGBColor color, Action<SRGBColor> set)
    {
        var well = new ColorWell { Label = "Color", Color = color };
        well.Committed += (_, _) => set(well.Color);
        return Row("Color", well);
    }

    private DockPanel Mode(BlendMode mode, Action<BlendMode> set) => Row("Blend Mode", Picker(BlendMode.PixelModes, m => m.Title(), mode, set));

    private ParameterSlider Slider(string label, double value, double min, double max, double step, Action<double> set, string suffix = " px")
    {
        var slider = new ParameterSlider(label, min, max, step, v => step < 1 ? $"{v * 100:0}%" : $"{(int)v}{suffix}") { Value = value };
        var d = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
        d.AddValueChanged(slider, (_, _) => set(slider.Value));
        return slider;
    }
}
