// Ported from App/ToolOptionsBar.swift (FilterSheet). Settings for one filter with live preview on the canvas;
// floating and non-modal (DocumentView places it), so the image stays in view.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Sheets;

public sealed class FilterSheet : UserControl
{
    private readonly IEditorSession session;
    private FilterSettings settings;
    private readonly StackPanel fields = new();
    private readonly CheckBox preview;
    private readonly Spinner busy = new() { Size = 12, Visibility = Visibility.Collapsed };

    public FilterSheet(IEditorSession session, FilterSettings initial)
    {
        this.session = session;
        settings = initial;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(busy, Dock.Right);
        header.Children.Add(busy);
        header.Children.Add(new TextBlock { Text = Loc.T(initial.Kind.Title()), Style = (Style)FindResource("Glimmer.Text.Headline") });
        preview = new CheckBox { Content = Loc.T("Preview"), IsChecked = true, Style = (Style)FindResource("Glimmer.Compact.CheckBox") };
        preview.Click += (_, _) => { if (preview.IsChecked == true) session.PreviewFilter(settings); else session.StopFilterPreview(); };
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        var reset = new Button { Content = Loc.T("Reset"), Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => { settings = new FilterSettings(settings.Kind); Build(); Changed(); };
        var ok = new Button { Content = Loc.T("OK"), Style = (Style)FindResource("Glimmer.Compact.AccentButton"), IsDefault = true };
        ok.Click += (_, _) => session.ApplyFilter(settings);
        var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)FindResource("Glimmer.Compact.Button"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => session.CancelFilterDialog();
        DockPanel.SetDock(preview, Dock.Left);
        buttons.Children.Add(preview);
        foreach (var b in new[] { cancel, ok, reset }) { DockPanel.SetDock(b, Dock.Right); buttons.Children.Add(b); }
        var body = new StackPanel { Margin = new Thickness(20), Width = 380 };
        body.Children.Add(header);
        body.Children.Add(fields);
        if (session.State.Selection is not null)
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0),
                Children = { new Icon { Symbol = "selection.pin.in.out", Size = 11, Margin = new Thickness(0, 0, 5, 0) },
                             new TextBlock { Text = Loc.T("Applies to the selection"), Style = (Style)FindResource("Glimmer.Text.Caption") } },
            });
        body.Children.Add(buttons);
        Content = new PanelSurface { Content = body, CornerRadius = new CornerRadius(14) };
        System.Windows.Automation.AutomationProperties.SetName(this, Loc.T(initial.Kind.Title()));
        Build();
        Loaded += (_, _) => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    private void Build()
    {
        fields.Children.Clear();
        foreach (var field in settings.Kind.Fields())
        {
            if (field.IsToggle)
            {
                var box = new CheckBox { Content = Loc.T(field.Label), IsChecked = settings.Flag(field.Key), Style = (Style)FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 0, 0, 8) };
                var key = field.Key;
                box.Click += (_, _) => { settings = settings with { Values = settings.Values.SetItem(key, box.IsChecked == true ? 1 : 0) }; Changed(); };
                fields.Children.Add(box);
                continue;
            }
            var f = field;
            var slider = new ParameterSlider(f.Label, f.Range.LowerBound, f.Range.UpperBound, (f.Range.UpperBound - f.Range.LowerBound) / 1000, v => Format(v, f)) { Value = settings.Value(f.Key), Margin = new Thickness(0, 0, 0, 8) };
            var d = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
            d.AddValueChanged(slider, (_, _) => { settings = settings with { Values = settings.Values.SetItem(f.Key, slider.Value) }; Changed(); });
            fields.Children.Add(slider);
        }
    }

    private void Changed()
    {
        if (preview.IsChecked == true) session.PreviewFilter(settings);
        busy.Visibility = session.IsPreviewingFilter && preview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Format(double v, FilterField field)
    {
        var text = v.ToString(field.Range.UpperBound <= 5 ? "0.00" : "0", CultureInfo.CurrentCulture);
        return field.Unit.Length == 0 ? text : $"{text} {field.Unit}";
    }
}
