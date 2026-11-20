// Ported from App/NavigatorView.swift. A thumbnail of the whole document with the visible area outlined. Drag to pan;
// the slider zooms around the view centre; the view can be rotated and reset.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimmer.App.Controls;
using Glimmer.App.Localization;
using Glimmer.App.Views.Panels;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Views;

public sealed class NavigatorView : UserControl
{
    private IEditorSession? session;
    private readonly Preview preview;
    private readonly Slider zoom;
    private readonly TextBlock zoomText;
    private readonly TextBox angle;
    private readonly Button reset;
    private bool updating;

    public NavigatorView()
    {
        AutomationProperties.SetName(this, Loc.T("Navigator"));
        preview = new Preview(this) { Width = 220, Height = 150 };
        zoom = new Slider { Minimum = -6, Maximum = 5, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.TryFindResource("Glimmer.Compact.Slider") };
        AutomationProperties.SetName(zoom, Loc.T("Zoom"));
        zoom.ValueChanged += (_, e) =>
        {
            if (updating || session is null) return;
            var c = session.Canvas;
            c.ZoomBy(Math.Pow(2, e.NewValue) / Math.Max(c.Zoom, 0.01), c.ViewSize / 2);
        };
        zoomText = new TextBlock { Width = 44, TextAlignment = System.Windows.TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var zoomRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var zoomOut = IconButton("minus.magnifyingglass", "Zoom Out", () => session?.Canvas.ZoomBy(1 / 1.25, session.Canvas.ViewSize / 2));
        var zoomIn = IconButton("plus.magnifyingglass", "Zoom In", () => session?.Canvas.ZoomBy(1.25, session.Canvas.ViewSize / 2));
        DockPanel.SetDock(zoomOut, Dock.Left);
        DockPanel.SetDock(zoomText, Dock.Right);
        DockPanel.SetDock(zoomIn, Dock.Right);
        zoomRow.Children.Add(zoomOut);
        zoomRow.Children.Add(zoomText);
        zoomRow.Children.Add(zoomIn);
        zoomRow.Children.Add(zoom);

        angle = new TextBox { Width = 56, Style = (Style)Application.Current.FindResource("Glimmer.Compact.TextBox"), HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(angle, Loc.T("View rotation in degrees"));
        angle.LostKeyboardFocus += (_, _) => ApplyAngle();
        angle.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyAngle(); };
        reset = new Button { Content = Loc.T("Reset"), Style = (Style)Application.Current.FindResource("Glimmer.Compact.Button") };
        reset.Click += (_, _) => session?.Canvas.Perform(CommandID.ResetViewRotation);
        var rotateRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        var left = IconButton("rotate.left", "Rotate the view 15° left", () => session?.Canvas.Perform(CommandID.RotateViewLeft));
        var right = IconButton("rotate.right", "Rotate the view 15° right", () => session?.Canvas.Perform(CommandID.RotateViewRight));
        foreach (var e in new UIElement[] { left, angle, new TextBlock { Text = "°", Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, right })
        {
            DockPanel.SetDock(e, Dock.Left);
            rotateRow.Children.Add(e);
        }
        DockPanel.SetDock(reset, Dock.Right);
        rotateRow.Children.Add(reset);

        var body = new StackPanel { Width = 220 };
        body.Children.Add(preview);
        body.Children.Add(zoomRow);
        body.Children.Add(rotateRow);
        Content = new PanelSurface { Content = body, Padding = new Thickness(10), CornerRadius = new CornerRadius(12) };

        DataContextChanged += (_, e) =>
        {
            if (session is not null) { session.Canvas.PropertyChanged -= OnChanged; session.PropertyChanged -= OnChanged; }
            session = (e.NewValue as DocumentChrome)?.Session;
            if (session is not null) { session.Canvas.PropertyChanged += OnChanged; session.PropertyChanged += OnChanged; }
            Refresh();
        };
    }

    private Button IconButton(string symbol, string name, Action click)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("Glimmer.IconButton"), Content = new Icon { Symbol = symbol, Size = 13 }, ToolTip = Loc.T(name) };
        AutomationProperties.SetName(b, Loc.T(name));
        b.Click += (_, _) => click();
        return b;
    }

    private void ApplyAngle()
    {
        if (session is not null && double.TryParse(angle.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
            session.Canvas.RotationDegrees = Math.Clamp(v, -360, 360);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) { if (IsVisible) Refresh(); }

    private void Refresh()
    {
        if (session is null) return;
        updating = true;
        var c = session.Canvas;
        zoom.Value = Math.Log2(Math.Max(c.Zoom, 0.01));
        zoomText.Text = $"{(int)Math.Round(c.Zoom * 100)} %";
        AutomationProperties.SetItemStatus(zoom, zoomText.Text);
        if (!angle.IsKeyboardFocused) angle.Text = (Math.Round(c.RotationDegrees * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture);
        reset.IsEnabled = c.CanPerform(CommandID.ResetViewRotation);
        preview.InvalidateVisual();
        updating = false;
    }

    /// <summary>The document thumbnail with the visible area outlined; dragging centres the view on the point.</summary>
    private sealed class Preview(NavigatorView owner) : FrameworkElement
    {
        private Rect Fitted()
        {
            var size = owner.session?.Canvas.DocumentSize ?? new Vec2(1, 1);
            double s = Math.Min(ActualWidth / Math.Max(size.X, 1), ActualHeight / Math.Max(size.Y, 1));
            double w = size.X * s, h = size.Y * s;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (owner.session is not { } session) return;
            var r = Fitted();
            if (session.NavigatorImage is { } image)
            {
                Checkerboard.Draw(dc, r, 6);
                dc.DrawImage(Thumbnails.Bitmap(image), r);
            }
            else
            {
                dc.DrawRectangle(new SolidColorBrush(Theme.WithAlpha(Theme.Ink, 0.08)), null, r);
            }
            var c = session.Canvas;
            var visible = c.Transform.VisibleDocumentRect(c.ViewSize);
            double s = r.Width / Math.Max(c.DocumentSize.X, 1);
            var viewport = Rect.Intersect(new Rect(r.X + visible.X * s, r.Y + visible.Y * s, visible.Width * s, visible.Height * s), r);
            if (!viewport.IsEmpty) dc.DrawRectangle(null, new Pen(Brushes.Red, 1.5), viewport);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) { CaptureMouse(); Centre(e.GetPosition(this)); }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Centre(e.GetPosition(this)); }
        protected override void OnMouseUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

        /// <summary>Pans so the document point under p is in the view centre.</summary>
        private void Centre(Point p)
        {
            if (owner.session is not { } session) return;
            var r = Fitted();
            var c = session.Canvas;
            double s = c.DocumentSize.X / Math.Max(r.Width, 1);
            var target = new Vec2((p.X - r.X) * s, (p.Y - r.Y) * s);
            c.Pan(c.ViewSize / 2 - c.Transform.ViewPoint(target));
        }
    }
}
