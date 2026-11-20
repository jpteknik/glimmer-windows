// Small converters the XAML uses.
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Controls;

/// <summary>true → Visible, false → Collapsed; with ConverterParameter "invert" the other way round.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value is true;
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Visibility.Visible;
}

/// <summary>null → Collapsed, anything else → Visible ("invert" swaps).</summary>
public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool shown = value is not null && !(value is string s && s.Length == 0);
        if (parameter as string == "invert") shown = !shown;
        return shown ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Looks the English key up, then upper-cases it in the shown language (SwiftUI .textCase(.uppercase)).</summary>
public sealed class LocUpperConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.T(value?.ToString() ?? parameter as string ?? "").ToUpper(CultureInfo.CurrentCulture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>SRGBColor → a brush (for swatches and colour wells).</summary>
public sealed class SRGBBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SRGBColor c => Theme.Frozen(ToColor(c)),
        _ => Brushes.Transparent,
    };

    public static Color ToColor(SRGBColor c, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), Byte(c.R), Byte(c.G), Byte(c.B));

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A tool's symbol and tooltip in the rail (its title is the editor's ToolTitles.Title).</summary>
public static class ToolPresentation
{
    public static string SymbolName(this ToolID tool) => tool switch
    {
        ToolID.Move => "arrow.up.and.down.and.arrow.left.and.right", ToolID.Hand => "hand.raised",
        ToolID.RectangleMarquee => "rectangle.dashed", ToolID.EllipseMarquee => "circle.dashed", ToolID.Lasso => "lasso",
        ToolID.PolygonLasso => "skew", ToolID.MagicWand => "wand.and.stars", ToolID.MagicSelect => "wand.and.sparkles",
        ToolID.Brush => "paintbrush.pointed", ToolID.Eraser => "eraser", ToolID.PaintBucket => "drop.halffull",
        ToolID.Gradient => "square.bottomhalf.filled", ToolID.Eyedropper => "eyedropper", ToolID.Crop => "crop",
        ToolID.CloneStamp => "seal", ToolID.PatternStamp => "square.grid.3x3.square", ToolID.HealingBrush => "bandage",
        ToolID.Dodge => "circle.lefthalf.filled", ToolID.Burn => "flame", ToolID.Blur => "drop.halffull", ToolID.Sharpen => "triangle",
        ToolID.Smudge => "hand.point.up", ToolID.FreeTransform => "arrow.up.left.and.down.right.and.arrow.up.right.and.down.left",
        ToolID.Type => "textformat", ToolID.Pen => "pencil.tip.crop.circle", _ => "square.on.circle",
    };

    /// <summary>"Brush (B)": the tooltip (ToolID.helpText), with the shortcut key.</summary>
    public static string HelpText(this ToolID tool) =>
        tool.ShortcutKey() is { } key ? $"{Loc.T(tool.Title())} ({char.ToUpperInvariant(key)})" : Loc.T(tool.Title());

}

/// <summary>Converters for the tool rail's item template.</summary>
public static class ToolPaletteConverters
{
    public static readonly IValueConverter Symbol = new FuncConverter(v => v is ToolID t ? t.SymbolName() : null);
    public static readonly IValueConverter Help = new FuncConverter(v => v is ToolID t ? t.HelpText() : null);
}

/// <summary>"12 pts" for a path row (the plural table's "{0} points" in Swift is "\(n) pts").</summary>
public static class PathsPanelConverters
{
    public static readonly IValueConverter PointCount = new FuncConverter(v =>
        v is VectorPath p ? Loc.F("{0} pts", p.Subpaths.Sum(s => s.Anchors.Length)) : "");
}

/// <summary>A one-way converter from a function.</summary>
public sealed class FuncConverter(Func<object?, object?> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A tool → its title through Loc (for bindings).</summary>
public sealed class ToolTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ToolID t ? Loc.T(t.Title()) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Equality with the ConverterParameter, both ways: for radio-style choices bound to one value.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

/// <summary>A UiText (or null) → the shown string.</summary>
public sealed class UiTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is UiText t ? Loc.T(t) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
