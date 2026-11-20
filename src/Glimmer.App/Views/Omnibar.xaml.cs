// Ported from App/CommandPalette.swift (Omnibar).
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public partial class Omnibar : UserControl
{
    private static readonly string[] GenerateSuggestions = ["a sunset over mountains", "a cute robot mascot", "a watercolor forest cabin", "a neon city street at night"];
    private static readonly string[] EditSuggestions = ["make it warmer", "add curves", "relight", "remove background", "vintage look"];

    private IEditorSession? session;

    public Omnibar()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (session is not null) session.OmnibarFocusRequested -= OnFocusRequested;
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) session.OmnibarFocusRequested += OnFocusRequested;
        };
    }

    private void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => Query.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private bool Focused => Query.IsKeyboardFocused;

    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Focused) session?.AI.RefreshInsights();
        Update();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Update();

    private void Update()
    {
        if (session is not { } s) return;
        var rows = Focused ? Palette.Rows(Query.Text, s, limit: 5, recentsWhenEmpty: false) : [];
        Rows.ItemsSource = rows;
        Rows.SelectedIndex = rows.Count > 0 ? 0 : -1;
        RowsSurface.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool showChips = Focused && Query.Text.Length == 0;
        Suggestions.Visibility = showChips ? Visibility.Visible : Visibility.Collapsed;
        if (showChips) BuildChips(s);
    }

    private void BuildChips(IEditorSession s)
    {
        Chips.Children.Clear();
        Chips.Children.Add(new TextBlock { Text = Loc.T("Try"), Foreground = (Brush)FindResource("Glimmer.MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bool blank = s.IsUntouchedBlank;
        var smart = blank ? [] : s.AI.SmartSuggestions;
        // On-device analysis of this image first (instant and free), then the general ideas.
        foreach (var id in smart)
        {
            var chip = Chip(SmartTitle(s, id), accent: true, "Suggested for this image (analyzed on this Mac)");
            chip.Click += (_, _) => { Finish(); Palette.RecordUse(id.RawValue); s.Perform(id); };
            Chips.Children.Add(chip);
        }
        foreach (var suggestion in (blank ? GenerateSuggestions : EditSuggestions).Take(Math.Max(2, 5 - smart.Count)))
        {
            var chip = Chip(Loc.T(suggestion), accent: false, null);
            chip.Click += (_, _) => { Finish(); Palette.Ask(blank ? "generate " + Loc.T(suggestion) : Loc.T(suggestion), s); };
            Chips.Children.Add(chip);
        }
    }

    private Button Chip(string text, bool accent, string? help)
    {
        var b = new Button
        {
            Style = (Style)FindResource("Glimmer.IconButton"), Padding = new Thickness(10, 0, 10, 0), Height = 22, Margin = new Thickness(0, 0, 6, 0),
            Foreground = (Brush)FindResource(accent ? "Glimmer.AccentBrush" : "Glimmer.InkBrush"), ToolTip = help is null ? null : Loc.T(help),
            Content = new TextBlock { Text = text },
            BorderBrush = (Brush)FindResource(accent ? "Glimmer.AccentBrush" : "Glimmer.EdgeBrush"),
        };
        AutomationProperties.SetName(b, text);
        return b;
    }

    private static string SmartTitle(IEditorSession s, CommandID id)
    {
        if (id == CommandID.AutoStraighten && s.AI.HorizonDegrees is { } tilt)
            return Loc.F("Straighten {0}°", Math.Abs(tilt).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
        return Loc.T(WindowsCommands.Registry.Descriptor(id)?.Title ?? id.RawValue);
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (session is not { } s) return;
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
            {
                var text = Query.Text.Trim();
                if (text.Length > 0) { Finish(); Palette.Ask(text, s); }
                break;
            }
            case Key.Enter:
                if (Rows.SelectedItem is PaletteRow row) { Finish(); Palette.Run(row, s); }
                break;
            case Key.Down: Rows.SelectedIndex = Math.Min(Rows.SelectedIndex + 1, Math.Max(Rows.Items.Count - 1, 0)); break;
            case Key.Up: Rows.SelectedIndex = Math.Max(Rows.SelectedIndex - 1, 0); break;
            case Key.Escape: Finish(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteRow row && session is { } s) { Finish(); Palette.Run(row, s); }
    }

    /// <summary>Clears the field and hands the keyboard back to the canvas.</summary>
    private void Finish()
    {
        Query.Text = "";
        Keyboard.ClearFocus();
        FindAncestor<DocumentView>(this)?.FocusCanvas();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
