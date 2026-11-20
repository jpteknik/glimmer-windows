// Ported from App/SettingsView.swift (SettingsView, GeneralSettingsView, AISettingsView, ModelSettingsView,
// PerformanceSettingsView, TabletSettingsView, ProviderTextSettings, OpenRouterTextPrice, ProviderImageSettings,
// LocalTextSettings). The debug-only TextModelTrial is not ported. One window, opened at a pane by
// IDocumentHost.OpenSettings ("general", "ai", "models", "performance", "tablet"), the pane kept in "settings.tab".
// Windows adds one thing to General: making Glimmer the app for .glimmer files (Platform/FileAssociations.cs).
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Glimmer.AI;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.App.Platform;
using Glimmer.App.Views.Sheets;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    public const string TabKey = "settings.tab";
    public static readonly string[] Panes = ["general", "ai", "models", "performance", "tablet"];
    private static SettingsWindow? open;
    private readonly TabControl tabs = new();

    /// <summary>Opens Settings at a pane (or brings the open window forward there). Changes no setting.</summary>
    public static void Show(Window? owner, string pane)
    {
        AppServices.Settings.Set(TabKey, Panes.Contains(pane) ? pane : "general");
        if (open is null)
        {
            open = new SettingsWindow { Owner = owner };
            open.Closed += (_, _) => open = null;
            open.Show();
        }
        open.Select(AppServices.Settings.Get(TabKey, "general"));
        open.Activate();
    }

    private SettingsWindow()
    {
        Title = Loc.T("Settings…").TrimEnd('…', '.');
        Width = 560;
        Height = 680;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Glimmer.PanelBrush");
        Foreground = (Brush)FindResource("Glimmer.InkBrush");
        FontFamily = (FontFamily)FindResource("Glimmer.Font");
        FontSize = 13;
        UseLayoutRounding = true;
        Add("general", "General", "gearshape", General);
        Add("ai", "AI", "sparkles", AI);
        Add("models", "Models", "cpu", Models);
        Add("performance", "Performance", "gauge.with.dots.needle.67percent", Performance);
        Add("tablet", "Tablet", "applepencil.tip", Tablet);
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs || tabs.SelectedItem is not TabItem { Tag: string pane } item) return;
            AppServices.Settings.Set(TabKey, pane);
            // Each pane is built when it is shown, so it reads the settings as they are now.
            if (item.Content is ScrollViewer { Tag: Func<FormPanel> build } scroller) scroller.Content = build();
        };
        Content = tabs;
    }

    private void Add(string pane, string title, string symbol, Func<FormPanel> build)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = symbol, Size = 13, Margin = new Thickness(0, 0, 6, 0) }, new TextBlock { Text = Loc.T(title) } } };
        var item = new TabItem { Header = header, Tag = pane, Content = new ScrollViewer { Tag = build, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        AutomationProperties.SetName(item, Loc.T(title));
        tabs.Items.Add(item);
    }

    private void Select(string pane)
    {
        var item = tabs.Items.OfType<TabItem>().FirstOrDefault(i => (string)i.Tag == pane) ?? (TabItem)tabs.Items[0];
        if (tabs.SelectedItem == item && item.Content is ScrollViewer { Tag: Func<FormPanel> build } s && s.Content is null) s.Content = build();
        tabs.SelectedItem = item;
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private static Style Compact(string kind) => (Style)Application.Current.FindResource("Glimmer.Compact." + kind);

    private static ComboBox Picker<T>(IEnumerable<(T Value, string Title)> choices, T selected, Action<T> changed, string name)
    {
        var box = new ComboBox { Style = Compact("ComboBox"), MinWidth = 170 };
        AutomationProperties.SetName(box, Loc.T(name));
        foreach (var (v, title) in choices) box.Items.Add(new ComboBoxItem { Content = title, Tag = v });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: T v }) changed(v); };
        return box;
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") };

    /// <summary>A catalogue text that carries Markdown links ("[Get a key](https://…)"): the links become hyperlinks.</summary>
    internal static TextBlock WithLinks(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("Glimmer.Text.Secondary") };
        int at = 0;
        foreach (Match m in Regex.Matches(text, @"\[([^\]]+)\]\((https?://[^)]+)\)"))
        {
            block.Inlines.Add(new Run(text[at..m.Index]));
            block.Inlines.Add(ModelDownloadSheet.Link(m.Groups[1].Value, m.Groups[2].Value, null));
            at = m.Index + m.Length;
        }
        block.Inlines.Add(new Run(text[at..]));
        return block;
    }

    private static WrapPanel Links(AIProvider provider)
    {
        var row = new WrapPanel();
        void Add(string title, string? url)
        {
            if (url is null) return;
            var t = new TextBlock { Margin = new Thickness(0, 0, 12, 0) };
            t.Inlines.Add(ModelDownloadSheet.Link(Loc.T(title), url, null));
            row.Children.Add(t);
        }
        Add("Get a key", provider.KeysUrl);
        Add("Terms", provider.TermsUrl);
        Add("Prices", provider.PricesUrl);
        return row;
    }

    private static Button SmallButton(string title, Action click, bool accent = false)
    {
        var b = new Button { Content = Loc.T(title), Style = Compact(accent ? "AccentButton" : "Button"), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------------------------------------- General

    /// <summary>Preferences.CanvasBackground: the canvas's surround.</summary>
    private static readonly (string Raw, string Title)[] Backgrounds =
        [("system", "Match System"), ("light", "Light Gray"), ("mid", "Mid Gray"), ("dark", "Dark Gray"), ("black", "Black")];

    private FormPanel General()
    {
        var s = AppServices.Settings;
        var form = new FormPanel();
        var canvas = form.Section("Canvas");
        canvas.Row("Background", Picker(Backgrounds.Select(b => (b.Raw, Loc.T(b.Title))), s.Get(Preferences.CanvasBackgroundKey, "system"),
                                        v => s.Set(Preferences.CanvasBackgroundKey, v), "Background"));
        canvas.Row("Transparency grid", Picker([(4.0, Loc.T("Small")), (8.0, Loc.T("Medium")), (16.0, Loc.T("Large"))], s.Get(Preferences.CheckerSizeKey, 8.0),
                                               v => s.Set(Preferences.CheckerSizeKey, v), "Transparency grid"));
        canvas.Toggle("Show the search bar on the canvas", s.Get(Preferences.ShowOmnibarKey, true), v => s.Set(Preferences.ShowOmnibarKey, v));
        canvas.Toggle("Show the action bar at the selection", s.Get(AppPreferences.ShowSelectionBarKey, true), v => s.Set(AppPreferences.ShowSelectionBarKey, v));

        var assistant = form.Section("Assistant");
        assistant.Toggle("Run AI steps without asking", s.Get(AIPreferences.AutoRunKey, false), v => s.Set(AIPreferences.AutoRunKey, v),
                         help: "Plans that only use the editor's own tools always run straight away.");
        assistant.Toggle("Let the assistant see the image", s.Get(AIPreferences.ShowImageKey, true), v => s.Set(AIPreferences.ShowImageKey, v));
        assistant.Toggle("Check the result and adjust once", s.Get(AIPreferences.ReviewKey, true), v => s.Set(AIPreferences.ReviewKey, v),
                         help: "A small copy of the image (1024 px) is sent with each request, so settings suit the photo. The check compares before and after.");
        // On the Mac this is Apple Intelligence. Windows has no on-device model for it (OnDeviceRouter.IsAvailable is false
        // until one is plugged in), so the switch shows only when there is one; "Needs Apple Intelligence" would mislead here.
        if (AppServices.AISettings?.OnDeviceRouterAvailable == true)
            assistant.Toggle("Understand simple requests on this Mac", s.Get(AIPreferences.OnDeviceRoutingKey, true), v => s.Set(AIPreferences.OnDeviceRoutingKey, v),
                             help: "Apple Intelligence runs one-command requests directly and sends only the rest to OpenRouter.");

        // Windows: the app for .glimmer files. Registered for this user only; nothing needs an administrator.
        var files = form.Section("Files");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Glimmer.Text.Secondary") };
        var make = SmallButton("Make Glimmer the default for .glimmer files", () => { });
        void Show()
        {
            bool isDefault = FileAssociations.IsDefaultForDocuments;
            state.Text = Loc.T(isDefault
                ? "Glimmer opens .glimmer files, and is in the Open with list for pictures, Photoshop documents and web projects."
                : ".glimmer files don't open in Glimmer yet.");
            make.IsEnabled = !isDefault;
        }
        make.Click += (_, _) =>
        {
            try { FileAssociations.RegisterDocuments(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                AlertWindow.Show(this, "Glimmer", ex.Message, [("OK", AlertRole.Default)]);
            }
            Show();
        };
        Show();
        files.Add(new StackPanel { Children = { make, state } });
        state.Margin = new Thickness(0, 6, 0, 0);
        make.HorizontalAlignment = HorizontalAlignment.Left;
        return form;
    }

    // ------------------------------------------------------------------------------------------------- Models

    private FormPanel Models()
    {
        var form = new FormPanel();
        if (AppServices.Models is not { } library) return form;
        var list = form.Section(footer: "These models run on this PC. They are ONNX versions on Hugging Face, fetched at a fixed version, and Glimmer checks every file’s checksum before it uses them. Nothing is downloaded until you click Download.");
        void Ask(OnDeviceModel model)
        {
            new ModelDownloadSheet([model], library) { Owner = this }.ShowDialog();
            Rebuild();
        }
        void Rebuild()
        {
            list.Clear();
            foreach (var model in OnDeviceModels.AllCases)
            {
                var row = new DockPanel();
                var facts = (StackPanel)ModelDownloadSheet.Facts(model);
                facts.Margin = new Thickness(0);
                var verification = library.Verification(model);
                facts.Children.Add(verification switch
                {
                    ModelVerification.Verified => new TextBlock { Text = Loc.T("Downloaded and checked"), Style = (Style)FindResource("Glimmer.Text.Caption") },
                    ModelVerification.Unverified => new StatusLine { Kind = StatusKind.Warning, TextKey = "Unverified (installed by an earlier version)", FontSize = 11 },
                    _ => new TextBlock { Text = Loc.T("Not downloaded"), Style = (Style)FindResource("Glimmer.Text.Caption") },
                });
                var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                Button Named(string title, string spoken, Action click)
                {
                    var b = SmallButton(title, click);
                    b.Margin = new Thickness(0, 0, 0, 4);
                    AutomationProperties.SetName(b, Loc.F(spoken, Loc.T(model.Title())));
                    return b;
                }
                if (verification == ModelVerification.Unverified) buttons.Children.Add(Named("Download Again…", "Download {0} again", () => Ask(model)));
                if (verification != ModelVerification.NotInstalled)
                    buttons.Children.Add(Named("Remove", "Remove {0}", () => { library.Remove(model); Rebuild(); }));
                else
                    buttons.Children.Add(Named("Download…", "Download {0}", () => Ask(model)));
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                row.Children.Add(facts);
                list.Add(row);
            }
        }
        Rebuild();
        var source = form.Section(footer: "Leave empty for Hugging Face. A mirror must serve the same paths (<repository>/resolve/<commit>/<file>). Every file is checked against the same checksums, wherever it comes from.");
        var field = new TextBox { Text = library.Source == "https://huggingface.co" ? "" : library.Source, Width = 260, Style = Compact("TextBox"), Tag = "https://huggingface.co" };
        field.LostKeyboardFocus += (_, _) => library.Source = field.Text.Trim().Length == 0 ? "https://huggingface.co" : field.Text.Trim();
        source.Row("Download from", field);
        return form;
    }

    // ------------------------------------------------------------------------------------------------- Performance

    private FormPanel Performance()
    {
        var s = AppServices.Settings;
        var form = new FormPanel();
        var history = form.Section("Undo History", "Undo steps share unchanged pixels, so each step only costs the tiles it changed. When the history needs more memory than this, the oldest steps move to disk and load back when you undo that far.");
        int limit = s.Get(Preferences.HistoryLimitKey, 100);
        var value = new TextBlock { Text = limit.ToString(CultureInfo.CurrentCulture), Width = 40, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Button Step(string glyph, int delta, string name)
        {
            var b = new Button { Content = glyph, Width = 28, Style = Compact("Button"), Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) =>
            {
                limit = Math.Clamp(limit + delta, 10, 500);
                s.Set(Preferences.HistoryLimitKey, limit);
                value.Text = limit.ToString(CultureInfo.CurrentCulture);
                AutomationProperties.SetItemStatus(value, value.Text);
            };
            return b;
        }
        history.Row("History states", new StackPanel { Orientation = Orientation.Horizontal, Children = { value, Step("−", -10, "−10"), Step("+", 10, "+10") } });
        // countStyle .memory: binary units, as Windows shows memory too.
        static string Memory(ulong bytes) => bytes >= 1UL << 30
            ? (bytes / (double)(1UL << 30)).ToString("0.#", CultureInfo.CurrentCulture) + " GB"
            : (bytes / (double)(1UL << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";
        history.Row("History memory", Picker(new[] { 512, 1024, 2048, 4096, 8192 }.Select(mb => (mb, Memory((ulong)mb << 20))), s.Get(Preferences.HistoryBudgetKey, 2048),
                                             v => s.Set(Preferences.HistoryBudgetKey, v), "History memory"));
        var graphics = form.Section("Graphics");
        var adapter = GraphicsInfo.Adapter();
        graphics.Row("GPU", new TextBlock { Text = adapter?.Name ?? "—" });
        graphics.Row("Memory", new TextBlock { Text = Memory(GraphicsInfo.PhysicalMemory()) });
        if (adapter is { VideoMemory: > 0 } a) graphics.Row("Video memory", new TextBlock { Text = Memory(a.VideoMemory) });
        return form;
    }

    // ------------------------------------------------------------------------------------------------- Tablet

    private FormPanel Tablet()
    {
        var s = AppServices.Settings;
        var form = new FormPanel();
        var pressure = form.Section("Pen Pressure", "Soft reaches full size and flow with a lighter touch; firm needs more force.");
        double exponent = s.Get(TabletSettings.PressureCurveKey, 1.0);
        // The slider in words: soft, even or firm, and how far.
        string Description()
        {
            double x = Math.Log2(exponent) / 1.3;
            if (Math.Abs(x) < 0.05) return Loc.T("Even");
            int amount = (int)Math.Round(Math.Abs(x) * 100);
            return x < 0 ? Loc.F("Soft, {0} %", amount) : Loc.F("Firm, {0} %", amount);
        }
        // Log scale so 1 sits in the middle.
        var slider = new Slider { Minimum = -1.3, Maximum = 1.3, Value = Math.Log2(exponent), Width = 200, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, Loc.T("Pressure"));
        var curve = new Canvas { Width = 120, Height = 120, Margin = new Thickness(0, 6, 0, 6) };
        AutomationProperties.SetName(curve, Loc.T("Pressure curve"));
        var reset = SmallButton("Reset", () => slider.Value = 0);
        void Draw()
        {
            curve.Children.Clear();
            curve.Children.Add(new Rectangle { Width = 120, Height = 120, Stroke = new SolidColorBrush(Theme.WithAlpha(((SolidColorBrush)FindResource("Glimmer.MutedBrush")).Color, 0.4)), StrokeThickness = 1 });
            var line = new Polyline { Stroke = (Brush)FindResource("Glimmer.AccentBrush"), StrokeThickness = 2 };
            for (int i = 0; i <= 40; i++)
            {
                double x = i / 40.0;
                line.Points.Add(new Point(x * 120, 120 - TabletSettings.Curve(x, exponent) * 120));
            }
            curve.Children.Add(line);
            AutomationProperties.SetItemStatus(slider, Description());
            AutomationProperties.SetItemStatus(curve, Description());
            reset.IsEnabled = Math.Abs(exponent - 1) > 1e-9;
        }
        slider.ValueChanged += (_, e) =>
        {
            exponent = Math.Pow(2, e.NewValue);
            if (Math.Abs(e.NewValue) < 1e-9) exponent = 1;
            s.Set(TabletSettings.PressureCurveKey, exponent);
            Draw();
        };
        var soft = new TextBlock { Text = Loc.T("Soft"), Style = (Style)FindResource("Glimmer.Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var firm = new TextBlock { Text = Loc.T("Firm"), Style = (Style)FindResource("Glimmer.Text.Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        pressure.Row("Pressure", new StackPanel { Orientation = Orientation.Horizontal, Children = { soft, slider, firm } });
        pressure.Add(new Border { Child = curve, HorizontalAlignment = HorizontalAlignment.Center });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        pressure.Add(reset);
        Draw();
        var tilt = form.Section(footer: "Turn on Tilt → Angle, Tilt → Roundness or Rotation → Angle under Dynamics in the brush options to shape dabs with the pen.");
        tilt.Toggle("Use pen tilt", s.Get(TabletSettings.UseTiltKey, true), v => s.Set(TabletSettings.UseTiltKey, v));
        return form;
    }
}
