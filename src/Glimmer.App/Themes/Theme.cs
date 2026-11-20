// Ported from App/Theme.swift
using System.Windows;
using System.Windows.Media;

namespace Glimmer.App;

/// <summary>Glimmer's look, shared with the web app: near-black surfaces, soft borders and an indigo accent.
/// Themes/Glimmer.xaml turns these into the brushes and styles the views use (always as DynamicResource, so high
/// contrast can put the system's colours in their place, see ThemeResources).</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromRgb(0x0c, 0x0d, 0x11);
    public static readonly Color Panel = Color.FromRgb(0x16, 0x17, 0x1c);
    public static readonly Color Raised = Color.FromRgb(0x1f, 0x20, 0x27);
    public static readonly Color Edge = Color.FromRgb(0x2c, 0x2e, 0x37);
    public static readonly Color Ink = Color.FromRgb(0xe8, 0xe9, 0xef);
    public static readonly Color Muted = Color.FromRgb(0x96, 0x9a, 0xa7);
    public static readonly Color Accent = Color.FromRgb(0x6d, 0x7d, 0xff);
    /// <summary>The accent for filled surfaces with white text (prominent buttons, your chat messages): 4.8 : 1 with
    /// white, where Accent gives only 3.5 : 1. Accent stays for text, icons and borders on dark surfaces (5.1 : 1 on
    /// Panel).</summary>
    public static readonly Color AccentFill = Color.FromRgb(0x55, 0x63, 0xe8);
    /// <summary>Outline of input fields: 3.1 : 1 against Panel, where Edge gives 1.3 : 1.</summary>
    public static readonly Color FieldEdge = WithAlpha(Muted, 0.6);
    /// <summary>A floating chrome surface: the panel colour at 96 %.</summary>
    public static readonly Color PanelSurface = WithAlpha(Panel, 0.96);

    // Tints the views use (Swift: Theme.accent.opacity(…), Color.black.opacity(…)).
    public static readonly Color AccentTint35 = WithAlpha(Accent, 0.35);
    public static readonly Color AccentTint28 = WithAlpha(Accent, 0.28);
    public static readonly Color AccentTint20 = WithAlpha(Accent, 0.20);
    public static readonly Color AccentTint15 = WithAlpha(Accent, 0.15);
    public static readonly Color AccentTint8 = WithAlpha(Accent, 0.08);
    public static readonly Color InkSoft = WithAlpha(Ink, 0.85);
    /// <summary>SwiftUI's .tertiary on a dark surface.</summary>
    public static readonly Color Tertiary = WithAlpha(Ink, 0.4);
    public static readonly Color Scrim = WithAlpha(Colors.Black, 0.35);
    public static readonly Color Shadow = Colors.Black;

    // States (GlimmerCore/StatusKind.swift, the colours on a dark surface).
    public static readonly Color StatusError = Color.FromRgb(0xff, 0x6b, 0x6b);
    public static readonly Color StatusWarning = Color.FromRgb(0xff, 0xb3, 0x40);
    public static readonly Color StatusNotice = Color.FromRgb(0x96, 0x9a, 0xa7);
    public static readonly Color StatusSuccess = Color.FromRgb(0x4c, 0xd7, 0x87);

    /// <summary>The canvas surround, as sRGB components.</summary>
    public static (double R, double G, double B) BackgroundRGB => (0x0c / 255.0, 0x0d / 255.0, 0x11 / 255.0);

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Corner radius of floating panels (Swift panelSurface(cornerRadius: 12)).</summary>
    public static readonly CornerRadius PanelCorner = new(12);
}
