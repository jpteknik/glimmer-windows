// Ported from App/PropertiesPanel.swift (CurvesEditor, LevelsHistogramView, GradientStopsEditor).
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Localization;
using Glimmer.Core;
using GradientStop = Glimmer.Core.GradientStop;

namespace Glimmer.App.Controls;

internal static class ChannelColors
{
    public static Brush For(CurveChannel c) => c switch
    {
        CurveChannel.Red => Theme.Frozen(Color.FromRgb(0xff, 0x45, 0x3a)),
        CurveChannel.Green => Theme.Frozen(Color.FromRgb(0x30, 0xd1, 0x58)),
        CurveChannel.Blue => Theme.Frozen(Color.FromRgb(0x0a, 0x84, 0xff)),
        _ => Theme.Frozen(Theme.Ink),
    };
}

/// <summary>Drag points to shape the curve; click to add a point; drag a point off the graph to remove it.
/// Keyboard: arrows move the selected point by one level (Shift by ten), Alt+Left/Right pick another.</summary>
public sealed class CurvesEditor : FrameworkElement
{
    private IReadOnlyList<CurvePoint> points = [new(0, 0), new(1, 1)];
    private int? dragging;
    private int selected;

    public event EventHandler<ImmutableArray<CurvePoint>>? Changed;
    public event EventHandler? Committed;

    public CurvesEditor()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
    }

    public CurveChannel Channel { get; set; }
    /// <summary>The other channels' curves, drawn faintly behind this one.</summary>
    public IReadOnlyList<(CurveChannel Channel, IReadOnlyList<CurvePoint> Points)> Others { get; set; } = [];
    /// <summary>Draw the curve the way the adjustment applies it (curves from a Photoshop file run differently).</summary>
    public bool Natural { get; set; }

    public IReadOnlyList<CurvePoint> Points
    {
        get => points;
        set { points = value; InvalidateVisual(); }
    }

    private List<CurvePoint> Sorted => points.OrderBy(p => p.X).ToList();

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var rect = new Rect(size);
        dc.DrawRectangle((Brush)FindResource("Glimmer.BackgroundBrush"), null, rect);
        var faint = new Pen(new SolidColorBrush(Theme.WithAlpha(Theme.Muted, 0.3)), 0.5);
        for (int i = 1; i < 4; i++)
        {
            double x = size.Width * i / 4, y = size.Height * i / 4;
            dc.DrawLine(faint, new Point(x, 0), new Point(x, size.Height));
            dc.DrawLine(faint, new Point(0, y), new Point(size.Width, y));
        }
        dc.DrawLine(faint, new Point(0, size.Height), new Point(size.Width, 0));
        foreach (var (other, otherPoints) in Others)
        {
            var b = ChannelColors.For(other).Clone();
            b.Opacity = 0.35;
            dc.DrawGeometry(null, new Pen(b, 1), Curve(otherPoints, 64, size));
        }
        var tint = ChannelColors.For(Channel);
        dc.DrawGeometry(null, new Pen(tint, 1.5), Curve(points, 128, size));
        var sorted = Sorted;
        for (int i = 0; i < sorted.Count; i++)
        {
            var c = new Point(size.Width * sorted[i].X, size.Height * (1 - sorted[i].Y));
            double r = dragging == i ? 5 : 4;
            dc.DrawEllipse(tint, null, c, r, r);
            if (IsKeyboardFocused && i == selected) dc.DrawEllipse(null, new Pen(new SolidColorBrush(Theme.Accent), 2), c, 8, 8);
        }
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Theme.WithAlpha(Theme.Muted, 0.5)), 1), rect);
    }

    private StreamGeometry Curve(IReadOnlyList<CurvePoint> pts, int steps, Size size)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                var p = new Point(size.Width * t, size.Height * (1 - CurveMath.Evaluate(pts, t, Natural)));
                if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, false);
            }
        g.Freeze();
        return g;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        var p = e.GetPosition(this);
        var pts = Sorted;
        var s = new CurvePoint(p.X / ActualWidth, 1 - p.Y / ActualHeight);
        int near = Enumerable.Range(0, pts.Count).OrderBy(i => Distance(pts[i], s)).First();
        if (Distance(pts[near], s) < 10) dragging = near;
        else
        {
            pts.Add(new CurvePoint(Math.Clamp(s.X, 0, 1), Math.Clamp(s.Y, 0, 1)));
            pts = pts.OrderBy(q => q.X).ToList();
            dragging = pts.FindIndex(q => q.X == Math.Clamp(s.X, 0, 1));
            Emit(pts);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || dragging is not int i) return;
        var p = e.GetPosition(this);
        var pts = Sorted;
        if (i >= pts.Count) return;
        double x = p.X / ActualWidth, y = 1 - p.Y / ActualHeight;
        bool endpoint = i == 0 || i == pts.Count - 1;
        if (!endpoint && (y < -0.15 || y > 1.15 || x < -0.15 || x > 1.15))
        {
            pts.RemoveAt(i);
            dragging = null;
            Emit(pts);
            return;
        }
        double lo = i > 0 ? pts[i - 1].X + 0.01 : 0, hi = i < pts.Count - 1 ? pts[i + 1].X - 0.01 : 1;
        pts[i] = new CurvePoint(Math.Clamp(x, lo, hi), Math.Clamp(y, 0, 1));
        Emit(pts);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        dragging = null;
        InvalidateVisual();
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        switch (key)
        {
            case Key.Left when alt: selected = Math.Max(selected - 1, 0); InvalidateVisual(); break;
            case Key.Right when alt: selected = Math.Min(selected + 1, points.Count - 1); InvalidateVisual(); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, step); break;
            case Key.Down: Nudge(0, -step); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    /// <summary>Moves the selected point by whole levels (0…255), keeping it between its neighbours.</summary>
    private void Nudge(double dx, double dy)
    {
        var pts = Sorted;
        if (selected < 0 || selected >= pts.Count) return;
        int i = selected;
        double lo = i > 0 ? pts[i - 1].X + 0.01 : 0, hi = i < pts.Count - 1 ? pts[i + 1].X - 0.01 : 1;
        bool endpoint = i == 0 || i == pts.Count - 1;
        pts[i] = new CurvePoint(endpoint ? pts[i].X : Math.Clamp(pts[i].X + dx / 255, lo, hi), Math.Clamp(pts[i].Y + dy / 255, 0, 1));
        Emit(pts);
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private void Emit(List<CurvePoint> pts)
    {
        points = pts;
        InvalidateVisual();
        Changed?.Invoke(this, [.. pts]);
    }

    private double Distance(CurvePoint p, CurvePoint q) => Math.Sqrt(Math.Pow((p.X - q.X) * ActualWidth, 2) + Math.Pow((p.Y - q.Y) * ActualHeight, 2));

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>"RGB curve", with the points as its value: "▸ 0 → 0, 255 → 255".</summary>
    private sealed class Peer(CurvesEditor owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetNameCore() => Loc.F("{0} curve", Loc.T(owner.Channel.Title()));
        protected override string GetHelpTextCore() => Loc.T("Arrow keys move the marked point; Option-Left and Option-Right pick another.");
        public override object? GetPattern(PatternInterface p) => p == PatternInterface.Value ? this : base.GetPattern(p);
        public bool IsReadOnly => true;
        public string Value => string.Join(", ", owner.Sorted.Select((p, i) =>
            (i == owner.selected ? "▸ " : "") + $"{(int)Math.Round(p.X * 255)} → {(int)Math.Round(p.Y * 255)}"));
        public void SetValue(string value) => throw new InvalidOperationException();
    }
}

/// <summary>The input histogram with draggable black, gamma and white input handles (the web app's Levels editor).</summary>
public sealed class LevelsHistogram : FrameworkElement
{
    private enum Handle { Black, Gamma, White }
    private Handle? dragging;
    private LevelsChannel levels = new();
    private uint[]? bins;
    private const double HandleHeight = 14;

    public event EventHandler<LevelsChannel>? Changed;
    public event EventHandler? Committed;

    public LevelsHistogram() { Focusable = false; AutomationProperties.SetName(this, Loc.T("Levels histogram")); }

    public CurveChannel Channel { get; set; }
    public LevelsChannel Levels { get => levels; set { levels = value; InvalidateVisual(); UpdateStatus(); } }
    public uint[]? Bins { get => bins; set { bins = value; InvalidateVisual(); } }

    private void UpdateStatus() => AutomationProperties.SetItemStatus(this, Loc.F("Black {0}, gamma {1}, white {2}",
        (int)(levels.InBlack * 255), levels.Gamma.ToString("0.00", CultureInfo.CurrentCulture), (int)(levels.InWhite * 255)));

    /// <summary>Where the gamma handle sits between black and white (web: 0.5^(1/γ)).</summary>
    private double GammaPosition => levels.InBlack + Math.Pow(0.5, 1 / Math.Clamp(levels.Gamma, 0.1, 9.99)) * (levels.InWhite - levels.InBlack);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, graphH = ActualHeight - HandleHeight - 4;
        dc.DrawRoundedRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.Ink, 0.06)), null, new Rect(0, 0, w, graphH), 4, 4);
        if (bins is { Length: > 0 })
        {
            // Clip the tallest spikes (pure black or white) so the rest stays readable.
            var sorted = bins.OrderBy(b => b).ToArray();
            double cap = Math.Max(sorted[Math.Min(sorted.Length - 1, sorted.Length * 99 / 100)], 1) * 1.15;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, graphH), true, true);
                for (int i = 0; i < bins.Length; i++)
                    c.LineTo(new Point(i / 255.0 * w, graphH - Math.Min(bins[i] / cap, 1) * (graphH - 4)), true, false);
                c.LineTo(new Point(w, graphH), true, false);
            }
            var tint = ChannelColors.For(Channel).Clone();
            tint.Opacity = 0.55;
            dc.DrawGeometry(tint, null, g);
        }
        DrawHandle(dc, levels.InBlack, Brushes.Black, w, graphH + 4);
        DrawHandle(dc, GammaPosition, Brushes.Gray, w, graphH + 4);
        DrawHandle(dc, levels.InWhite, Brushes.White, w, graphH + 4);
    }

    private static void DrawHandle(DrawingContext dc, double t, Brush fill, double width, double top)
    {
        double x = t * width, s = HandleHeight;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x, top), true, true);
            c.PolyLineTo([new Point(x + s * 0.55, top + s), new Point(x - s * 0.55, top + s)], true, false);
        }
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Theme.Muted), 0.75), g);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        double startX = e.GetPosition(this).X / ActualWidth;
        (Handle H, double At)[] candidates = [(Handle.Black, levels.InBlack), (Handle.Gamma, GammaPosition), (Handle.White, levels.InWhite)];
        dragging = candidates.OrderBy(c => Math.Abs(c.At - startX)).First().H;
        Drag(startX);
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Drag(e.GetPosition(this).X / ActualWidth); }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        dragging = null;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private void Drag(double x)
    {
        double t = Math.Clamp(x, 0, 1);
        var l = levels;
        switch (dragging)
        {
            case Handle.Black: l = l with { InBlack = Math.Min(t, l.InWhite - 0.01) }; break;
            case Handle.White: l = l with { InWhite = Math.Max(t, l.InBlack + 0.01) }; break;
            case Handle.Gamma:
                double span = Math.Max(l.InWhite - l.InBlack, 0.001);
                double mid = Math.Clamp((t - l.InBlack) / span, 0.001, 0.999);
                l = l with { Gamma = Math.Clamp(Math.Log(0.5) / Math.Log(mid), 0.1, 9.99) };
                break;
        }
        Levels = l;
        Changed?.Invoke(this, l);
    }
}

/// <summary>A gradient's stops: the ramp, then a row per stop (colour, position, remove), and Add Stop.</summary>
public sealed class GradientStopsEditor : StackPanel
{
    private ImmutableArray<GradientStop> stops = [];
    private readonly Border ramp;
    private readonly StackPanel rows = new();
    private bool building;

    public event EventHandler<ImmutableArray<GradientStop>>? Changed;
    public event EventHandler? Committed;

    public GradientStopsEditor()
    {
        ramp = new Border { Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8),
                            BorderBrush = new SolidColorBrush(Theme.WithAlpha(Colors.White, 0.2)) };
        Children.Add(ramp);
        Children.Add(rows);
        var add = new Button { Style = (Style)Application.Current.FindResource("Glimmer.Compact.Button"), HorizontalAlignment = HorizontalAlignment.Left,
                               Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Symbol = "plus", Size = 11, Margin = new Thickness(0, 0, 4, 0) }, new TextBlock { Text = Loc.T("Add Stop") } } } };
        AutomationProperties.SetName(add, Loc.T("Add Stop"));
        add.Click += (_, _) =>
        {
            var mid = Sample(0.5);
            Emit(stops.Add(new GradientStop(0.5, mid.Color, mid.Opacity)));
            Committed?.Invoke(this, EventArgs.Empty);
            Build();
        };
        Children.Add(add);
    }

    public ImmutableArray<GradientStop> Stops
    {
        get => stops;
        set
        {
            bool sameShape = value.Length == stops.Length;
            stops = value;
            UpdateRamp();
            if (!sameShape || rows.Children.Count == 0) Build();
        }
    }

    private void UpdateRamp() => ramp.Background = new LinearGradientBrush(new GradientStopCollection(
        stops.OrderBy(s => s.Position).Select(s => new System.Windows.Media.GradientStop(SRGBBrushConverter.ToColor(s.Color, s.Opacity), s.Position))), 0);

    private void Build()
    {
        building = true;
        rows.Children.Clear();
        for (int index = 0; index < stops.Length; index++)
        {
            int i = index;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var well = new ColorWell { Label = "Color", Color = stops[i].Color, Margin = new Thickness(0, 0, 8, 0) };
            well.Committed += (_, _) => { Emit(stops.SetItem(i, stops[i] with { Color = well.Color })); Committed?.Invoke(this, EventArgs.Empty); };
            DockPanel.SetDock(well, Dock.Left);
            var remove = new Button { Style = (Style)FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = "minus.circle", Size = 13 }, IsEnabled = stops.Length > 2 };
            AutomationProperties.SetName(remove, Loc.T("Remove stop"));
            remove.Click += (_, _) => { Emit(stops.RemoveAt(i)); Committed?.Invoke(this, EventArgs.Empty); Build(); };
            DockPanel.SetDock(remove, Dock.Right);
            var position = new Slider { Minimum = 0, Maximum = 1, Value = stops[i].Position, VerticalAlignment = VerticalAlignment.Center,
                                        Style = (Style)Application.Current.TryFindResource("Glimmer.Compact.Slider") };
            AutomationProperties.SetName(position, Loc.T("Stop position"));
            position.ValueChanged += (_, e) => { if (!building) Emit(stops.SetItem(i, stops[i] with { Position = e.NewValue })); };
            position.PreviewMouseUp += (_, _) => Committed?.Invoke(this, EventArgs.Empty);
            row.Children.Add(well);
            row.Children.Add(remove);
            row.Children.Add(position);
            rows.Children.Add(row);
        }
        building = false;
    }

    private void Emit(ImmutableArray<GradientStop> next)
    {
        stops = next;
        UpdateRamp();
        Changed?.Invoke(this, next);
    }

    /// <summary>GradientStop.sample: the colour and opacity a gradient has at t.</summary>
    private (SRGBColor Color, double Opacity) Sample(double t)
    {
        var s = stops.OrderBy(x => x.Position).ToList();
        if (s.Count == 0) return (SRGBColor.Black, 1);
        if (t <= s[0].Position) return (s[0].Color, s[0].Opacity);
        for (int i = 1; i < s.Count; i++)
            if (t <= s[i].Position)
            {
                double f = (t - s[i - 1].Position) / Math.Max(s[i].Position - s[i - 1].Position, 1e-9);
                var a = s[i - 1].Color; var b = s[i].Color;
                return (new SRGBColor(a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f), s[i - 1].Opacity + (s[i].Opacity - s[i - 1].Opacity) * f);
            }
        return (s[^1].Color, s[^1].Opacity);
    }
}
