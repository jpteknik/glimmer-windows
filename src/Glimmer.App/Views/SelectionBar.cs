// Ported from App/SelectionBar.swift. A small bar that follows the selection: describe what to put there, remove
// what's in it, or adjust the selection, without a trip to the menus.
using System.ComponentModel;
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

public sealed class SelectionBar : UserControl
{
    private const double BarWidth = 430, BarHeight = 40;
    private IEditorSession? session;
    // Selection.Bounds walks every tile, and Place runs on every change of the session and the view: computed once per
    // selection and canvas.
    private (Selection? Selection, PixelRect Limit) boundsKey;
    private PixelRect? bounds;
    private readonly PanelSurface bar;
    private readonly TextBox prompt;
    private readonly Button generate;
    private readonly StackPanel actions;

    public SelectionBar()
    {
        IsHitTestVisible = true;
        prompt = new TextBox { Style = (Style)Application.Current.FindResource("Glimmer.PlainTextBox"), Tag = Loc.T("Describe what to add here…"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(prompt, Loc.T("Generative Fill prompt"));
        prompt.TextChanged += (_, _) => UpdateParts();
        prompt.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Generate(); e.Handled = true; } };
        generate = new Button { Content = Loc.T("Generate"), Style = (Style)Application.Current.FindResource("Glimmer.Compact.AccentButton"), Visibility = Visibility.Collapsed };
        generate.Click += (_, _) => Generate();
        actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(IconAction("eraser", "Remove", "Remove what's inside the selection", () => session?.AI.Fill(AIPipelines.InpaintMode.Remove, "", null)));
#if GLIMMER_HEAL
        actions.Children.Add(IconAction("bandage", "Heal", "Fill the selection from what surrounds it (on this Mac, free)", () => session?.Perform(CommandID.HealSelection)));
#endif
        actions.Children.Add(new Border { Width = 1, Height = 18, Background = (Brush)Application.Current.FindResource("Glimmer.EdgeBrush"), Margin = new Thickness(4, 0, 4, 0) });
        actions.Children.Add(IconAction("circle.righthalf.filled", "Invert", "Invert the selection", () => session?.Perform(CommandID.InvertSelection)));
        actions.Children.Add(IconAction("circle.dotted", "Feather", "Feather the selection", () => session?.Perform(CommandID.FeatherSelection)));
        actions.Children.Add(IconAction("xmark", "Deselect", "Deselect (⌘D)", () => session?.Perform(CommandID.Deselect)));
        var row = new DockPanel { Margin = new Thickness(14, 0, 14, 0) };
        var sparkles = new Icon { Symbol = "sparkles", Size = 13, Foreground = (Brush)Application.Current.FindResource("Glimmer.MutedBrush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(sparkles, Dock.Left);
        DockPanel.SetDock(generate, Dock.Right);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(sparkles);
        row.Children.Add(generate);
        row.Children.Add(actions);
        row.Children.Add(prompt);
        bar = new PanelSurface { Content = row, Width = BarWidth, Height = BarHeight, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Content = new Canvas { Children = { bar } };
        DataContextChanged += (_, e) =>
        {
            if (session is not null) { session.PropertyChanged -= OnChanged; session.Canvas.PropertyChanged -= OnChanged; }
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) { session.PropertyChanged += OnChanged; session.Canvas.PropertyChanged += OnChanged; }
            Place();
        };
        SizeChanged += (_, _) => Place();
    }

    private Button IconAction(string symbol, string name, string help, Action click)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = symbol, Size = 13 }, ToolTip = Loc.T(help) };
        AutomationProperties.SetName(b, Loc.T(name));
        b.Click += (_, _) => click();
        return b;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Place();

    private void UpdateParts()
    {
        bool typed = prompt.Text.Trim().Length > 0;
        generate.Visibility = typed ? Visibility.Visible : Visibility.Collapsed;
        actions.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        bool key = session?.AI.HasAPIKey ?? false;
        prompt.IsEnabled = key;
        ((Button)actions.Children[0]).IsEnabled = key;
        ((Button)actions.Children[0]).ToolTip = Loc.T(key ? "Remove what's inside the selection" : "Add your OpenRouter key in Settings › AI");
    }

    private PixelRect? SelectionBounds(IEditorSession s)
    {
        var key = (s.State.Selection, s.State.Bounds);
        if (!ReferenceEquals(key.Selection, boundsKey.Selection) || key.Bounds != boundsKey.Limit)
        {
            boundsKey = key;
            bounds = key.Selection?.Bounds(key.Bounds);
        }
        return bounds;
    }

    /// <summary>Below the selection when there's room, otherwise above, otherwise inside its bottom edge.</summary>
    private void Place()
    {
        UpdateParts();
        if (session is not { } s || IsHidden(s) || SelectionBounds(s) is not { } b)
        {
            bar.Visibility = Visibility.Collapsed;
            return;
        }
        var t = s.Canvas.Transform;
        var corners = new[] { new Vec2(b.X, b.Y), new Vec2(b.MaxX, b.Y), new Vec2(b.X, b.MaxY), new Vec2(b.MaxX, b.MaxY) }.Select(t.ViewPoint).ToArray();
        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X), minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);
        double viewW = ActualWidth, viewH = ActualHeight;
        double below = maxY + 12 + BarHeight / 2, above = minY - 12 - BarHeight / 2;
        double y = below + BarHeight / 2 < viewH - 56 ? below : (above - BarHeight / 2 > 56 ? above : viewH - 56 - BarHeight);
        double x = Math.Min(Math.Max((minX + maxX) / 2, BarWidth / 2 + 12), Math.Max(viewW - BarWidth / 2 - 12, BarWidth / 2 + 12));
        Canvas.SetLeft(bar, x - BarWidth / 2);
        Canvas.SetTop(bar, y - BarHeight / 2);
        bar.Visibility = Visibility.Visible;
    }

    private static bool IsHidden(IEditorSession s) =>
        s.TransformSession is not null || s.CropSession is not null || s.Liquify is not null || s.FilterDialog is not null || s.IsPaletteOpen || s.EditingText is not null;

    private void Generate()
    {
        var text = prompt.Text.Trim();
        if (text.Length == 0 || session is null) return;
        session.AI.Fill(AIPipelines.InpaintMode.Fill, text, null);
        prompt.Text = "";
        Keyboard.ClearFocus();
    }
}
