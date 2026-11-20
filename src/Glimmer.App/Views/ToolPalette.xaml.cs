// Ported from App/DocumentView.swift (ToolPalette, ColorSwatches, SwatchesView).
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public partial class ToolPalette : UserControl
{
    public ToolPalette()
    {
        InitializeComponent();
        Tools.ItemsSource = ToolTitles.Available;
        SwatchesButton.Checked += (_, _) => { if (Session is { } s) SwatchesHost.Content = new SwatchesView(s); };
    }

    private IEditorSession? Session => (DataContext as DocumentChrome)?.Session;

    private void OnSwap(object sender, RoutedEventArgs e) => Session?.SwapColors();
    private void OnReset(object sender, RoutedEventArgs e) => Session?.ResetColors();
}

/// <summary>A palette of colour swatches: click sets the foreground, Alt-click the background. Saved swatches persist
/// across documents ("swatches.saved").</summary>
public sealed class SwatchesView : StackPanel
{
    private static readonly SRGBColor[] Defaults =
    [
        new(0, 0, 0), new(0.25, 0.25, 0.25), new(0.5, 0.5, 0.5), new(0.75, 0.75, 0.75), new(1, 1, 1),
        new(0.86, 0.15, 0.15), new(0.96, 0.45, 0.1), new(0.98, 0.8, 0.1), new(0.35, 0.72, 0.25),
        new(0.1, 0.6, 0.55), new(0.15, 0.55, 0.9), new(0.3, 0.3, 0.8), new(0.55, 0.3, 0.75),
        new(0.9, 0.35, 0.6), new(0.55, 0.36, 0.2), new(0.96, 0.87, 0.72), new(0.75, 0.55, 0.45),
        new(0.35, 0.45, 0.3), new(0.2, 0.25, 0.35), new(0.95, 0.6, 0.55),
    ];

    private readonly IEditorSession session;

    public SwatchesView(IEditorSession session)
    {
        this.session = session;
        Width = 236;
        Build();
    }

    private static List<SRGBColor> Saved
    {
        get
        {
            try { return JsonSerializer.Deserialize<List<SRGBColor>>(AppServices.Settings.Get(AppPreferences.SwatchesKey, "[]")) ?? []; }
            catch (JsonException) { return []; }
        }
        set => AppServices.Settings.Set(AppPreferences.SwatchesKey, JsonSerializer.Serialize(value));
    }

    private void Build()
    {
        Children.Clear();
        Children.Add(new TextBlock { Text = Loc.T("Swatches"), Style = (Style)FindResource("Glimmer.Text.Headline"), Margin = new Thickness(0, 0, 0, 10) });
        Children.Add(Grid(Defaults, removable: false));
        var header = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var add = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = "plus", Size = 12 }, ToolTip = Loc.T("Save the foreground color") };
        AutomationProperties.SetName(add, Loc.T("Add Foreground"));
        add.Click += (_, _) =>
        {
            var s = Saved;
            if (!s.Contains(session.Foreground)) s.Add(session.Foreground);
            Saved = s;
            Build();
        };
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(new TextBlock { Text = Loc.T("Saved"), Style = (Style)FindResource("Glimmer.Text.Secondary"), VerticalAlignment = VerticalAlignment.Center });
        Children.Add(header);
        var saved = Saved;
        Children.Add(saved.Count == 0
            ? new TextBlock { Text = Loc.T("Click + to save the foreground color."), Style = (Style)FindResource("Glimmer.Text.Caption") }
            : Grid(saved, removable: true));
        Children.Add(new TextBlock { Text = Loc.T("Click: foreground · ⌥-click: background"), Style = (Style)FindResource("Glimmer.Text.Caption"), Margin = new Thickness(0, 10, 0, 0) });
    }

    private UIElement Grid(IReadOnlyList<SRGBColor> colors, bool removable)
    {
        var panel = new WrapPanel { Width = 230 };
        foreach (var c in colors)
        {
            var hex = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", (int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255));
            // A real button, so the keyboard reaches it; Alt-click sets the background.
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Style = (Style)FindResource("Glimmer.IconButton"), Padding = new Thickness(0),
                Content = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(SRGBBrushConverter.ToColor(c)),
                                       BorderBrush = new SolidColorBrush(Theme.WithAlpha(Colors.White, 0.2)), BorderThickness = new Thickness(1) },
            };
            AutomationProperties.SetName(b, Loc.F("Color {0}", hex));
            AutomationProperties.SetHelpText(b, Loc.T("Sets the foreground color."));
            b.Click += (_, _) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) session.Background = c; else session.Foreground = c;
            };
            var menu = new ContextMenu();
            var asBackground = new MenuItem { Header = Loc.T("Set as Background") };
            asBackground.Click += (_, _) => session.Background = c;
            menu.Items.Add(asBackground);
            if (removable)
            {
                var remove = new MenuItem { Header = Loc.T("Remove") };
                remove.Click += (_, _) => { Saved = Saved.Where(x => x != c).ToList(); Build(); };
                menu.Items.Add(remove);
            }
            b.ContextMenu = menu;
            panel.Children.Add(b);
        }
        return panel;
    }
}
