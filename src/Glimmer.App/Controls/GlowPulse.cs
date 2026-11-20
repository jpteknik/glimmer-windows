// Ported from App/GlowPulse.swift. The glow behind the app's icon, breathing slowly: its strength eases between about
// 60 % and 100 % and its radius follows a little, one breath in about four seconds. Put it behind the icon, the same
// size, with the icon's corner radius. It stands still at its ordinary strength when Windows' animation effects are off
// (Settings › Accessibility › Visual effects), and it doesn't run while its window is minimised or hidden or while
// Glimmer isn't the active app. Not in the accessibility tree, never hit by a click.
//
// The Mac animates a Core Animation shadow in the window server, with no work in the app per frame. WPF animates the
// effect's properties on the UI thread (a few property updates a frame while visible): cheap, but not free.
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Glimmer.App.Controls;

public sealed class GlowPulse : FrameworkElement
{
    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(nameof(GlowColor), typeof(Color), typeof(GlowPulse),
        new FrameworkPropertyMetadata(Color.FromRgb(0xC4, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((GlowPulse)d).Configure()));
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(GlowPulse),
        new FrameworkPropertyMetadata(0.35, (d, _) => ((GlowPulse)d).Configure()));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(GlowPulse),
        new FrameworkPropertyMetadata(24.0, (d, _) => ((GlowPulse)d).Configure()));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(GlowPulse),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>WPF's blur radius against Core Animation's shadowRadius (same look, not measured on screen).</summary>
    private const double BlurPerRadius = 2.0;

    private readonly DropShadowEffect glow = new() { ShadowDepth = 0, RenderingBias = RenderingBias.Performance };
    private Window? window;
    private bool breathing;

    public GlowPulse()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Effect = glow;
        Configure();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => Refresh();
    }

    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    /// <summary>The glow's opacity when it stands still (what the design had before it moved).</summary>
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    protected override void OnRender(DrawingContext dc)
    {
        // The shape that casts the glow; the icon in front of it covers it.
        double r = Math.Min(CornerRadius, Math.Min(ActualWidth, ActualHeight) / 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(GlowColor), null, new Rect(RenderSize), r, r);
    }

    private void Configure()
    {
        glow.Color = GlowColor;
        if (breathing) { Stop(); Refresh(); }
        else
        {
            glow.Opacity = Strength;
            glow.BlurRadius = Radius * BlurPerRadius;
        }
    }

    private void Attach()
    {
        window = Window.GetWindow(this);
        if (window is not null)
        {
            window.StateChanged += OnChanged;
            window.IsVisibleChanged += OnVisibleChanged;
        }
        if (Application.Current is { } app)
        {
            app.Activated += OnChanged;
            app.Deactivated += OnChanged;
        }
        SystemEvents.UserPreferenceChanged += OnPreferences;
        Refresh();
    }

    private void Detach()
    {
        if (window is not null)
        {
            window.StateChanged -= OnChanged;
            window.IsVisibleChanged -= OnVisibleChanged;
            window = null;
        }
        if (Application.Current is { } app)
        {
            app.Activated -= OnChanged;
            app.Deactivated -= OnChanged;
        }
        SystemEvents.UserPreferenceChanged -= OnPreferences;
        Stop();
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void OnVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e) => Refresh();
    private void OnPreferences(object? sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    /// <summary>Whether the glow should be moving right now.</summary>
    private bool ShouldBreathe =>
        SystemParameters.ClientAreaAnimation && IsVisible && window is { IsVisible: true, IsActive: true } w && w.WindowState != WindowState.Minimized;

    private void Refresh()
    {
        if (!ShouldBreathe) { Stop(); return; }
        if (breathing) return;
        breathing = true;
        // "100 %" is a little above the still glow and "60 %" of that a little below it, so the average is what the
        // design had.
        double peak = Math.Min(1, Strength * 1.25);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var opacity = new DoubleAnimation(peak * 0.6, peak, TimeSpan.FromSeconds(2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        var size = new DoubleAnimation(Radius * 0.92 * BlurPerRadius, Radius * 1.12 * BlurPerRadius, TimeSpan.FromSeconds(2))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, opacity);
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, size);
    }

    private void Stop()
    {
        breathing = false;
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        glow.Opacity = Strength;
        glow.BlurRadius = Radius * BlurPerRadius;
    }
}
