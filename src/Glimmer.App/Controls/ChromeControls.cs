// Ported from App/Theme.swift (panelSurface, ChromeChip) and App/StatusLine.swift (StatusLine, StatusSymbol).
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Glimmer.App.Localization;
using Glimmer.Core;

namespace Glimmer.App.Controls;

/// <summary>A floating chrome surface: solid panel colour at 96 %, hairline border, soft shadow (Swift
/// panelSurface(cornerRadius:)). The shadow is drawn by a sibling behind the content, so text inside keeps ClearType.
/// Its template is in Themes/Glimmer.xaml.</summary>
public class PanelSurface : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner(
        typeof(PanelSurface), new FrameworkPropertyMetadata(new CornerRadius(12)));

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
}

/// <summary>The small labelled buttons along the top of the canvas (AI, Tools, Layers, Export). IsChecked shows
/// whether its panel is open; clicking never flips it by itself (the click runs the action, the action sets the
/// state), so it reads to a screen reader as a toggle with the true state.</summary>
public sealed class ChromeChip : ToggleButton
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(string), typeof(ChromeChip));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ChromeChip), new PropertyMetadata("", (d, e) => ((ChromeChip)d).UpdateName()));
    public static readonly DependencyProperty ShowsTitleProperty = DependencyProperty.Register(nameof(ShowsTitle), typeof(bool), typeof(ChromeChip), new PropertyMetadata(true));

    public string? Symbol { get => (string?)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    /// <summary>An English key; shown (and named for screen readers) through Loc.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowsTitle { get => (bool)GetValue(ShowsTitleProperty); set => SetValue(ShowsTitleProperty, value); }

    public string ShownTitle => Loc.T(Title);

    private void UpdateName() => AutomationProperties.SetName(this, Loc.T(Title));

    protected override void OnToggle() { }
}

/// <summary>A state said in the window: the symbol carries the kind and its colour, the text stays in the label
/// colour, and a screen reader hears the kind as a word first ("Error: …"). Every state in a view goes through this
/// control or StatusSymbol: no view picks a red, an orange or a triangle for itself.</summary>
public sealed class StatusLine : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusLine), new PropertyMetadata(StatusKind.Notice, (d, _) => ((StatusLine)d).Update()));
    /// <summary>A catalogue key (Swift: StatusLine(kind, "…")).</summary>
    public static readonly DependencyProperty TextKeyProperty = DependencyProperty.Register(
        nameof(TextKey), typeof(string), typeof(StatusLine), new PropertyMetadata(null, (d, _) => ((StatusLine)d).Update()));
    /// <summary>A text already in the person's language (Swift: StatusLine(kind, verbatim:)).</summary>
    public static readonly DependencyProperty VerbatimProperty = DependencyProperty.Register(
        nameof(Verbatim), typeof(string), typeof(StatusLine), new PropertyMetadata(null, (d, _) => ((StatusLine)d).Update()));
    private static readonly DependencyPropertyKey ShownTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownText), typeof(string), typeof(StatusLine), new PropertyMetadata(""));
    public static readonly DependencyProperty ShownTextProperty = ShownTextKey.DependencyProperty;

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string? TextKey { get => (string?)GetValue(TextKeyProperty); set => SetValue(TextKeyProperty, value); }
    public string? Verbatim { get => (string?)GetValue(VerbatimProperty); set => SetValue(VerbatimProperty, value); }
    public string ShownText => (string)GetValue(ShownTextProperty);

    private void Update()
    {
        var text = Verbatim is { } v ? Loc.Verbatim(v) : TextKey is { } k ? Loc.T(k) : "";
        SetValue(ShownTextKey, text);
        // "Error: <text>" for a screen reader; a notice and work under way are said as they are.
        AutomationProperties.SetName(this, Kind.Prefix() is { } prefix ? Loc.T(prefix) + " " + text : text);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusLineAutomationPeer(this);

    private sealed class StatusLineAutomationPeer(StatusLine owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override bool IsControlElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>The symbol of a state alone, in its colour (a row's status in a list). Work under way is a spinner.</summary>
public sealed class StatusSymbol : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusSymbol), new PropertyMetadata(StatusKind.Notice));

    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
}

/// <summary>Work under way (SwiftUI ProgressView().controlSize(.small)): an arc that turns. It stands still when
/// Windows' animations are turned off.</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Spinner), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(
        typeof(Spinner), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly RotateTransform turn = new();

    public Spinner()
    {
        RenderTransform = turn;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Loaded += (_, _) =>
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            turn.BeginAnimation(RotateTransform.AngleProperty, spin);
        };
        Unloaded += (_, _) => turn.BeginAnimation(RotateTransform.AngleProperty, null);
        AutomationProperties.SetName(this, Loc.T("Running"));
    }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        double r = Size / 2 - 1.5;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        var faint = new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushOpacity(0.25);
        dc.DrawEllipse(null, faint, c, r, r);
        dc.Pop();
        dc.DrawGeometry(null, faint, arc);
    }
}

/// <summary>Colours of the states, for the templates.</summary>
public static class StatusBrushes
{
    public static Brush For(StatusKind kind)
    {
        // Glimmer's windows are dark (App.xaml: ThemeMode="Dark").
        var (r, g, b) = kind.Color(onDark: true);
        return Theme.Frozen(Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255)));
    }
}
