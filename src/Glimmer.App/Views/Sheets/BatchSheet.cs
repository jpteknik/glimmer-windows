// Ported from App/BatchSheet.swift (BatchSheet, BatchFileRow, BatchBadge, BatchRatingBar, BatchProgressSection) and
// App/ExportSheet.swift (ExportLimitsFields). The runner itself (BatchRunner) is the editor library's IBatchRunner.
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

/// <summary>Limit longest side, and for formats with a quality setting, Limit file size (ExportLimitsFields).</summary>
public static class ExportLimits
{
    public static IEnumerable<UIElement> Rows(ExportOptions options, Action<ExportOptions> set, Action rebuild, Func<int, string, TextBox> numberField)
    {
        var app = Application.Current;
        var limitSide = new CheckBox { Content = Loc.T("Limit longest side"), IsChecked = options.MaxPixelSide is not null, Style = (Style)app.FindResource("Glimmer.Compact.CheckBox") };
        limitSide.Click += (_, _) => { set(options with { MaxPixelSide = limitSide.IsChecked == true ? 2048 : null }); rebuild(); };
        yield return limitSide;
        if (options.MaxPixelSide is int side)
        {
            var box = numberField(side, "Longest side");
            box.LostKeyboardFocus += (_, _) => { if (Parse(box) is int v) set(options with { MaxPixelSide = Math.Max(16, v) }); };
            yield return Labeled("Longest side", box, "px");
        }
        if (!options.Format.HasQuality()) yield break;
        var limitBytes = new CheckBox { Content = Loc.T("Limit file size"), IsChecked = options.MaxBytes is not null, Style = (Style)app.FindResource("Glimmer.Compact.CheckBox") };
        limitBytes.Click += (_, _) => { set(options with { MaxBytes = limitBytes.IsChecked == true ? 500_000 : null }); rebuild(); };
        yield return limitBytes;
        if (options.MaxBytes is int bytes)
        {
            var box = numberField(bytes / 1000, "At most");
            box.LostKeyboardFocus += (_, _) => { if (Parse(box) is int v) set(options with { MaxBytes = Math.Max(5, v) * 1000 }); };
            yield return Labeled("At most", box, "kB");
            yield return new TextBlock { Text = Loc.T("Glimmer finds the best quality that fits, and scales the image down only if it must."), TextWrapping = TextWrapping.Wrap,
                                         Style = (Style)app.FindResource("Glimmer.Text.Secondary") };
        }
    }

    private static int? Parse(TextBox box) =>
        int.TryParse(box.Text.Replace(" ", "").Replace(" ", ""), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? v : null;

    private static DockPanel Labeled(string label, TextBox box, string unit)
    {
        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { box, new TextBlock { Text = " " + Loc.T(unit), VerticalAlignment = VerticalAlignment.Center,
                                                                                                            Foreground = (Brush)Application.Current.FindResource("Glimmer.MutedBrush") } } };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = Loc.T(label), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}

/// <summary>File › Batch Process…: runs a recipe over many images and writes the results to a folder.</summary>
public sealed class BatchSheet : SheetWindow
{
    private readonly IEditorSession session;
    private readonly IBatchRunner runner;
    private List<string> files = [];
    private BatchRecipe recipe = new BatchRecipe.AutoEnhance();
    private ExportOptions options = new() { Format = ExportFormat.Jpeg };
    private string? destination;
    private string suffix = "";
    private bool isAdding;

    private readonly ListBox list = new() { MinHeight = 110, MaxHeight = 160, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly StackPanel empty = new() { MinHeight = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border dropArea = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, AllowDrop = true };
    private readonly TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button clear;
    private readonly Spinner adding = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 8, 0), Visibility = Visibility.Collapsed };
    private readonly DockPanel ratingBar = new() { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel recipeForm = new();
    private readonly StackPanel outputForm = new();
    private readonly StackPanel progressPanel = new();
    private readonly Button process, showInExplorer, stop, close;

    public BatchSheet(IEditorSession session, IBatchRunner runner) : base("Batch Process", 520)
    {
        this.session = session;
        this.runner = runner;
        Height = 660;
        SizeToContent = SizeToContent.Manual;

        // Images
        Body.Children.Add(Header("Images"));
        empty.Children.Add(new Icon { Symbol = "photo.stack", Size = 28, Foreground = (Brush)FindResource("Glimmer.MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        empty.Children.Add(new TextBlock { Text = Loc.T("Drop images or folders here"), Style = (Style)FindResource("Glimmer.Text.Secondary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        AutomationProperties.SetName(list, Loc.T("Images"));
        list.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Delete or Key.Back) || list.SelectedItems.Count == 0) return;
            var remove = list.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToHashSet();
            files = [.. files.Where(f => !remove.Contains(f))];
            Update();
        };
        dropArea.Child = new Grid { Children = { empty, list } };
        dropArea.DragEnter += (_, e) => { dropArea.BorderBrush = (Brush)FindResource("Glimmer.AccentBrush"); e.Effects = DragDropEffects.Copy; };
        dropArea.DragLeave += (_, _) => dropArea.BorderBrush = Brushes.Transparent;
        dropArea.Drop += (_, e) =>
        {
            dropArea.BorderBrush = Brushes.Transparent;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Add(dropped);
        };
        var add = SmallButton("Add Images…", "plus");
        add.Click += (_, _) => Add(AppServices.FileDialogs.ChooseImages("Add Images…", "Add", multiple: true));
        // Windows' file dialog chooses files or a folder, never both: folders have their own button.
        var addFolder = SmallButton("Add Folder…", "folder");
        addFolder.Click += (_, _) => { if (AppServices.FileDialogs.ChooseFolder("Add Folder…") is { } folder) Add([folder]); };
        clear = SmallButton("Clear", null);
        clear.Click += (_, _) => { files = []; Update(); };
        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in new UIElement[] { add, addFolder, clear }) { DockPanel.SetDock(b, Dock.Left); buttons.Children.Add(b); }
        DockPanel.SetDock(count, Dock.Right);
        DockPanel.SetDock(adding, Dock.Right);
        buttons.Children.Add(count);
        buttons.Children.Add(adding);
        Body.Children.Add(Section(dropArea, buttons, ratingBar));

        // Recipe and output
        Body.Children.Add(Header("Recipe"));
        Body.Children.Add(recipeForm);
        Body.Children.Add(Header("Output"));
        Body.Children.Add(outputForm);
        Body.Children.Add(progressPanel);

        // Buttons: Windows' order, the default first.
        process = new Button { IsDefault = true, Style = (Style)FindResource("Glimmer.Compact.AccentButton"), MinWidth = 80 };
        process.Click += (_, _) => { if (destination is not null) runner.Start(files, recipe, options, destination, suffix); };
        stop = new Button { Content = Loc.T("Stop"), Style = (Style)FindResource("Glimmer.Compact.Button"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        stop.Click += (_, _) => runner.Cancel();
        close = new Button { Content = Loc.T("Close"), IsCancel = true, Style = (Style)FindResource("Glimmer.Compact.Button"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        showInExplorer = new Button { Content = Loc.T("Show in Finder"), Style = (Style)FindResource("Glimmer.Compact.Button") };
        showInExplorer.Click += (_, _) => ShowInExplorer();
        var bottom = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in new[] { close, stop, process }) { DockPanel.SetDock(b, Dock.Right); bottom.Children.Add(b); }
        DockPanel.SetDock(showInExplorer, Dock.Left);
        bottom.Children.Add(showInExplorer);
        Body.Children.Add(bottom);

        runner.PropertyChanged += OnRunnerChanged;
        Closing += (_, e) => { if (runner.IsRunning) runner.Cancel(); };
        Closed += (_, _) => runner.PropertyChanged -= OnRunnerChanged;
        BuildRecipe();
        BuildOutput();
        Update();
    }

    private void OnRunnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Update);

    private TextBlock Header(string key) => new() { Text = Loc.T(key), FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 4, 0, 6), Foreground = (Brush)FindResource("Glimmer.InkSoftBrush") };

    private Button SmallButton(string title, string? symbol)
    {
        var b = new Button { Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(0, 0, 6, 0) };
        b.Content = symbol is null ? Loc.T(title)
            : new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = symbol, Size = 11, Margin = new Thickness(0, 0, 5, 0) }, new TextBlock { Text = Loc.T(title) } } };
        AutomationProperties.SetName(b, Loc.T(title));
        return b;
    }

    /// <summary>Adds image files, looking one level into folders. A folder is read off the UI thread, so a very large one
    /// does not stop the window; each file is added once.</summary>
    private async void Add(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        isAdding = true;
        Update();
        var found = await Task.Run(() => BatchFiles.Images(paths));
        files = BatchFiles.Merge(files, found);
        isAdding = false;
        Update();
    }

    private void BuildRecipe()
    {
        recipeForm.Children.Clear();
        var choices = new List<(BatchRecipe Recipe, string Title, bool Verbatim)>
        {
            (new BatchRecipe.AutoEnhance(), "Auto Enhance", false), (new BatchRecipe.RemoveBackground(), "Remove Background", false),
            (new BatchRecipe.ConvertOnly(), "Nothing (convert only)", false),
        };
        choices.AddRange(Preset.All.Select(p => ((BatchRecipe)new BatchRecipe.Preset(p.Id), p.Name, false)));
        choices.AddRange(session.AI.SavedActions.Select(a => ((BatchRecipe)new BatchRecipe.Action(a.Id), a.Name, true)));
        var picker = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), MinWidth = 200 };
        AutomationProperties.SetName(picker, Loc.T("Apply"));
        int presetsAt = 3, actionsAt = 3 + Preset.All.Length;
        for (int i = 0; i < choices.Count; i++)
        {
            if (i == presetsAt || (i == actionsAt && actionsAt < choices.Count)) picker.Items.Add(new Separator());
            var (r, title, verbatim) = choices[i];
            picker.Items.Add(new ComboBoxItem { Content = verbatim ? title : Loc.Maybe(title), Tag = r });
        }
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, recipe));
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is ComboBoxItem { Tag: BatchRecipe r }) { recipe = r; BuildRecipe(); } };
        var rows = new List<UIElement> { Row("Apply", picker) };
        if (recipe is BatchRecipe.Action)
            rows.Add(new TextBlock { Text = Loc.T("Steps that need the window or a paid AI job are not run in a batch."), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary") });
        if (recipe is BatchRecipe.RemoveBackground && !options.Format.SupportsAlpha())
            rows.Add(new TextBlock { Text = Loc.T("JPEG has no transparency: the background turns white. Choose PNG, HEIC or WebP to keep it."), TextWrapping = TextWrapping.Wrap,
                                     Style = (Style)FindResource("Glimmer.Text.Secondary") });
        recipeForm.Children.Add(Section([.. rows]));
    }

    private void BuildOutput()
    {
        outputForm.Children.Clear();
        var rows = new List<UIElement>
        {
            Row("Format", Picker(Enum.GetValues<ExportFormat>().Where(ImageExporter.CanWrite), f => f.Title(), options.Format, f => { options = options with { Format = f }; BuildOutput(); BuildRecipe(); })),
        };
        if (options.Format.HasQuality())
        {
            var slider = new ValueSlider("Quality", 0.1, 1, options.Quality, v => v.ToString("P0", CultureInfo.CurrentCulture), width: 160, readoutWidth: 44);
            slider.Changed += (_, v) => options = options with { Quality = v };
            rows.Add(Row("Quality", slider));
        }
        rows.AddRange(ExportLimits.Rows(options, o => options = o, BuildOutput, (v, name) => NumberField(v, name, 80)));
        var folder = new TextBlock
        {
            Text = destination is null ? Loc.T("Choose a folder") : Path.GetFileName(destination.TrimEnd('\\')) is { Length: > 0 } n ? n : destination,
            Foreground = (Brush)FindResource(destination is null ? "Glimmer.MutedBrush" : "Glimmer.InkBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, ToolTip = destination, Margin = new Thickness(0, 0, 8, 0),
        };
        var choose = new Button { Content = Loc.T("Choose…"), Style = (Style)FindResource("Glimmer.Compact.Button") };
        choose.Click += (_, _) =>
        {
            if (AppServices.FileDialogs.ChooseFolder("Save Here", destination) is not { } chosen) return;
            destination = chosen;
            BuildOutput();
            Update();
        };
        AutomationProperties.SetName(choose, Loc.T("Save to"));
        rows.Add(Row("Save to", new StackPanel { Orientation = Orientation.Horizontal, Children = { folder, choose } }));
        var name = new TextBox { Text = suffix, Width = 160, Style = (Style)FindResource("Glimmer.Compact.TextBox"), Tag = Loc.T("e.g. -edited") };
        AutomationProperties.SetName(name, Loc.T("Name suffix"));
        name.TextChanged += (_, _) => suffix = name.Text;
        rows.Add(Row("Name suffix", name));
        outputForm.Children.Add(Section([.. rows]));
    }

    private void Update()
    {
        // The list: one row per file, its name, badges, stars and outcome.
        empty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        list.Visibility = files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        list.Items.Clear();
        foreach (var f in files) list.Items.Add(FileRow(f));
        count.Text = Loc.F("{0} images", files.Count);
        clear.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        adding.Visibility = isAdding ? Visibility.Visible : Visibility.Collapsed;
        BuildRatingBar();
        BuildProgress();
        process.Content = Loc.F("Process {0} Images", files.Count);
        process.IsEnabled = files.Count > 0 && destination is not null && !runner.IsRunning && !runner.IsRating && !isAdding;
        stop.Visibility = runner.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        close.Visibility = runner.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        showInExplorer.Visibility = !runner.IsRunning && runner.Done > 0 && destination is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListBoxItem FileRow(string path)
    {
        var row = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (runner.Rating(path) is { } r)
        {
            // A property of the photo, not a state: a quiet outlined badge.
            if (r.IsBlurry) right.Children.Add(Badge("Blurry"));
            if (r.IsUtility) right.Children.Add(Badge("Document"));
            right.Children.Add(new TextBlock
            {
                Text = new string('★', r.Stars) + new string('☆', 5 - r.Stars), FontSize = 11, Foreground = (Brush)FindResource("Glimmer.AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.F("Aesthetics {0}, sharpness {1} %", r.Aesthetics.ToString("0.00", CultureInfo.CurrentCulture), (int)(r.Sharpness * 100)),
                Margin = new Thickness(6, 0, 0, 0),
            });
        }
        if (runner.Succeeded(path) is { } ok)
            right.Children.Add(new StatusSymbol { Kind = ok ? StatusKind.Success : StatusKind.Error, Margin = new Thickness(6, 0, 0, 0) });
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new ListBoxItem { Content = row, Tag = path, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, Path.GetFileName(path));
        return item;
    }

    private Border Badge(string key) => new()
    {
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0),
        BorderBrush = new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)FindResource("Glimmer.MutedBrush")).Color, 0.6)), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = Loc.T(key), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Glimmer.MutedBrush") },
    };

    /// <summary>Rate the photos, then sort them or keep only the best (BatchRatingBar).</summary>
    private void BuildRatingBar()
    {
        ratingBar.Children.Clear();
        ratingBar.Visibility = files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (files.Count == 0) return;
        if (runner.IsRating)
        {
            var progress = new ProgressBar { Width = 120, Height = 6, Maximum = Math.Max(files.Count, 1), Value = runner.Rated, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(progress, Loc.T("Rating…"));
            DockPanel.SetDock(progress, Dock.Left);
            ratingBar.Children.Add(progress);
            ratingBar.Children.Add(new TextBlock { Text = Loc.T("Rating…"), Style = (Style)FindResource("Glimmer.Text.Secondary"), Margin = new Thickness(8, 0, 0, 0) });
            return;
        }
        bool ratedAll = files.All(f => runner.Rating(f) is not null);
        if (!ratedAll)
        {
            var rate = SmallButton("Rate Photos", "star.leadinghalf.filled");
            rate.ToolTip = Loc.T("Scores every photo for aesthetics and sharpness on this Mac, so you can keep the best");
            rate.Click += (_, _) => runner.Rate(files);
            ratingBar.Children.Add(rate);
            return;
        }
        var best = SmallButton("Best First", "arrow.up.arrow.down");
        best.Click += (_, _) => { files = [.. files.OrderByDescending(f => runner.Rating(f)?.Score ?? -9)]; Update(); };
        ratingBar.Children.Add(best);
        var keep = SmallButton("Keep", null);
        keep.Content = Loc.T("Keep") + " ▾";
        keep.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = keep, Placement = PlacementMode.Bottom };
            void Item(string title, Func<string, bool> drop)
            {
                var item = new MenuItem { Header = Loc.T(title) };
                item.Click += (_, _) => { files.RemoveAll(f => drop(f)); Update(); };
                menu.Items.Add(item);
            }
            Item("Without Blurry Photos", f => runner.Rating(f)?.IsBlurry == true);
            Item("Without Documents and Screenshots", f => runner.Rating(f)?.IsUtility == true);
            menu.Items.Add(new Separator());
            Item("4 Stars and Up", f => (runner.Rating(f)?.Stars ?? 0) < 4);
            var half = new MenuItem { Header = Loc.T("Best Half") };
            half.Click += (_, _) => KeepBest(0.5);
            var quarter = new MenuItem { Header = Loc.T("Best Quarter") };
            quarter.Click += (_, _) => KeepBest(0.25);
            menu.Items.Add(half);
            menu.Items.Add(quarter);
            menu.IsOpen = true;
        };
        ratingBar.Children.Add(keep);
        int blurry = files.Count(f => runner.Rating(f)?.IsBlurry == true);
        if (blurry > 0)
        {
            var label = new TextBlock { Text = Loc.F("{0} blurry", blurry), Style = (Style)FindResource("Glimmer.Text.Secondary"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(label, Dock.Right);
            ratingBar.Children.Add(label);
        }
    }

    private void KeepBest(double fraction)
    {
        int keep = Math.Max(1, (int)Math.Ceiling(files.Count * fraction));
        var best = files.OrderByDescending(f => runner.Rating(f)?.Score ?? -9).Take(keep).ToHashSet();
        files.RemoveAll(f => !best.Contains(f));
        Update();
    }

    /// <summary>The progress of a run and the files that failed (BatchProgressSection).</summary>
    private void BuildProgress()
    {
        progressPanel.Children.Clear();
        if (runner.Total == 0) return;
        var label = runner.IsRunning ? Loc.F("Processing {0} of {1}…", runner.Done + 1, runner.Total) : Loc.F("Finished {0} of {1}", runner.Done - runner.Failures.Count, runner.Total);
        var bar = new ProgressBar { Height = 6, Maximum = Math.Max(runner.Total, 1), Value = runner.Done, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(bar, label);
        var rows = new List<UIElement> { new StackPanel { Children = { new TextBlock { Text = label }, bar } } };
        rows.AddRange(runner.Failures.Select(o => (UIElement)new StatusLine { Kind = StatusKind.Error, Verbatim = $"{Path.GetFileName(o.Source)}: {o.Error}" }));
        progressPanel.Children.Add(Section([.. rows]));
    }

    private void ShowInExplorer()
    {
        var written = runner.Outcomes.Select(o => o.Output).OfType<string>().ToList();
        if (written.Count > 0) Process.Start(new ProcessStartInfo("explorer", $"/select,\"{written[0]}\"") { UseShellExecute = true });
        else if (destination is not null) Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
    }
}
