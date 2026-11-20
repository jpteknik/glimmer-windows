// Ported from App/RawDevelopSheet.swift (RawDevelopSheet; the model, RawDevelopModel, is the editor library's
// IRawDevelopModel). Layer › Develop…: the RAW file in the active layer developed again with other settings. The
// picture on the left follows the controls; Develop renders at full size and replaces the layer as one undo step.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Sheets;

public sealed class RawDevelopSheet : SheetWindow
{
    private const double PreviewSide = 460;
    private readonly IRawDevelopModel? model;
    private readonly Image picture = new() { Stretch = Stretch.Fill };
    private readonly Spinner first = new() { Width = 20, Height = 20 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock behind = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StatusLine problem = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private readonly StackPanel developing = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button develop;
    private readonly CheckBox picking = new();
    private readonly List<Action> refreshers = [];
    private long shownPicture = -1;

    public RawDevelopSheet(IRawDevelopModel? model) : base("Develop RAW", model is null ? 420 : 860)
    {
        this.model = model;
        if (model is null)
        {
            Body.Children.Add(Headline("Develop RAW"));
            Body.Children.Add(new TextBlock { Text = Loc.T("This layer has no RAW file that this Mac can develop."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var ok = new Button { Content = Loc.T("OK"), IsDefault = true, IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("Glimmer.Compact.AccentButton") };
            ok.Click += (_, _) => Close();
            Body.Children.Add(ok);
            develop = ok;
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewSide) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        Body.Children.Add(grid);

        // The picture, at an exact size so the line under it sits right under it whatever its shape.
        double aspect = (double)model.Info.Width / Math.Max(1, model.Info.Height);
        var frame = new Grid
        {
            Width = aspect >= 1 ? PreviewSide : PreviewSide * aspect, Height = aspect >= 1 ? PreviewSide / aspect : PreviewSide,
            Background = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Left,
        };
        frame.Children.Add(picture);
        frame.Children.Add(first);
        AutomationProperties.SetName(first, Loc.T("Developing the first picture"));
        AutomationProperties.SetName(frame, Loc.T("Preview of the developed picture"));
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (!model.IsPickingNeutral || frame.ActualWidth <= 0 || frame.ActualHeight <= 0) return;
            var p = e.GetPosition(frame);
            model.PickNeutral(p.X / frame.ActualWidth, p.Y / frame.ActualHeight);
        };
        frame.Cursor = Cursors.Arrow;
        var left = new StackPanel { Children = { frame, status, behind } };
        behind.Text = Loc.T("A large file: the picture follows the controls a moment behind. What it shows when it has caught up is what you get.");
        behind.Style = (Style)FindResource("Glimmer.Text.Secondary");
        status.Style = (Style)FindResource("Glimmer.Text.Secondary");
        grid.Children.Add(left);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        right.Children.Add(Headline("Develop RAW"));
        right.Children.Add(new TextBlock
        {
            Text = $"{model.FileName} · {model.Info.Width} × {model.Info.Height} · {Readable.Bytes(model.FileBytes)}", TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 10),
        });
        if (model.PixelsChanged)
            right.Children.Add(new StatusLine { Kind = StatusKind.Warning, TextKey = "This layer has been painted on or changed since it was developed. Developing again replaces those changes.", Margin = new Thickness(0, 0, 0, 10) });
        var info = model.Info;
        Slider(right, "Exposure", -3, 3, () => model.Settings.Exposure, v => model.Settings = model.Settings with { Exposure = v }, Stops);
        Slider(right, "Temperature", 2000, 12000, () => model.Temperature, v => model.Temperature = v, v => Loc.F("{0} K", (int)Math.Round(v)));
        Slider(right, "Tint", -100, 100, () => model.Tint, v => model.Tint = v, v => ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture));
        picking.Content = new TextBlock { Text = Loc.T("Click the picture to set what should be gray"), TextWrapping = TextWrapping.Wrap };
        picking.Style = (Style)FindResource("Glimmer.Compact.CheckBox");
        picking.Margin = new Thickness(0, 0, 0, 10);
        picking.Click += (_, _) => model.IsPickingNeutral = picking.IsChecked == true;
        refreshers.Add(() =>
        {
            picking.IsChecked = model.IsPickingNeutral;
            frame.Cursor = model.IsPickingNeutral ? Cursors.Cross : Cursors.Arrow;
        });
        right.Children.Add(picking);
        Slider(right, "Shadows", 0, 2, () => model.Settings.Shadows, v => model.Settings = model.Settings with { Shadows = v }, v => v.ToString("0.00", CultureInfo.CurrentCulture));
        if (info.SupportsContrast)
            Slider(right, "Contrast", 0, 1, () => model.Settings.Contrast ?? info.Contrast, v => model.Settings = model.Settings with { Contrast = v }, Percent);
        if (info.SupportsSharpness)
            Slider(right, "Sharpness", 0, 1, () => model.Settings.Sharpness ?? info.Sharpness, v => model.Settings = model.Settings with { Sharpness = v }, Percent);
        if (info.SupportsLuminanceNoise)
            Slider(right, "Noise reduction", 0, 1, () => model.Settings.LuminanceNoise ?? info.LuminanceNoise, v => model.Settings = model.Settings with { LuminanceNoise = v }, Percent);
        if (info.SupportsColorNoise)
            Slider(right, "Color noise reduction", 0, 1, () => model.Settings.ColorNoise ?? info.ColorNoise, v => model.Settings = model.Settings with { ColorNoise = v }, Percent);
        if (info.SupportsHighlightRecovery)
            Check(right, "Recover highlights", () => model.Settings.HighlightRecovery, v => model.Settings = model.Settings with { HighlightRecovery = v });
        if (info.SupportsLensCorrection)
        {
            Check(right, "Lens correction", () => model.Settings.LensCorrection ?? info.LensCorrection, v => model.Settings = model.Settings with { LensCorrection = v });
            right.Children.Add(new TextBlock { Text = Loc.T("Uses the correction the camera stored in the file. It reshapes the whole picture a little, not only the corners."),
                                               TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 10) });
        }
        right.Children.Add(problem);
        developing.Children.Add(new Spinner { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) });
        developing.Children.Add(new TextBlock { Text = Loc.T("Developing the picture at full size… Cancel leaves the layer as it was."), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        right.Children.Add(developing);

        var asShot = new Button { Content = Loc.T("As Shot"), Style = (Style)FindResource("Glimmer.Compact.Button") };
        asShot.Click += (_, _) => model.ResetToAsShot();
        var buttons = Buttons("Develop", Develop, null, asShot);
        develop = buttons.Children.OfType<Button>().First(b => b.IsDefault);
        // Cancel at any moment: a development that is running is abandoned and the document stays as it was.
        foreach (var cancel in buttons.Children.OfType<Button>().Where(b => b.IsCancel)) cancel.Click += (_, _) => model.Cancel();
        right.Children.Add(buttons);

        model.PropertyChanged += OnModelChanged;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelChanged;
            model.Cancel();
        };
        Refresh();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (model is null) return;
        foreach (var r in refreshers) r();
        if (model.Picture is { } p && p.Id != shownPicture)
        {
            shownPicture = p.Id;
            picture.Source = Panels.Thumbnails.Bitmap(p);
        }
        first.Visibility = model.Picture is null ? Visibility.Visible : Visibility.Collapsed;
        status.Text = Loc.T(model.Picture is null ? "Developing the first picture…"
                            : model.IsBehind ? "The picture is catching up with the controls…" : "The picture shows the settings as they are.");
        behind.Visibility = model.FollowsBehind ? Visibility.Visible : Visibility.Collapsed;
        problem.Verbatim = model.Problem;
        problem.Visibility = model.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        developing.Visibility = model.IsDeveloping ? Visibility.Visible : Visibility.Collapsed;
        develop.IsEnabled = !model.IsDeveloping;
    }

    private async void Develop()
    {
        if (model is null) return;
        if (await model.DevelopAsync()) Close();
    }

    private static string Stops(double v)
    {
        var number = v.ToString("0.00", CultureInfo.CurrentCulture);
        if (v > 0) number = "+" + number;
        return Loc.F("{0} stops", number);
    }

    private static string Percent(double v) => v.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>The name and the value on one line, the slider under them (the value is the slider's spoken value).</summary>
    private void Slider(Panel into, string title, double min, double max, Func<double> get, Action<double> set, Func<double, string> shown)
    {
        var value = new TextBlock { Foreground = (Brush)FindResource("Glimmer.MutedBrush"), FontFamily = (FontFamily)FindResource("Glimmer.MonoFont") };
        var head = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        head.Children.Add(new TextBlock { Text = Loc.T(title) });
        var slider = new Slider { Minimum = min, Maximum = max, IsMoveToPointEnabled = true, SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };
        AutomationProperties.SetName(slider, Loc.T(title));
        bool updating = false;
        slider.ValueChanged += (_, e) =>
        {
            value.Text = shown(e.NewValue);
            AutomationProperties.SetItemStatus(slider, value.Text);
            if (!updating) set(e.NewValue);
        };
        refreshers.Add(() =>
        {
            if (slider.IsMouseCaptureWithin) return;    // never move a slider under the person's hand
            updating = true;
            slider.Value = get();
            value.Text = shown(slider.Value);
            updating = false;
        });
        into.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { head, slider } });
    }

    private void Check(Panel into, string title, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Content = Loc.T(title), Style = (Style)FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 0, 0, 10) };
        box.Click += (_, _) => set(box.IsChecked == true);
        refreshers.Add(() => box.IsChecked = get());
        into.Children.Add(box);
    }
}
