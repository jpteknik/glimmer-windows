// Ported from App/AIPanel.swift (RelightView, CleanupView, StrengthAction, UpscaleView, PresetsView, LensBlurView), in
// the Mac's order between Match Color and On This Mac. Each section is a disclosure group with the section's tag, so a
// menu command can open it (Reveal).
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glimmer.AI;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Panels;

public partial class AIPanel
{
    private readonly List<Action> sectionRefreshers = [];

    private void RefreshSections()
    {
        foreach (var r in sectionRefreshers) r();
    }

    private void BuildSections()
    {
        var panel = (Panel)Scroller.Content;
        int at = panel.Children.IndexOf(panel.Children.OfType<Expander>().First(x => x.Tag as string == "OnDevice"));
        void Add(string tag, string title, string symbol, UIElement body)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new Icon { Symbol = symbol, Size = 14, Margin = new Thickness(0, 0, 6, 0) });
            header.Children.Add(new TextBlock { Text = Loc.T(title), Style = (Style)FindResource("Glimmer.Text.Headline") });
            var expander = new Expander { Tag = tag, Header = header, Content = body };
            AutomationProperties.SetName(expander, Loc.T(title));
            panel.Children.Insert(at++, expander);
        }
        Add("Relight", "Relight", "lightbulb.max", Relight());
        Add("Cleanup", "Clean Up", "eraser.line.dashed", Cleanup());
        Add("Reflections", "Remove Reflections", "sun.max.trianglebadge.exclamationmark",
            Strength("Removes glare from glass, water and screens — in the selection, or the whole image.", "Remove Reflections", 0.7, (a, s) => a.RemoveReflections(s)));
        Add("Harmonize", "Harmonize", "wand.and.rays",
            Strength("Relights and grades the active layer so it sits naturally in the scene below.", "Harmonize Layer", 0.6, (a, s) => a.Harmonize(s)));
        Add("Upscale", "Upscale", "arrow.up.backward.and.arrow.down.forward", Upscale());
        Add("Presets", "Presets", "camera.filters", Presets());
        Add("LensBlur", "Lens Blur", "camera.aperture", LensBlur());
    }

    private Style Compact => (Style)FindResource("Glimmer.Compact.Button");

    private TextBlock Note(string key) =>
        new() { Text = Loc.T(key), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(0, 0, 0, 8) };

    private static ParameterSlider Percent(string label, double initial, double max = 100) =>
        new(label, 0, max, 1, v => $"{(int)v} %") { Value = initial * 100, Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>A note, a strength, the price line and the action (StrengthAction).</summary>
    private StackPanel Strength(string note, string button, double initial, Action<IAIController, double> run)
    {
        var strength = Percent("Strength", initial);
        var go = PriceGate.Gate(new Button { Content = Loc.T(button), Style = Compact, HorizontalAlignment = HorizontalAlignment.Left });
        go.Click += (_, _) => { if (AI is { } ai) run(ai, strength.Value / 100); };
        return new StackPanel { Children = { Note(note), strength, new UnknownPriceNote(), go } };
    }

    private StackPanel Relight()
    {
        var direction = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), MinWidth = 120 };
        foreach (var d in LightDirections.AllCases) direction.Items.Add(new ComboBoxItem { Content = Loc.T(d.Title()), Tag = d });
        direction.SelectedIndex = 0;
        AutomationProperties.SetName(direction, Loc.T("Light from"));
        var color = new ColorWell { Label = "Light color", Color = new SRGBColor(1, 0.9, 0.75) };
        var intensity = Percent("Intensity", 0.6);
        var environment = new TextBox { Style = (Style)FindResource("Glimmer.Compact.TextBox"), Tag = Loc.T("Environment (optional), e.g. a neon-lit street"), Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(environment, Loc.T("Environment (optional), e.g. a neon-lit street"));
        var go = PriceGate.Gate(new Button { Content = Loc.T("Relight"), Style = Compact, HorizontalAlignment = HorizontalAlignment.Left });
        go.Click += (_, _) =>
        {
            if (AI is { } ai && direction.SelectedItem is ComboBoxItem { Tag: AIPipelines.LightDirection d }) ai.Relight(d, color.Color, intensity.Value / 100, environment.Text);
        };
        var from = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(direction, Dock.Right);
        from.Children.Add(direction);
        from.Children.Add(new TextBlock { Text = Loc.T("Light from"), VerticalAlignment = VerticalAlignment.Center });
        var well = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(color, Dock.Right);
        well.Children.Add(color);
        well.Children.Add(new TextBlock { Text = Loc.T("Light color"), VerticalAlignment = VerticalAlignment.Center });
        return new StackPanel { Children = { from, well, intensity, environment, new UnknownPriceNote(), go } };
    }

    private StackPanel Cleanup()
    {
        var body = new StackPanel();
        body.Children.Add(Note("Finds photobombers, litter, signs, cables and dust spots to remove."));
        var find = new Button { Content = Loc.T("Find Distractions"), Style = Compact, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        find.Click += (_, _) => AI?.FindDistractions();
        body.Children.Add(find);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(message);
        var list = new StackPanel();
        body.Children.Add(list);
        var removeAll = new Button { Content = Loc.T("Remove All"), Style = Compact, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        removeAll.Click += (_, _) => { if (AI is { } ai) ai.RemoveDistractions(ai.Distractions); };
        body.Children.Add(removeAll);
        IReadOnlyList<AIPipelines.Distraction>? shown = null;
        sectionRefreshers.Add(() =>
        {
            if (AI is not { } ai) return;
            message.Text = ai.DistractionMessage is { } said ? Loc.T(said) : "";
            message.Visibility = ai.DistractionMessage is null ? Visibility.Collapsed : Visibility.Visible;
            removeAll.Visibility = ai.Distractions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (ReferenceEquals(shown, ai.Distractions)) return;
            shown = ai.Distractions;
            list.Children.Clear();
            foreach (var d in ai.Distractions) list.Children.Add(DistractionRow(d));
        });
        return body;
    }

    private DockPanel DistractionRow(AIPipelines.Distraction d)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        Button Small(string title, Action click)
        {
            var b = new Button { Content = Loc.T(title), Style = Compact, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            b.Click += (_, _) => click();
            return b;
        }
        var dismiss = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = "xmark", Size = 11 }, Margin = new Thickness(4, 0, 0, 0),
                                   VerticalAlignment = VerticalAlignment.Top, ToolTip = Loc.T("Dismiss") };
        AutomationProperties.SetName(dismiss, Loc.T("Dismiss"));
        dismiss.Click += (_, _) => AI?.DismissDistraction(d.Id);
        foreach (var b in new UIElement[] { dismiss, Small("Remove", () => AI?.RemoveDistractions([d])), Small("Select", () => AI?.SelectDistractions([d])) })
        {
            DockPanel.SetDock(b, Dock.Right);
            row.Children.Add(b);
        }
        var text = new StackPanel();
        // How much it disturbs is said as a word; a coloured dot said nothing without colour.
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        line.Inlines.Add(new System.Windows.Documents.Run(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(d.Label) + " · "));
        line.Inlines.Add(new System.Windows.Documents.Run(Loc.T(d.Severity == "high" ? "disturbs a lot" : d.Severity == "low" ? "disturbs a little" : "disturbs"))
            { Foreground = (Brush)FindResource("Glimmer.MutedBrush") });
        text.Children.Add(line);
        if (d.Rationale is { } r)
            text.Children.Add(new TextBlock { Text = r, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)FindResource("Glimmer.Text.Caption") });
        row.Children.Add(text);
        AutomationProperties.SetName(row, d.Label);
        return row;
    }

    /// <summary>Real-ESRGAN holds the result as float pixels; keep it to sizes that fit comfortably.</summary>
    private const int NeuralMaxSide = 16_384;

    private StackPanel Upscale()
    {
        int scale = 2;
        bool neural = AppServices.Settings.Get(AIPreferences.UpscaleNeuralKey, true), enhance = false;
        var scales = new ListBox { Style = (Style)FindResource("Glimmer.Segmented"), Width = 120, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(scales, Loc.T("Scale"));
        scales.Items.Add(new ListBoxItem { Content = Loc.T("2×"), Tag = 2 });
        scales.Items.Add(new ListBoxItem { Content = Loc.T("4×"), Tag = 4 });
        scales.SelectedIndex = 0;
        var method = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(method, Loc.T("Method"));
        method.Items.Add(new ComboBoxItem { Content = Loc.T("Real-ESRGAN (on this Mac)"), Tag = true });
        method.Items.Add(new ComboBoxItem { Content = Loc.T("Lanczos"), Tag = false });
        method.SelectedIndex = neural ? 0 : 1;
        var result = new TextBlock { Style = (Style)FindResource("Glimmer.Text.Caption"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        var tooLarge = new StatusLine { Kind = StatusKind.Error, TextKey = "Too large for Real-ESRGAN; use 2× or Lanczos.", FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
        var detail = new CheckBox { Content = Loc.T("Add AI detail"), Style = (Style)FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 0, 0, 8) };
        var creativity = Percent("Creativity", 0.3);
        var tilesWarning = new StatusLine { Kind = StatusKind.Warning, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };
        var tilesNote = new TextBlock { Style = (Style)FindResource("Glimmer.Text.Caption"), Margin = new Thickness(0, 0, 0, 6) };
        var price = new UnknownPriceNote();
        // Gated by size as well as price, so Update decides alone (and runs again when the price tick is made).
        var go = new Button { Content = Loc.T("Upscale"), Style = Compact, HorizontalAlignment = HorizontalAlignment.Left };
        void OnSettings(object? sender, string key) => Dispatcher.BeginInvoke(Update);
        go.Loaded += (_, _) => AppServices.Settings.Changed += OnSettings;
        go.Unloaded += (_, _) => AppServices.Settings.Changed -= OnSettings;
        void Update()
        {
            if (session is null) return;
            int w = session.State.Width * scale, h = session.State.Height * scale;
            int tiles = AIPipelines.EnhanceTiles(w, h).Count;
            result.Text = Loc.F("Result: {0} × {1} px, opened as a new document.", w, h);
            tooLarge.Visibility = neural && Math.Max(w, h) > NeuralMaxSide ? Visibility.Visible : Visibility.Collapsed;
            creativity.Visibility = price.Visibility = enhance ? Visibility.Visible : Visibility.Collapsed;
            // Many tiles are many paid calls: said as a warning, by its sign, not by a colour on the words.
            // The catalogue's Swedish keeps the English plural ending's slot ("{0} ruta/rutor{1}"): fill it only in English.
            var tileText = Loc.F("Runs the image model on {0} tile{1}.", tiles, Loc.IsSwedish || tiles == 1 ? "" : "s");
            tilesWarning.Verbatim = tileText;
            tilesNote.Text = tileText;
            tilesWarning.Visibility = enhance && tiles > 16 ? Visibility.Visible : Visibility.Collapsed;
            tilesNote.Visibility = enhance && tiles <= 16 ? Visibility.Visible : Visibility.Collapsed;
            bool blocked = (enhance && Math.Max(w, h) > 8192) || (neural && Math.Max(w, h) > NeuralMaxSide);
            go.IsEnabled = !blocked && (!enhance || AIPreferences.PriceAccepted);
        }
        scales.SelectionChanged += (_, _) => { if (scales.SelectedItem is ListBoxItem { Tag: int s }) { scale = s; Update(); } };
        method.SelectionChanged += (_, _) =>
        {
            if (method.SelectedItem is not ComboBoxItem { Tag: bool n }) return;
            neural = n;
            AppServices.Settings.Set(AIPreferences.UpscaleNeuralKey, n);
            Update();
        };
        detail.Click += (_, _) => { enhance = detail.IsChecked == true; Update(); };
        go.Click += (_, _) => AI?.Upscale(scale, neural, enhance, creativity.Value / 100);
        sectionRefreshers.Add(Update);
        return new StackPanel { Children = { scales, method, result, tooLarge, detail, creativity, tilesWarning, tilesNote, price, go } };
    }

    private WrapPanel Presets()
    {
        var grid = new WrapPanel();
        foreach (var preset in Preset.All)
        {
            var swatch = new Border
            {
                Height = 28, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 4),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(preset.Swatch.Item1), (Color)ColorConverter.ConvertFromString(preset.Swatch.Item2), 0),
            };
            var label = new TextBlock { Text = Loc.Maybe(preset.Name), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            var b = new Button
            {
                Width = 120, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0), Style = (Style)FindResource("Glimmer.IconButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = Loc.Maybe(preset.Description),
                Content = new StackPanel { Children = { swatch, label } },
            };
            AutomationProperties.SetName(b, Loc.Maybe(preset.Name));
            AutomationProperties.SetHelpText(b, Loc.Maybe(preset.Description));
            var p = preset;
            b.Click += (_, _) => AI?.ApplyPreset(p);
            grid.Children.Add(b);
        }
        return grid;
    }

    private StackPanel LensBlur()
    {
        var p = new DocumentEffects.LensBlurParameters();
        bool showDepth = false;
        var body = new StackPanel();
        body.Children.Add(Note("Estimates depth on this Mac and blurs what's out of focus. The first time, Glimmer asks before it downloads a 50 MB model."));
        var focus = new ParameterSlider("Focus distance", 0, 100, 1, v => Loc.T(v > 66 ? "Near" : v < 33 ? "Far" : "Middle")) { Value = p.Focus * 100, Margin = new Thickness(0, 0, 0, 8) };
        var range = new ParameterSlider("Focus range", 0, 60, 1, v => $"{(int)v} %") { Value = p.FocusRange * 100, Margin = new Thickness(0, 0, 0, 8) };
        var blur = new ParameterSlider("Blur", 2, 60, 1, v => (v / 10).ToString("0.0", CultureInfo.CurrentCulture) + " %") { Value = p.Amount * 1000, Margin = new Thickness(0, 0, 0, 8) };
        var bokeh = new ParameterSlider("Bokeh", 0, 100, 1, v => $"{(int)v} %") { Value = p.Bokeh * 100, Margin = new Thickness(0, 0, 0, 8) };
        void Changed()
        {
            p = new DocumentEffects.LensBlurParameters(focus.Value / 100, range.Value / 100, blur.Value / 1000, bokeh.Value / 100);
            if (AI is { IsLensBlurLive: true } ai) ai.UpdateLensBlur(p, showDepth);
        }
        foreach (var s in new[] { focus, range, blur, bokeh })
        {
            var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ParameterSlider.ValueProperty, typeof(ParameterSlider));
            descriptor.AddValueChanged(s, (_, _) => Changed());
            body.Children.Add(s);
        }
        var depth = new CheckBox { Content = Loc.T("Show depth map"), ToolTip = Loc.T("White is near, black is far"), Style = (Style)FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 0, 0, 8) };
        depth.Click += (_, _) => { showDepth = depth.IsChecked == true; if (AI is { IsLensBlurLive: true } ai) ai.UpdateLensBlur(p, showDepth); };
        var cancel = new Button { Content = Loc.T("Cancel"), Style = Compact, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = Loc.T("Apply"), Style = (Style)FindResource("Glimmer.Compact.AccentButton") };
        void Finish(bool keep)
        {
            AI?.EndLensBlur(keep, p);
            showDepth = false;
            depth.IsChecked = false;
        }
        cancel.Click += (_, _) => Finish(false);
        apply.Click += (_, _) => Finish(true);
        var live = new StackPanel { Children = { depth, new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, apply } } } };
        var preview = new Button { Content = Loc.T("Preview Lens Blur"), Style = Compact, ToolTip = Loc.T("Shows the blur on the canvas while you adjust it") };
        preview.Click += (_, _) => AI?.BeginLensBlur(p);
        var preparing = new Spinner { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var idle = new StackPanel { Orientation = Orientation.Horizontal, Children = { preview, preparing } };
        body.Children.Add(live);
        body.Children.Add(idle);
        sectionRefreshers.Add(() =>
        {
            bool isLive = AI?.IsLensBlurLive == true;
            live.Visibility = isLive ? Visibility.Visible : Visibility.Collapsed;
            idle.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;
            preview.IsEnabled = AI?.IsPreparingLensBlur != true;
            preparing.Visibility = AI?.IsPreparingLensBlur == true ? Visibility.Visible : Visibility.Collapsed;
        });
        // Leaving the panel with the preview on cancels it, as the Mac's onDisappear does.
        Unloaded += (_, _) => { if (AI is { IsLensBlurLive: true }) Finish(false); };
        return body;
    }
}
