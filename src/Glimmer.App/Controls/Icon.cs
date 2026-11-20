// SwiftUI's Image(systemName:) for Windows: draws an icon from the catalogue (Icons/IconCatalog) by the original's
// SF Symbol name, in the inherited foreground colour.
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Glimmer.App.Icons;

namespace Glimmer.App.Controls;

public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The icon's box, in DIPs (SwiftUI's font size for the symbol).</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Stroke width of path icons, in the 16-unit box (1.3 matches Segoe's weight at 16 px).</summary>
    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(
        nameof(Weight), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.3, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        // Decorative: the button around it carries the name.
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
    }

    public string? Symbol { get => (string?)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Weight { get => (double)GetValue(WeightProperty); set => SetValue(WeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var spec = IconCatalog.Resolve(Symbol);
        if (spec is null) return;
        double s = Size;
        var origin = new Point((ActualWidth - s) / 2, (ActualHeight - s) / 2);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        switch (spec.Kind)
        {
            case IconKind.Glyph:
            {
                var text = new FormattedText(spec.Glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                             new Typeface(IconCatalog.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                                             s, Foreground, dpi);
                var at = new Point(origin.X + (s - text.Width) / 2, origin.Y + (s - text.Height) / 2);
                if (spec.Mirror) dc.PushTransform(new ScaleTransform(-1, 1, origin.X + s / 2, 0));
                dc.DrawText(text, at);
                if (spec.Mirror) dc.Pop();
                break;
            }
            case IconKind.Path:
            {
                dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
                dc.PushTransform(new ScaleTransform(s / 16, s / 16));
                if (spec.Fill is { } fill) dc.DrawGeometry(Foreground, null, fill);
                if (spec.Stroke is { } stroke)
                {
                    var pen = new Pen(Foreground, Weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    dc.DrawGeometry(null, pen, stroke);
                }
                dc.Pop();
                dc.Pop();
                break;
            }
            case IconKind.Text:
            {
                var font = TextElement.GetFontFamily(this);
                var text = new FormattedText(spec.Text!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                             new Typeface(font, spec.Italic ? FontStyles.Italic : FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                                             s * 0.92, Foreground, dpi);
                dc.DrawText(text, new Point(origin.X + (s - text.Width) / 2, origin.Y + (s - text.Height) / 2));
                break;
            }
        }
    }
}
