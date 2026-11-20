// SwiftUI's ColorPicker for Windows (PropertiesPanel.swift ColorWell, DocumentView.swift ColorSwatches): a well
// showing the colour that opens a picker beside it. WPF has no colour panel of its own, so the picker is Glimmer's:
// a saturation/brightness square, a hue strip, an opacity strip when the well supports it, and a hex field
// (the web app's HSV square, which the Mac pairs with the system colour panel).
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Localization;
using Glimmer.Core;

namespace Glimmer.App.Controls;

public sealed class ColorWell : Button
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(SRGBColor), typeof(ColorWell),
        new FrameworkPropertyMetadata(SRGBColor.Black, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorWell)d).InvalidateVisual()));
    public static readonly DependencyProperty AlphaProperty = DependencyProperty.Register(
        nameof(Alpha), typeof(double), typeof(ColorWell),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorWell)d).InvalidateVisual()));
    public static readonly DependencyProperty SupportsOpacityProperty = DependencyProperty.Register(nameof(SupportsOpacity), typeof(bool), typeof(ColorWell));
    /// <summary>An English key: what the well is ("Foreground", "Fill color"); its name for screen readers.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorWell), new PropertyMetadata("", (d, e) => AutomationProperties.SetName(d, Loc.T((string)e.NewValue))));

    /// <summary>Raised when the picker closes after a change (one undo step per pick, as a slider's release).</summary>
    public event EventHandler? Committed;

    public SRGBColor Color { get => (SRGBColor)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Alpha { get => (double)GetValue(AlphaProperty); set => SetValue(AlphaProperty, value); }
    public bool SupportsOpacity { get => (bool)GetValue(SupportsOpacityProperty); set => SetValue(SupportsOpacityProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public ColorWell()
    {
        Width = 40;
        Height = 24;
        Cursor = Cursors.Hand;
        FocusVisualStyle = (Style)Application.Current.TryFindResource("Glimmer.FocusVisual");
        Template = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
        var clip = new RectangleGeometry(r, 5, 5);
        dc.PushClip(clip);
        if (Alpha < 1) Checkerboard.Draw(dc, r, 5);
        dc.DrawRectangle(new SolidColorBrush(SRGBBrushConverter.ToColor(Color, Alpha)), null, r);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Theme.WithAlpha(Colors.White, 0.25)), 1), r, 5, 5);
        if (IsKeyboardFocused) dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Theme.Accent), 2), new Rect(-1.5, -1.5, ActualWidth + 3, ActualHeight + 3), 6, 6);
    }

    protected override void OnClick()
    {
        base.OnClick();
        var picker = new ColorPicker { Color = Color, Alpha = Alpha, SupportsOpacity = SupportsOpacity };
        var before = (Color, Alpha);
        picker.Changed += (_, _) => { Color = picker.Color; Alpha = picker.Alpha; };
        var popup = new Popup
        {
            Child = new PanelSurface { Content = picker, Padding = new Thickness(12), CornerRadius = new CornerRadius(10) },
            PlacementTarget = this, Placement = PlacementMode.Right, HorizontalOffset = 8, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None,
        };
        popup.Closed += (_, _) => { if ((Color, Alpha) != before) Committed?.Invoke(this, EventArgs.Empty); Focus(); };
        popup.IsOpen = true;
        picker.Focus();
    }
}

/// <summary>Saturation/brightness square, hue and opacity strips, hex field.</summary>
public sealed class ColorPicker : StackPanel
{
    private double hue, saturation, brightness;
    private readonly FrameworkElement square, hueStrip;
    private readonly Slider alphaSlider;
    private readonly TextBox hex;
    private bool updating;

    public event EventHandler? Changed;

    public SRGBColor Color
    {
        get => FromHsv(hue, saturation, brightness);
        set { (hue, saturation, brightness) = ToHsv(value); Refresh(); }
    }

    public double Alpha
    {
        get => alphaSlider.Value;
        set { alphaSlider.Value = value; }
    }

    public bool SupportsOpacity { get => alphaSlider.Visibility == Visibility.Visible; set => alphaSlider.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public ColorPicker()
    {
        Width = 216;
        Focusable = true;
        square = new SquareSurface(this) { Height = 150, Margin = new Thickness(0, 0, 0, 8) };
        hueStrip = new HueSurface(this) { Height = 14, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(square, Loc.T("Saturation and brightness"));
        AutomationProperties.SetName(hueStrip, Loc.T("Hue"));
        alphaSlider = new Slider { Minimum = 0, Maximum = 1, Value = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(alphaSlider, Loc.T("Opacity"));
        alphaSlider.ValueChanged += (_, _) => { if (!updating) Changed?.Invoke(this, EventArgs.Empty); };
        hex = new TextBox { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Text = "#000000" };
        AutomationProperties.SetName(hex, Loc.T("Hex color"));
        hex.LostKeyboardFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        Children.Add(square);
        Children.Add(hueStrip);
        Children.Add(alphaSlider);
        Children.Add(hex);
    }

    internal double Hue => hue;
    internal double Saturation => saturation;
    internal double Value => brightness;

    internal void SetSV(double s, double v) { saturation = Math.Clamp(s, 0, 1); brightness = Math.Clamp(v, 0, 1); Refresh(); Changed?.Invoke(this, EventArgs.Empty); }
    internal void SetHue(double h) { hue = (h % 360 + 360) % 360; Refresh(); Changed?.Invoke(this, EventArgs.Empty); }

    private void Refresh()
    {
        updating = true;
        var c = FromHsv(hue, saturation, brightness);
        hex.Text = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", B(c.R), B(c.G), B(c.B));
        square.InvalidateVisual();
        hueStrip.InvalidateVisual();
        updating = false;
    }

    private void ApplyHex()
    {
        var t = hex.Text.Trim().TrimStart('#');
        if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            Color = new SRGBColor(((rgb >> 16) & 255) / 255.0, ((rgb >> 8) & 255) / 255.0, (rgb & 255) / 255.0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Refresh();
        }
    }

    private static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    public static SRGBColor FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return new SRGBColor(r + m, g + m, b + m);
    }

    public static (double H, double S, double V) ToHsv(SRGBColor c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), d = max - min;
        double h = d == 0 ? 0 : max == c.R ? 60 * ((c.G - c.B) / d % 6) : max == c.G ? 60 * ((c.B - c.R) / d + 2) : 60 * ((c.R - c.G) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    /// <summary>The saturation/brightness square: drag or arrow keys (Shift for bigger steps).</summary>
    private sealed class SquareSurface : FrameworkElement
    {
        private readonly ColorPicker owner;
        public SquareSurface(ColorPicker owner) { this.owner = owner; Focusable = true; Cursor = Cursors.Cross; }

        protected override void OnRender(DrawingContext dc)
        {
            var r = new Rect(RenderSize);
            var pure = FromHsv(owner.Hue, 1, 1);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.White, SRGBBrushConverter.ToColor(pure), 0), null, r, 4, 4);
            dc.DrawRoundedRectangle(new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), Colors.Black, 90), null, r, 4, 4);
            var p = new Point(owner.Saturation * r.Width, (1 - owner.Value) * r.Height);
            dc.DrawEllipse(null, new Pen(Brushes.Black, 3), p, 6, 6);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.5), p, 6, 6);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); Pick(e.GetPosition(this)); }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Pick(e.GetPosition(this)); }
        protected override void OnMouseUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
        private void Pick(Point p) => owner.SetSV(p.X / ActualWidth, 1 - p.Y / ActualHeight);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
            switch (e.Key)
            {
                case Key.Left: owner.SetSV(owner.Saturation - step, owner.Value); break;
                case Key.Right: owner.SetSV(owner.Saturation + step, owner.Value); break;
                case Key.Up: owner.SetSV(owner.Saturation, owner.Value + step); break;
                case Key.Down: owner.SetSV(owner.Saturation, owner.Value - step); break;
                default: return;
            }
            e.Handled = true;
        }
    }

    private sealed class HueSurface : FrameworkElement
    {
        private readonly ColorPicker owner;
        public HueSurface(ColorPicker owner) { this.owner = owner; Focusable = true; Cursor = Cursors.Hand; }

        protected override void OnRender(DrawingContext dc)
        {
            var stops = new GradientStopCollection(Enumerable.Range(0, 7).Select(i => new System.Windows.Media.GradientStop(SRGBBrushConverter.ToColor(FromHsv(i * 60 % 360, 1, 1)), i / 6.0)));
            dc.DrawRoundedRectangle(new LinearGradientBrush(stops, 0), null, new Rect(RenderSize), 4, 4);
            double x = owner.Hue / 360 * ActualWidth;
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1), new Rect(x - 2, -1, 4, ActualHeight + 2));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { Focus(); CaptureMouse(); owner.SetHue(e.GetPosition(this).X / ActualWidth * 360); }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) owner.SetHue(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 0.9999) * 360); }
        protected override void OnMouseUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            if (e.Key == Key.Left) owner.SetHue(owner.Hue - step);
            else if (e.Key == Key.Right) owner.SetHue(owner.Hue + step);
            else return;
            e.Handled = true;
        }
    }
}

/// <summary>Grey/white checkerboard behind transparent previews (NavigatorView.swift CheckerboardView, LayersPanel.swift
/// Checkerboard).</summary>
public static class Checkerboard
{
    private static readonly Brush Light = Brushes.White;
    private static readonly Brush Dark = Theme.Frozen(Color.FromRgb(0xd9, 0xd9, 0xd9));

    public static void Draw(DrawingContext dc, Rect r, double cell)
    {
        dc.DrawRectangle(Light, null, r);
        for (int row = 0; row * cell < r.Height; row++)
            for (int col = row % 2; col * cell < r.Width; col += 2)
                dc.DrawRectangle(Dark, null, new Rect(r.X + col * cell, r.Y + row * cell, Math.Min(cell, r.Width - col * cell), Math.Min(cell, r.Height - row * cell)));
    }

    /// <summary>A tiled brush of the same pattern, for backgrounds.</summary>
    public static DrawingBrush Brush(double cell)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Light, null, new Rect(0, 0, 2 * cell, 2 * cell));
            dc.DrawRectangle(Dark, null, new Rect(cell, 0, cell, cell));
            dc.DrawRectangle(Dark, null, new Rect(0, cell, cell, cell));
        }
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 2 * cell, 2 * cell), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
