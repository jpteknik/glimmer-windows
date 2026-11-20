// Ported from App/ToolOptionsBar.swift (ToolOptionsBar; TextOptions, BrushPresetsMenu, BrushDynamicsButton and
// GradientToolOptions in reduced form). Options for the current tool, floating above the canvas. The row is rebuilt
// when the tool (or what the row shows) changes, and only updated otherwise, so a slider being dragged stays put.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public sealed partial class ToolOptionsBar : UserControl
{
    private IEditorSession? session;
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly PanelSurface surface;
    private readonly List<Action> refreshers = [];
    private string? shape;
    private bool updating;

    public ToolOptionsBar()
    {
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = row, Padding = new Thickness(14, 7, 14, 7),
        };
        surface = new PanelSurface { Content = scroller, CornerRadius = new CornerRadius(10) };
        Content = surface;
        TextElement_SetFontSize(this, 12.5);
        AutomationProperties.SetName(this, Loc.T("Tool options"));
        DataContextChanged += (_, e) =>
        {
            if (session is not null) { session.PropertyChanged -= OnChanged; session.AI.PropertyChanged -= OnChanged; }
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) { session.PropertyChanged += OnChanged; session.AI.PropertyChanged += OnChanged; }
            Rebuild();
        };
    }

    private static void TextElement_SetFontSize(DependencyObject d, double size) => d.SetValue(TextBlock.FontSizeProperty, size);

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shape() != shape) Rebuild();
        else Refresh();
    }

    /// <summary>What decides the row's parts (not their values).</summary>
    private string Shape()
    {
        if (session is not { } s) return "";
        return string.Join("|", s.Tool, s.Liquify is not null, s.TransformSession?.IsQuad, s.TransformSession?.Mode, s.CropSession is not null,
                           s.AI.MagicPoints.Count == 0, s.ActiveLayer?.Mask is not null, s.ShapeKind, s.ShapeFillColor is null, s.ShapeStrokeWidth >= 1,
                           s.CloneSource is null, s.ActiveLayer?.Id, s.GradientStops is null, s.State.Selection is null,
                           TextShape(s), BrushPresetStore.Shared.Version);
    }

    private void Refresh()
    {
        updating = true;
        foreach (var r in refreshers) r();
        updating = false;
    }

    private void Rebuild()
    {
        row.Children.Clear();
        refreshers.Clear();
        shape = Shape();
        if (session is not { } s) { Visibility = Visibility.Collapsed; return; }
        if (s.Liquify is not null) LiquifyOptions(s);
        else if (s.Tool is not (ToolID.Move or ToolID.Hand))
        {
            // The tool's name in small capitals, as in the web app.
            Add(new TextBlock { Text = Loc.T(s.Tool.Title()).ToUpper(CultureInfo.CurrentCulture), Style = (Style)FindResource("Glimmer.Text.SmallCaps") });
            ToolOptions(s);
        }
        surface.Visibility = row.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void ToolOptions(IEditorSession s)
    {
        switch (s.Tool)
        {
            case ToolID.Brush or ToolID.Eraser:
                Add(BrushPresetsMenu(s));
                BrushSize(s);
                Add(Slider("Hardness", 0, 1, () => s.Brush.Hardness, v => s.Brush = s.Brush with { Hardness = v }, Percent));
                Add(Slider("Opacity", 0, 1, () => s.Brush.Opacity, v => s.Brush = s.Brush with { Opacity = v }, Percent));
                Add(Slider("Flow", 0.01, 1, () => s.Brush.Flow, v => s.Brush = s.Brush with { Flow = v }, Percent));
                Add(DynamicsButton(s));
                if (s.ActiveLayer?.Mask is not null) Add(Check("Paint mask", () => s.EditingMask, v => s.EditingMask = v));
                break;
            case ToolID.MagicWand:
                WandOptions(s);
                Feather(s);
                break;
            case ToolID.PaintBucket:
                WandOptions(s);
                Add(Slider("Opacity", 0, 1, () => s.Brush.Opacity, v => s.Brush = s.Brush with { Opacity = v }, Percent));
                Add(Menu("Fill", [
                    (s.State.Selection is null ? "Whole Layer with Foreground" : "Selection with Foreground", () => s.Perform(CommandID.FillForeground)),
                    (s.State.Selection is null ? "Whole Layer with Pattern" : "Selection with Pattern", () => s.Perform(CommandID.FillPattern)),
                ], "Fill without tolerance (⌥⌫ fills with the foreground color)"));
                break;
            case ToolID.Gradient:
                Add(Segmented(Enum.GetValues<GradientStyle>(), g => g.Title(), () => s.GradientStyle, g => s.GradientStyle = g, "Style"));
                GradientOptions(s);
                break;
            case ToolID.PolygonLasso:
                Feather(s);
                Hint("Click to add points · double-click or Return to close · Esc cancels");
                break;
            case ToolID.Lasso:
                Feather(s);
                Hint("⇧ add · ⌥ subtract · ⇧⌥ intersect");
                break;
            case ToolID.RectangleMarquee or ToolID.EllipseMarquee:
                Feather(s);
                Hint("⇧ add · ⌥ subtract · while dragging: ⇧ square, ⌥ from the center");
                break;
            case ToolID.MagicSelect:
                if (s.AI.MagicPoints.Count == 0) Hint("Click an object · ⌥-click to exclude");
                else
                {
                    var count = new TextBlock { Foreground = (Brush)FindResource("Glimmer.MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
                    refreshers.Add(() => count.Text = Loc.F("{0} points", s.AI.MagicPoints.Count));
                    Add(count);
                    Hint("Click to add · ⌥-click to exclude");
                    Add(Button("Clear Points", s.AI.CancelMagicSelect, help: "Forget the points and put back the earlier selection (Esc)"));
                    Add(Button("Apply", s.AI.ResetMagicSelect, accent: true));
                }
                break;
            case ToolID.Eyedropper:
                Hint("Click to pick the foreground · ⌥-click for the background");
                break;
            case ToolID.FreeTransform:
                TransformOptions(s);
                break;
            case ToolID.Crop:
                CropOptions(s);
                break;
            case ToolID.Type:
                TextOptions(s);
                break;
            case ToolID.Pen:
                Hint("Click for corners · drag for curves · ⌥ breaks handles · click the first point to close · Return ends");
                break;
            case ToolID.Shape:
                Add(Combo(Enum.GetValues<ShapeKind>(), k => k switch
                {
                    ShapeKind.RoundedRectangle => "Rounded Rectangle", ShapeKind.Ellipse => "Ellipse", ShapeKind.Line => "Line", _ => "Rectangle",
                }, () => s.ShapeKind, k => s.ShapeKind = k, "Shape"));
                if (s.ShapeKind == ShapeKind.RoundedRectangle)
                    Add(Slider("Radius", 1, 400, () => s.ShapeCornerRadius, v => s.ShapeCornerRadius = v, Pixels));
                if (s.ShapeKind == ShapeKind.Line)
                    Add(Slider("Width", 1, 100, () => s.ShapeLineWidth, v => s.ShapeLineWidth = v, Pixels));
                Add(Well("Fill color", () => s.ShapeFillColor ?? s.Foreground, c => s.ShapeFillColor = c, "Fill color of new shapes"));
                if (s.ShapeFillColor is not null) Add(Button("Use Foreground", () => s.ShapeFillColor = null, help: "Fill new shapes with the foreground color again"));
                Add(Slider("Stroke", 0, 100, () => s.ShapeStrokeWidth, v => s.ShapeStrokeWidth = v, v => v < 1 ? Loc.T("Off") : Pixels(v)));
                if (s.ShapeStrokeWidth >= 1) Add(Well("Stroke color", () => s.ShapeStrokeColor, c => s.ShapeStrokeColor = c, null));
                Hint("⇧ constrains · U next shape");
                break;
            case ToolID.CloneStamp or ToolID.HealingBrush:
                RetouchBrush(s);
                Add(Check("Aligned", () => s.CloneAligned, v => s.CloneAligned = v));
                Hint(s.CloneSource is null ? "⌥-click to set the source" : "⌥-click sets a new source");
                break;
            case ToolID.Dodge or ToolID.Burn:
                RetouchBrush(s);
                Add(Combo([0, 1, 2], r => r switch { 0 => "Shadows", 1 => "Midtones", _ => "Highlights" }, () => s.DodgeRange, r => s.DodgeRange = r, "Range"));
                Add(Slider("Exposure", 0.01, 1, () => s.DodgeExposure, v => s.DodgeExposure = v, Percent));
                break;
            case ToolID.PatternStamp:
                RetouchBrush(s);
                Add(Slider("Opacity", 0, 1, () => s.Brush.Opacity, v => s.Brush = s.Brush with { Opacity = v }, Percent));
                Add(Combo(Enum.GetValues<PatternKind>(), p => p.Title(), () => s.PatternKind, p => s.PatternKind = p, "Pattern"));
                Add(Slider("Scale", 4, 256, () => s.PatternScale, v => s.PatternScale = v, Pixels));
                Hint("Uses the foreground and background colors");
                break;
            case ToolID.Blur or ToolID.Sharpen or ToolID.Smudge:
                RetouchBrush(s);
                Add(Slider("Strength", 0.01, 1, () => s.RetouchStrength, v => s.RetouchStrength = v, Percent));
                break;
        }
    }

    private void LiquifyOptions(IEditorSession s)
    {
        Add(new TextBlock { Text = Loc.T("Liquify"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Add(Combo(Enum.GetValues<LiquifyEngine.Mode>(), m => m.Title(), () => s.LiquifyMode, m => s.LiquifyMode = m, "Mode"));
        Add(Slider("Size", 3, 11, () => Math.Log2(s.LiquifySize), v => s.LiquifySize = Math.Round(Math.Pow(2, v)), _ => Pixels(s.LiquifySize)));
        Add(Slider("Pressure", 0.01, 1, () => s.LiquifyPressure, v => s.LiquifyPressure = v, Percent));
        Add(Button("Restore All", s.RestoreAllLiquify, help: "Undo every liquify change on this layer"));
        Add(Button("Cancel", s.CancelLiquify));
        Add(Button("Apply", s.CommitLiquify, accent: true));
    }

    private void TransformOptions(IEditorSession s)
    {
        if (s.TransformSession is not { } t) { Hint("Select a layer to transform"); return; }
        Add(Combo(Enum.GetValues<TransformMode>(), m => m switch
        {
            TransformMode.Distort => "Distort", TransformMode.Skew => "Skew", TransformMode.Perspective => "Perspective", _ => "Scale & Rotate",
        }, () => s.TransformSession?.Mode ?? TransformMode.ScaleRotate, s.SetTransformMode, "Mode"));
        if (!t.IsQuad)
        {
            Add(Number("W", "Width (percent)", () => (s.TransformSession?.ScaleX ?? 1) * 100, "%", s.SetTransformScaleXPercent));
            Add(Number("H", "Height (percent)", () => (s.TransformSession?.ScaleY ?? 1) * 100, "%", s.SetTransformScaleYPercent));
            Add(Number("∠", "Angle (degrees)", () => (s.TransformSession?.Rotation ?? 0) * 180 / Math.PI, "°", s.SetTransformDegrees));
            Hint("corners keep proportions, ⇧ frees them · drag just outside a corner to rotate · click away to apply · ⌘-drag a corner to distort");
        }
        else
        {
            Hint(t.Mode == TransformMode.Skew ? "Drag an edge to slant it" : t.Mode == TransformMode.Perspective ? "Drag a corner; its neighbor mirrors it" : "Drag corners freely");
        }
        Add(Button("Cancel", () => { s.CancelTransform(); s.Tool = ToolID.Move; }));
        Add(Button("Apply", () => { s.CommitTransform(); s.Tool = ToolID.Move; }, accent: true));
    }

    private void CropOptions(IEditorSession s)
    {
        double original = (double)s.State.Width / Math.Max(s.State.Height, 1);
        (string Title, double? Aspect)[] aspects = [("Free", null), ("Original", original), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("4:5", 0.8), ("9:16", 9.0 / 16)];
        Add(Combo(Enumerable.Range(0, aspects.Length).ToArray(), i => aspects[i].Title,
                  () => Array.FindIndex(aspects, a => a.Aspect == s.CropSession?.Aspect) is var i && i >= 0 ? i : 0, i => s.SetCropAspect(aspects[i].Aspect), "Aspect"));
        Add(Button("Suggest", s.AI.SuggestCrop, symbol: "sparkles", help: "Frame the subject at this aspect ratio (on-device)"));
        Add(Button("Level", s.AI.AutoStraighten, symbol: "level", help: "Straighten a tilted horizon (on-device)"));
        Add(Slider("Straighten", -45, 45, () => (s.CropSession?.Angle ?? 0) * 180 / Math.PI, s.SetCropAngle, v => v.ToString("0.0", CultureInfo.CurrentCulture) + "°"));
        var size = new TextBlock { Foreground = (Brush)FindResource("Glimmer.MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        refreshers.Add(() => size.Text = s.CropSession is { } c ? Loc.F("{0} × {1} px", (int)Math.Round(c.Rect.Width), (int)Math.Round(c.Rect.Height)) : "");
        Add(size);
        var delete = Check("Delete cropped pixels", () => s.CropDeletesPixels, v => s.CropDeletesPixels = v);
        delete.ToolTip = Loc.T("Off keeps the pixels outside the crop, so you can move them back in later");
        Add(delete);
        Add(Button("Cancel", () => { s.CancelCrop(); s.Tool = ToolID.Move; }));
        Add(Button("Crop", () => { s.ApplyCrop(); s.Tool = ToolID.Move; }, accent: true));
    }

    // ------------------------------------------------------------------------------------------------- shared rows

    private void BrushSize(IEditorSession s) =>
        Add(Slider("Size", 0, 11, () => Math.Log2(Math.Max(s.Brush.Size, 1)), v => s.Brush = s.Brush with { Size = Math.Round(Math.Pow(2, v)) }, _ => Pixels(s.Brush.Size)));

    private void RetouchBrush(IEditorSession s)
    {
        BrushSize(s);
        Add(Slider("Hardness", 0, 1, () => s.Brush.Hardness, v => s.Brush = s.Brush with { Hardness = v }, Percent));
        if (s.Tool is ToolID.CloneStamp or ToolID.HealingBrush)
            Add(Slider("Opacity", 0, 1, () => s.Brush.Opacity, v => s.Brush = s.Brush with { Opacity = v }, Percent));
    }

    /// <summary>Softens selections as they are made.</summary>
    private void Feather(IEditorSession s) => Add(Slider("Feather", 0, 32, () => s.SelectionFeather, v => s.SelectionFeather = v, Pixels));

    private void WandOptions(IEditorSession s)
    {
        Add(Slider("Tolerance", 0, 255, () => s.WandTolerance, v => s.WandTolerance = v, v => ((int)v).ToString(CultureInfo.CurrentCulture)));
        Add(Check("Contiguous", () => s.WandContiguous, v => s.WandContiguous = v));
        Add(Check("Sample all layers", () => s.SampleAllLayers, v => s.SampleAllLayers = v));
    }

    private static string Percent(double v) => $"{(int)Math.Round(v * 100)} %";
    private static string Pixels(double v) => $"{(int)v} px";

    private void Add(UIElement element)
    {
        if (element is FrameworkElement fe) fe.Margin = new Thickness(row.Children.Count == 0 ? 0 : 14, 0, 0, 0);
        row.Children.Add(element);
    }

    private void Hint(string key) =>
        Add(new TextBlock { Text = Loc.T(key), Foreground = (Brush)FindResource("Glimmer.MutedBrush"), VerticalAlignment = VerticalAlignment.Center });

    private ParameterSlider Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var slider = new ParameterSlider(label, min, max, (max - min) / 1000, v => format(v), inline: true);
        var descriptor = DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
        descriptor.AddValueChanged(slider, (_, _) => { if (!updating) set(slider.Value); });
        refreshers.Add(() => slider.Value = get());
        return slider;
    }

    private CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Content = Loc.T(label), Style = (Style)FindResource("Glimmer.Compact.CheckBox") };
        box.Click += (_, _) => set(box.IsChecked == true);
        refreshers.Add(() => box.IsChecked = get());
        return box;
    }

    private Button Button(string title, Action click, bool accent = false, string? symbol = null, string? help = null)
    {
        object content = Loc.T(title);
        if (symbol is not null)
            content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = symbol, Size = 12, Margin = new Thickness(0, 0, 5, 0) }, new TextBlock { Text = Loc.T(title) } } };
        var b = new Button { Content = content, Style = (Style)FindResource(accent ? "Glimmer.Compact.AccentButton" : "Glimmer.Compact.Button"), ToolTip = help is null ? null : Loc.T(help) };
        AutomationProperties.SetName(b, Loc.T(title));
        b.Click += (_, _) => click();
        return b;
    }

    private ComboBox Combo<T>(IReadOnlyList<T> values, Func<T, string> title, Func<T> get, Action<T> set, string name)
    {
        var box = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), MinWidth = 110 };
        AutomationProperties.SetName(box, Loc.T(name));
        foreach (var v in values) box.Items.Add(new ComboBoxItem { Content = Loc.T(title(v)), Tag = v });
        box.SelectionChanged += (_, _) => { if (!updating && box.SelectedItem is ComboBoxItem { Tag: T v }) set(v); };
        refreshers.Add(() => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, get())));
        return box;
    }

    private ListBox Segmented<T>(IReadOnlyList<T> values, Func<T, string> title, Func<T> get, Action<T> set, string name) where T : notnull
    {
        var list = new ListBox { Style = (Style)FindResource("Glimmer.Segmented"), Width = 140 };
        AutomationProperties.SetName(list, Loc.T(name));
        foreach (var v in values) list.Items.Add(new ListBoxItem { Content = Loc.T(title(v)), Tag = v });
        list.SelectionChanged += (_, _) => { if (!updating && list.SelectedItem is ListBoxItem { Tag: T v }) set(v); };
        refreshers.Add(() => list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, get())));
        return list;
    }

    /// <summary>A borderless menu button (SwiftUI Menu with .borderlessButton): the title in the accent colour.</summary>
    private Button Menu(string title, (string Title, Action Run)[] items, string? help = null)
    {
        var b = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Foreground = (Brush)FindResource("Glimmer.AccentBrush"), ToolTip = help is null ? null : Loc.T(help),
                             Content = new TextBlock { Text = Loc.T(title) + " ▾" } };
        AutomationProperties.SetName(b, Loc.T(title));
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var (t, run) in items)
            {
                var item = new MenuItem { Header = Loc.T(t) };
                item.Click += (_, _) => run();
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    private ColorWell Well(string label, Func<SRGBColor> get, Action<SRGBColor> set, string? help)
    {
        var well = new ColorWell { Label = label, ToolTip = help is null ? null : Loc.T(help) };
        well.Committed += (_, _) => set(well.Color);
        refreshers.Add(() => well.Color = get());
        return well;
    }

    /// <summary>A number field with a one-letter label; `name` is what a screen reader says (the label is a letter).</summary>
    private StackPanel Number(string label, string name, Func<double> get, string suffix, Action<double> set)
    {
        var box = new TextBox { Width = 56, Style = (Style)FindResource("Glimmer.Compact.TextBox"), HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(box, Loc.T(name));
        void Apply()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) set(v);
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(); };
        refreshers.Add(() => { if (!box.IsKeyboardFocused) box.Text = (Math.Round(get() * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture); });
        var muted = (Brush)FindResource("Glimmer.MutedBrush");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = label, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) },
                box,
                new TextBlock { Text = suffix, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) },
            },
        };
    }
}
