// Lays Glimmer's palette over WPF's Fluent theme (Application.ThemeMode = Dark), and gives way to the system's
// colours in high contrast. Run once at startup, after App.xaml has loaded (the Fluent dictionary is in place then).
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Glimmer.App;

public static class ThemeResources
{
    private static readonly Uri GlimmerDictionary = new("pack://application:,,,/Glimmer;component/Themes/Glimmer.xaml", UriKind.Absolute);
    private static readonly List<object> overriddenKeys = [];

    public static void Apply(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = GlimmerDictionary });
        AddCompactStyles(app);
        ApplyPalette(app);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) app.Dispatcher.Invoke(() => ApplyPalette(app));
        };
    }

    /// <summary>SwiftUI's .controlSize(.small) for the Fluent controls the options bar and the panels use.</summary>
    private static void AddCompactStyles(Application app)
    {
        double callout = (double)app.FindResource("Glimmer.Size.Callout");
        Style Based(object baseKey, Type type, params (DependencyProperty Property, object Value)[] setters)
        {
            var style = new Style(type, app.TryFindResource(baseKey) as Style);
            foreach (var (p, v) in setters) style.Setters.Add(new Setter(p, v));
            style.Seal();
            return style;
        }

        app.Resources["Glimmer.Compact.Button"] = Based(typeof(Button), typeof(Button),
            (Control.PaddingProperty, new Thickness(10, 3, 10, 3)), (FrameworkElement.MinHeightProperty, 26.0), (Control.FontSizeProperty, callout));
        app.Resources["Glimmer.Compact.AccentButton"] = Based("AccentButtonStyle", typeof(Button),
            (Control.PaddingProperty, new Thickness(10, 3, 10, 3)), (FrameworkElement.MinHeightProperty, 26.0), (Control.FontSizeProperty, callout));
        app.Resources["Glimmer.Compact.ComboBox"] = Based(typeof(ComboBox), typeof(ComboBox),
            (FrameworkElement.MinHeightProperty, 26.0), (Control.PaddingProperty, new Thickness(8, 2, 6, 2)), (Control.FontSizeProperty, callout));
        app.Resources["Glimmer.Compact.CheckBox"] = Based(typeof(CheckBox), typeof(CheckBox),
            (FrameworkElement.MinHeightProperty, 20.0), (FrameworkElement.MinWidthProperty, 0.0), (Control.FontSizeProperty, callout),
            (FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        app.Resources["Glimmer.Compact.TextBox"] = Based(typeof(TextBox), typeof(TextBox),
            (FrameworkElement.MinHeightProperty, 26.0), (Control.PaddingProperty, new Thickness(6, 2, 6, 2)), (Control.FontSizeProperty, callout),
            (Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        app.Resources["Glimmer.Compact.Slider"] = Based(typeof(Slider), typeof(Slider),
            (FrameworkElement.MinHeightProperty, 20.0), (FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center),
            (Slider.IsMoveToPointEnabledProperty, true));
    }

    /// <summary>Glimmer's colours over Fluent's, as root entries of the application's dictionary (they win over any
    /// merged dictionary, wherever WPF put the Fluent one). In high contrast they are taken away again, and Glimmer's
    /// own brushes take the system's colours, so every view follows the person's theme.</summary>
    private static void ApplyPalette(Application app)
    {
        foreach (var key in overriddenKeys) app.Resources.Remove(key);
        overriddenKeys.Clear();
        void Set(object key, object value)
        {
            if (value is Freezable f && f.CanFreeze) f.Freeze();
            app.Resources[key] = value;
            overriddenKeys.Add(key);
        }
        SolidColorBrush B(Color c) => new(c);

        if (SystemParameters.HighContrast)
        {
            Set("Glimmer.BackgroundBrush", SystemColors.WindowBrush);
            Set("Glimmer.PanelBrush", SystemColors.WindowBrush);
            Set("Glimmer.PanelSurfaceBrush", SystemColors.WindowBrush);
            Set("Glimmer.RaisedBrush", SystemColors.ControlBrush);
            Set("Glimmer.EdgeBrush", SystemColors.WindowTextBrush);
            Set("Glimmer.InkBrush", SystemColors.WindowTextBrush);
            Set("Glimmer.InkSoftBrush", SystemColors.WindowTextBrush);
            Set("Glimmer.MutedBrush", SystemColors.WindowTextBrush);
            Set("Glimmer.TertiaryBrush", SystemColors.GrayTextBrush);
            Set("Glimmer.AccentBrush", SystemColors.HighlightBrush);
            Set("Glimmer.AccentFillBrush", SystemColors.HighlightBrush);
            Set("Glimmer.OnAccentBrush", SystemColors.HighlightTextBrush);
            foreach (var tint in new[] { "Glimmer.AccentTint35Brush", "Glimmer.AccentTint28Brush", "Glimmer.AccentTint20Brush", "Glimmer.AccentTint15Brush", "Glimmer.AccentTint8Brush" })
                Set(tint, SystemColors.HighlightBrush);
            Set("Glimmer.FieldEdgeBrush", SystemColors.WindowTextBrush);
            return;
        }

        var accent = Theme.Accent;
        var fill = Theme.AccentFill;
        var hover = Mix(fill, Colors.White, 0.08);
        var pressed = Mix(fill, Colors.Black, 0.15);
        foreach (var k in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
            Set(k, accent);
        foreach (var k in new[] { "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            Set(k, fill);

        // Filled accent surfaces carry white text: accentFill (4.8 : 1 with white).
        Set("AccentFillColorDefaultBrush", B(fill));
        Set("AccentFillColorSecondaryBrush", new SolidColorBrush(fill) { Opacity = 0.9 });
        Set("AccentFillColorTertiaryBrush", new SolidColorBrush(fill) { Opacity = 0.8 });
        Set("AccentButtonBackground", B(fill));
        Set("AccentButtonBackgroundPointerOver", B(hover));
        Set("AccentButtonBackgroundPressed", B(pressed));
        Set("AccentButtonBorderBrush", B(fill));
        Set("AccentButtonBorderBrushPointerOver", B(hover));
        Set("AccentButtonBorderBrushPressed", B(pressed));
        foreach (var k in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush" })
            Set(k, Brushes.White);
        foreach (var k in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                                  "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
                                  "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver",
                                  "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver" })
            Set(k, B(fill));

        // Accent on dark surfaces (text, icons, thin marks): accent (5.1 : 1 on the panel).
        foreach (var k in new[] { "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
                                  "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "TextControlFocusedBorderBrush", "TextControlBorderBrushFocused",
                                  "ComboBoxItemPillFillBrush", "ListViewItemPillFillBrush", "ProgressBarForeground", "KeyboardFocusBorderColorBrush" })
            Set(k, B(accent));
        Set("ListBoxItemSelectedBackgroundThemeBrush", B(Theme.AccentTint35));
        Set("ComboBoxItemBackgroundSelected", B(Theme.AccentTint28));

        // Surfaces and text.
        Set("ApplicationBackgroundBrush", B(Theme.Background));
        Set("WindowBackground", B(Theme.Background));
        Set("SolidBackgroundFillColorBaseBrush", B(Theme.Panel));
        foreach (var k in new[] { "FlyoutBackground", "ContextMenuBackground", "ComboBoxDropDownBackground", "ToolTipBackground", "ToolTipBackgroundBrush" })
            Set(k, B(Theme.Panel));
        foreach (var k in new[] { "FlyoutBorderBrush", "ContextMenuBorderBrush", "ComboBoxDropDownBorderBrush", "ToolTipBorderBrush" })
            Set(k, B(Theme.Edge));
        foreach (var k in new[] { "TextFillColorPrimaryBrush", "WindowForeground", "ContextMenuForeground", "ToolTipForeground", "ToolTipForegroundBrush" })
            Set(k, B(Theme.Ink));
        Set("TextFillColorSecondaryBrush", B(Theme.Muted));

        // Compact text fields everywhere (Fluent's default is 32).
        Set("TextControlThemeMinHeight", 26.0);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
