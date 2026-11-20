// Ported from App/NewDocumentView.swift, App/ImageSizeSheet.swift, App/CanvasSizeSheet.swift, App/ExportSheet.swift,
// App/ToolOptionsBar.swift (SelectionModifySheet) and App/WelcomeView.swift.
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Sheets;

/// <summary>New…: a preset or a custom size, and the background.</summary>
public sealed class NewDocumentView : SheetWindow
{
    private static readonly (string Name, int Width, int Height)[] Presets =
    [
        ("HD 1920 × 1080", 1920, 1080), ("4K 3840 × 2160", 3840, 2160), ("Square 1024", 1024, 1024), ("Square 2048", 2048, 2048),
        ("Portrait 1080 × 1350", 1080, 1350), ("Story 1080 × 1920", 1080, 1920), ("A4 at 150 ppi", 1240, 1754),
    ];
    private const int MaxDimension = DocumentManifest.MaxDimension;

    private readonly TextBox width, height;
    private readonly StatusLine error = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private string background;
    private SRGBColor custom = new(0.95, 0.93, 0.88);
    private readonly DockPanel customRow;

    public NewDocumentView() : base("New…", 400)
    {
        var settings = AppServices.Settings;
        width = NumberField(settings.Get("newDocument.width", 1920), "Width");
        height = NumberField(settings.Get("newDocument.height", 1080), "Height");
        background = settings.Get("newDocument.background", "white");
        var preset = new ComboBox { Style = (Style)FindResource("Glimmer.Compact.ComboBox"), MinWidth = 180 };
        preset.Items.Add(new ComboBoxItem { Content = Loc.T("Custom") });
        foreach (var p in Presets) preset.Items.Add(new ComboBoxItem { Content = Loc.T(p.Name), Tag = p });
        void SelectPreset()
        {
            var match = preset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is (string, int w, int h) && ParseInt(width) == w && ParseInt(height) == h);
            preset.SelectedItem = match ?? preset.Items[0];
        }
        SelectPreset();
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is ComboBoxItem { Tag: (string, int w, int h) })
            {
                width.Text = w.ToString(CultureInfo.CurrentCulture);
                height.Text = h.ToString(CultureInfo.CurrentCulture);
            }
        };
        width.TextChanged += (_, _) => { Validate(); };
        height.TextChanged += (_, _) => { Validate(); };
        var backgroundPicker = Picker(["white", "black", "transparent", "custom"], b => char.ToUpperInvariant(b[0]) + b[1..], background, b =>
        {
            background = b;
            customRow!.Visibility = b == "custom" ? Visibility.Visible : Visibility.Collapsed;
        });
        var well = new ColorWell { Label = "Color", Color = custom };
        well.Committed += (_, _) => custom = well.Color;
        customRow = Row("Color", well);
        customRow.Visibility = background == "custom" ? Visibility.Visible : Visibility.Collapsed;
        Body.Children.Add(Section(Row("Preset", preset), Row("Width", width), Row("Height", height), Row("Background", backgroundPicker), customRow));
        Body.Children.Add(error);
        Body.Children.Add(Buttons("Create", Create, IsValid));
        Validate();
    }

    private bool SidesValid => ParseInt(width) is >= 1 and <= MaxDimension && ParseInt(height) is >= 1 and <= MaxDimension;
    private bool IsValid() => SidesValid;

    private void Validate()
    {
        error.TextKey = null;
        error.Verbatim = SidesValid ? null : Loc.F("Width and height must be between 1 and {0} pixels.", MaxDimension);
        error.Visibility = SidesValid ? Visibility.Collapsed : Visibility.Visible;
        Validation();
    }

    private void Create()
    {
        if (!IsValid()) return;
        int w = ParseInt(width)!.Value, h = ParseInt(height)!.Value;
        AppServices.Settings.Set("newDocument.width", w);
        AppServices.Settings.Set("newDocument.height", h);
        AppServices.Settings.Set("newDocument.background", background);
        var bg = background switch
        {
            "black" => DocumentState.Background.Black, "transparent" => DocumentState.Background.Transparent,
            "custom" => DocumentState.Background.Of(new System.Numerics.Vector4((float)Linear(custom.R), (float)Linear(custom.G), (float)Linear(custom.B), 1)),
            _ => DocumentState.Background.White,
        };
        Close();
        AppServices.Opener?.CreateDocument(DocumentState.New(w, h, bg));
    }

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}

/// <summary>Image › Image Size…: a new pixel size for the whole image.</summary>
public sealed class ImageSizeSheet : SheetWindow
{
    private const int MaxSide = CanvasSteps.MaxSide;
    private readonly IEditorSession session;
    private readonly TextBox width, height;
    private readonly CheckBox keep;
    private readonly TextBlock percent = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StatusLine error = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private bool syncing;

    public ImageSizeSheet(IEditorSession session) : base("Image Size", 340)
    {
        this.session = session;
        int oldW = session.State.Width, oldH = session.State.Height;
        Body.Children.Add(Headline("Image Size"));
        Body.Children.Add(Secondary(Loc.F("Now {0} × {1} pixels", oldW, oldH)));
        width = NumberField(oldW, "Width in pixels");
        height = NumberField(oldH, "Height in pixels");
        Body.Children.Add(Grid(("Width", width), ("Height", height)));
        keep = new CheckBox { Content = Loc.T("Keep proportions"), IsChecked = true, Style = (Style)FindResource("Glimmer.Compact.CheckBox"), Margin = new Thickness(0, 10, 0, 10) };
        Body.Children.Add(keep);
        var presets = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var p in new[] { 25, 50, 200 })
        {
            var b = new Button { Content = $"{p} %", Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) =>
            {
                syncing = true;
                width.Text = Math.Max(1, (int)Math.Round(oldW * p / 100.0)).ToString(CultureInfo.CurrentCulture);
                height.Text = Math.Max(1, (int)Math.Round(oldH * p / 100.0)).ToString(CultureInfo.CurrentCulture);
                syncing = false;
                Validate();
            };
            DockPanel.SetDock(b, Dock.Left);
            presets.Children.Add(b);
        }
        DockPanel.SetDock(percent, Dock.Right);
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        presets.Children.Add(percent);
        Body.Children.Add(presets);
        Body.Children.Add(error);
        // A side the sheet set itself doesn't set the other one back.
        width.TextChanged += (_, _) => Follow(width, height, oldH / (double)oldW);
        height.TextChanged += (_, _) => Follow(height, width, oldW / (double)oldH);
        keep.Click += (_, _) => Follow(width, height, oldH / (double)oldW);
        Body.Children.Add(Buttons("Resize", Resize, IsValid));
        Loaded += (_, _) => { width.Focus(); width.SelectAll(); };
        Validate();
    }

    private Grid Grid(params (string Label, TextBox Box)[] rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Loc.T(rows[i].Label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) };
            var box = rows[i].Box;
            box.Margin = new Thickness(0, 0, 8, 8);
            var unit = new TextBlock { Text = Loc.T("pixels"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            System.Windows.Controls.Grid.SetRow(label, i); System.Windows.Controls.Grid.SetRow(box, i); System.Windows.Controls.Grid.SetRow(unit, i);
            System.Windows.Controls.Grid.SetColumn(box, 1); System.Windows.Controls.Grid.SetColumn(unit, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(unit);
        }
        return grid;
    }

    private void Follow(TextBox changed, TextBox other, double ratio)
    {
        if (syncing) return;
        if (keep.IsChecked == true && ParseInt(changed) is int v and > 0)
        {
            syncing = true;
            other.Text = Math.Max(1, (int)Math.Round(v * ratio)).ToString(CultureInfo.CurrentCulture);
            syncing = false;
        }
        Validate();
    }

    private bool IsValid() => ParseInt(width) is >= 1 and <= MaxSide && ParseInt(height) is >= 1 and <= MaxSide;

    private void Validate()
    {
        bool ok = IsValid();
        error.Verbatim = ok ? null : Loc.F("Each side must be 1 to {0} pixels.", MaxSide);
        error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        percent.Text = ok && keep.IsChecked == true ? $"{(int)Math.Round(ParseInt(width)!.Value / (double)Math.Max(session.State.Width, 1) * 100)} %" : "";
        Validation();
    }

    private void Resize()
    {
        try { session.ResizeImage(ParseInt(width)!.Value, ParseInt(height)!.Value); }
        catch (Exception ex) { session.Report(ex); }
        Close();
    }
}

/// <summary>Image › Canvas Size…: more or less room around the image, without scaling it.</summary>
public sealed class CanvasSizeSheet : SheetWindow
{
    private static readonly string[][] AnchorNames =
        [["Top Left", "Top Center", "Top Right"], ["Left", "Center", "Right"], ["Bottom Left", "Bottom Center", "Bottom Right"]];
    private const int MaxSide = CanvasSteps.MaxSide;
    private readonly IEditorSession session;
    private readonly TextBox width, height;
    private readonly StatusLine error = new() { Kind = StatusKind.Error, Visibility = Visibility.Collapsed };
    private int anchorX = 1, anchorY = 1;
    private readonly List<ToggleButton_> anchors = [];

    private sealed class ToggleButton_ : System.Windows.Controls.Primitives.ToggleButton
    {
        public int X, Y;
    }

    public CanvasSizeSheet(IEditorSession session) : base("Canvas Size", 400)
    {
        this.session = session;
        Body.Children.Add(Headline("Canvas Size"));
        Body.Children.Add(Secondary(Loc.F("Now {0} × {1} pixels", session.State.Width, session.State.Height)));
        width = NumberField(session.State.Width, "Width in pixels", 80);
        height = NumberField(session.State.Height, "Height in pixels", 80);
        var fields = new StackPanel();
        fields.Children.Add(Row("Width", new StackPanel { Orientation = Orientation.Horizontal, Children = { width, new TextBlock { Text = " " + Loc.T("pixels"), VerticalAlignment = VerticalAlignment.Center } } }));
        fields.Children.Add(new Border { Height = 8 });
        fields.Children.Add(Row("Height", new StackPanel { Orientation = Orientation.Horizontal, Children = { height, new TextBlock { Text = " " + Loc.T("pixels"), VerticalAlignment = VerticalAlignment.Center } } }));
        var grid = new UniformGrid_(3);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var b = new ToggleButton_ { X = x, Y = y, Width = 22, Height = 22, Margin = new Thickness(2), Template = AnchorTemplate() };
                AutomationProperties.SetName(b, Loc.T(AnchorNames[y][x]));
                b.Click += (_, _) => { anchorX = b.X; anchorY = b.Y; UpdateAnchors(); };
                anchors.Add(b);
                grid.Children.Add(b);
            }
        UpdateAnchors();
        var anchorPanel = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        anchorPanel.Children.Add(new TextBlock { Text = Loc.T("Anchor"), Foreground = (Brush)FindResource("Glimmer.MutedBrush"), Margin = new Thickness(0, 0, 0, 6) });
        anchorPanel.Children.Add(grid);
        Body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { fields, anchorPanel } });
        Body.Children.Add(new TextBlock { Margin = new Thickness(0, 14, 0, 10), Style = (Style)FindResource("Glimmer.Text.Secondary"),
                                          Text = Loc.T("The image stays where the anchor is. New room is transparent; what a smaller canvas cuts off is kept outside it.") });
        Body.Children.Add(error);
        width.TextChanged += (_, _) => Validate();
        height.TextChanged += (_, _) => Validate();
        Body.Children.Add(Buttons("Resize", Resize, IsValid));
        Validate();
    }

    private sealed class UniformGrid_ : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid_(int columns) { Columns = columns; Rows = columns; HorizontalAlignment = HorizontalAlignment.Left; }
    }

    private ControlTemplate AnchorTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = factory };
    }

    private void UpdateAnchors()
    {
        foreach (var a in anchors)
        {
            bool on = a.X == anchorX && a.Y == anchorY;
            a.IsChecked = on;
            a.Tag = on ? FindResource("Glimmer.AccentBrush") : new SolidColorBrush(Theme.WithAlpha(Theme.Muted, 0.25));
        }
    }

    private bool IsValid() => ParseInt(width) is >= 1 and <= MaxSide && ParseInt(height) is >= 1 and <= MaxSide;

    private void Validate()
    {
        bool ok = IsValid();
        error.Verbatim = ok ? null : Loc.F("Each side must be 1 to {0} pixels.", MaxSide);
        error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        Validation();
    }

    private void Resize()
    {
        int w = ParseInt(width)!.Value, h = ParseInt(height)!.Value;
        if (w != session.State.Width || h != session.State.Height)
        {
            try { session.ResizeCanvas(w, h, anchorX, anchorY); }
            catch (Exception ex) { session.Report(ex); }
        }
        Close();
    }
}

/// <summary>File › Export…: format, depth, quality, size and file-size limits, colour profile.</summary>
public sealed class ExportSheet : SheetWindow
{
    private readonly IEditorSession session;
    private ExportOptions options;
    private readonly StackPanel form = new();

    public ExportSheet(IEditorSession session) : base("Export", 420)
    {
        this.session = session;
        options = session.ExportOptions;
        // HEIC needs the system's encoder; a format this computer can't write is not offered, so it isn't kept either.
        if (!ImageExporter.CanWrite(options.Format)) options = options with { Format = ExportFormat.Png };
        Body.Children.Add(form);
#if GLIMMER_PSD
        // A Photoshop file keeps the layers, so it has its own step: what the file will hold is shown before it is saved.
        var psd = new Button { Content = Loc.T("Photoshop File…"), Style = (Style)FindResource("Glimmer.Compact.Button"),
                               ToolTip = Loc.T("Export the document with its layers as a Photoshop file (PSD)") };
        psd.Click += (_, _) => { Close(); session.IsPSDExportPresented = true; };
        Body.Children.Add(Buttons("Export…", ExportNow, null, psd));
#else
        Body.Children.Add(Buttons("Export…", ExportNow));
#endif
        Build();

        void ExportNow()
        {
            session.ExportOptions = options;
            Close();
            session.Export(options);
        }
    }

    private void Build()
    {
        form.Children.Clear();
        var format = options.Format;
        var rows = new List<UIElement>
        {
            Row("Format", Picker(Enum.GetValues<ExportFormat>().Where(ImageExporter.CanWrite), f => f.Title(), format, f => { options = options with { Format = f }; Build(); })),
        };
        if (format.BitDepths().Count > 1)
            rows.Add(Row("Bit depth", Picker(format.BitDepths(), d => d.Title(), options.EffectiveBitDepth, d => options = options with { BitDepth = d })));
        if (format.HasQuality())
        {
            var shown = new TextBlock { Width = 44, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = 0.1, Maximum = 1, Value = options.Quality, Width = 160, Style = (Style)FindResource("Glimmer.Compact.Slider") };
            AutomationProperties.SetName(slider, Loc.T("Quality"));
            void Show() { shown.Text = options.Quality.ToString("P0", CultureInfo.CurrentCulture); AutomationProperties.SetItemStatus(slider, shown.Text); }
            slider.ValueChanged += (_, e) => { options = options with { Quality = e.NewValue }; Show(); };
            Show();
            rows.Add(Row("Quality", new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, shown } }));
        }
        rows.AddRange(ExportLimits.Rows(options, o => options = o, Build, (v, name) => NumberField(v, name, 80)));
        if (format == ExportFormat.Webp) rows.Add(new TextBlock { Text = Loc.T("WebP is lossless at 100 % and lossy below."), Style = (Style)FindResource("Glimmer.Text.Secondary") });
        rows.Add(Row("Color profile", Picker([WorkingColorSpace.SRGB, WorkingColorSpace.DisplayP3], c => c == WorkingColorSpace.SRGB ? "sRGB" : "Display P3",
                                             options.ColorSpace, c => options = options with { ColorSpace = c })));
        if (!format.SupportsAlpha()) rows.Add(new TextBlock { Text = Loc.T("Transparent areas are flattened onto white."), Style = (Style)FindResource("Glimmer.Text.Secondary") });
        rows.Add(Row("Size", new TextBlock { Text = OutputSize(session.State.Width, session.State.Height, options.MaxPixelSide) }));
        form.Children.Add(Section([.. rows]));
    }

    public static string OutputSize(int w, int h, int? maxSide)
    {
        if (maxSide is not int side || side <= 0 || Math.Max(w, h) <= side) return $"{w} × {h} px";
        double k = (double)side / Math.Max(w, h);
        return $"{(int)Math.Round(w * k)} × {(int)Math.Round(h * k)} px";
    }
}

/// <summary>Feather / Expand / Contract radius prompt.</summary>
public sealed class SelectionModifySheet : SheetWindow
{
    public SelectionModifySheet(IEditorSession session, CommandID command)
        : base((WindowsCommands.Registry.Descriptor(command)?.Title ?? "").Replace("…", ""), 320)
    {
        var title = (WindowsCommands.Registry.Descriptor(command)?.Title ?? "").Replace("…", "");
        Body.Children.Add(Headline(title));
        var radius = NumberField(10, "Radius", 70);
        Body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                new TextBlock { Text = Loc.T(command == CommandID.FeatherSelection ? "Feather radius" : "By"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                radius,
                new TextBlock { Text = Loc.T("pixels"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) },
            },
        });
        Body.Children.Add(Buttons("OK", () =>
        {
            if (double.TryParse(radius.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var r)) session.ModifySelection(command, r);
            Close();
        }));
        Loaded += (_, _) => { radius.Focus(); radius.SelectAll(); };
    }
}

/// <summary>First-launch introduction. Also under Help › Welcome to Glimmer.</summary>
public sealed class WelcomeView : SheetWindow
{
    private static readonly (string Symbol, string Title, string Text)[] Features =
    [
        ("square.3.layers.3d", "A real layer editor", "Layers, groups, masks, adjustment layers, layer styles, smart objects and text — non-destructive, in 16-bit linear color."),
        ("paintbrush.pointed", "Paint and retouch", "Brushes with pressure, clone and healing, dodge and burn, liquify and 14 filters that respect your selection."),
        ("sparkles", "AI with your own key", "Generate, fill, expand, relight and more through OpenRouter. Subject selection, depth blur and background removal run on this Mac."),
        ("sparkle.magnifyingglass", "Just ask", "Press ⌘K to find any command, or describe an edit and the assistant plans it with the editor's own tools."),
    ];

    public WelcomeView() : base("Welcome to Glimmer", 560)
    {
        Body.Margin = new Thickness(32);
        var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 22) };
        head.Children.Add(new Icon { Symbol = "sparkle", Size = 72, Foreground = (Brush)FindResource("Glimmer.AccentBrush") });
        var title = new TextBlock { Text = Loc.T("Welcome to Glimmer"), FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        head.Children.Add(title);
        head.Children.Add(new TextBlock { Text = Loc.T("A photo editor for the Mac, with AI where it helps."), Foreground = (Brush)FindResource("Glimmer.MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        Body.Children.Add(head);
        foreach (var (symbol, featureTitle, text) in Features)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var icon = new Icon { Symbol = symbol, Size = 22, Width = 34, Foreground = (Brush)FindResource("Glimmer.AccentBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = Loc.T(featureTitle), Style = (Style)FindResource("Glimmer.Text.Headline") });
            words.Children.Add(new TextBlock { Text = Loc.T(text), Foreground = (Brush)FindResource("Glimmer.MutedBrush"), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(words);
            // One stop per feature: its title, then what it does.
            AutomationProperties.SetName(row, Loc.T(featureTitle) + ". " + Loc.T(text));
            Body.Children.Add(row);
        }
        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var key = new Button { Content = Loc.T("Add OpenRouter Key…"), Style = (Style)FindResource("Glimmer.Compact.Button") };
        key.Click += (_, _) => { AppServices.Settings.Set(AppPreferences.SettingsTabKey, "ai"); AppServices.Documents?.OpenSettings("ai"); };
        var open = new Button { Content = Loc.T("Open Image…"), Style = (Style)FindResource("Glimmer.Compact.Button"), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => { Close(); AppServices.Opener?.ShowOpenPanel(); };
        var create = new Button { Content = Loc.T("New Document"), IsDefault = true, Style = (Style)FindResource("Glimmer.Compact.AccentButton") };
        create.Click += (_, _) => { Close(); AppServices.Documents?.ShowNewDocumentWindow(); };
        DockPanel.SetDock(key, Dock.Left);
        DockPanel.SetDock(create, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        buttons.Children.Add(key);
        buttons.Children.Add(create);
        buttons.Children.Add(open);
        Body.Children.Add(buttons);
        // Shown once at launch (DocumentOpener.showWelcomeWindow): marked as seen as soon as it opens.
        Loaded += (_, _) => AppServices.Settings.Set(AppPreferences.WelcomeShownKey, true);
    }
}
