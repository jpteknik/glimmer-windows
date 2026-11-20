// Ported from App/CanvasView.swift (CanvasView, CanvasNSView: hosting the canvas, rendering, lifecycle).
// The Mac draws into a CAMetalLayer at the display's backing scale. Here the canvas owns a WriteableBitmap (Pbgra32)
// at device-pixel size; the engine's renderer fills a back buffer on a worker thread (ICanvasFrame.Render) and the UI
// thread only copies the finished frame and invalidates. Overlays are drawn on top with a DrawingContext
// (CanvasOverlay.cs), input is in CanvasSurface.Input.cs.
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glimmer.App.Localization;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Controls.Canvas;

public sealed partial class CanvasSurface : FrameworkElement
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(
        nameof(Session), typeof(IEditorSession), typeof(CanvasSurface), new PropertyMetadata(null, (d, e) => ((CanvasSurface)d).SessionChanged(e)));

    private WriteableBitmap? bitmap;
    private byte[] backBuffer = [];
    private int pixelWidth, pixelHeight;
    private double scale = 1;
    private bool renderInFlight, renderAgain;
    private readonly CancellationTokenSource closing = new();
    private readonly CanvasOverlay overlay;

    public CanvasSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;              // the canvas is the document; a focus ring around all of it says nothing
        ClipToBounds = true;
        AllowDrop = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(this, false);  // a long press is not a right click here
        System.Windows.Input.Stylus.SetIsFlicksEnabled(this, false);
        System.Windows.Input.Stylus.SetIsTapFeedbackEnabled(this, false);
        // Letters are tools here: an input method would take them for composition. The text editor turns it back on.
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(this, false);
        overlay = new CanvasOverlay(this);
        Loaded += (_, _) => { AttachWindowHook(); RequestFrame(); };
        Unloaded += (_, _) => DetachWindowHook();
    }

    public IEditorSession? Session { get => (IEditorSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }

    private ICanvasController? Controller => Session?.Canvas;

    private void SessionChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IEditorSession old)
        {
            old.Canvas.NeedsRender -= OnNeedsRender;
            old.PropertyChanged -= OnSessionChanged;
        }
        if (e.NewValue is IEditorSession session)
        {
            session.Canvas.NeedsRender += OnNeedsRender;
            session.PropertyChanged += OnSessionChanged;
            session.Canvas.BackingScale = scale;
            if (ActualWidth > 0) session.Canvas.ViewDidResize(new Vec2(ActualWidth, ActualHeight));
        }
        RequestFrame();
    }

    private void OnSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // When the palette closes its text field is gone: take the keyboard back, so single-key shortcuts work without
        // first clicking the canvas (CanvasNSView.observeSession).
        if (e.PropertyName == nameof(IEditorSession.IsPaletteOpen) && Session is { IsPaletteOpen: false, EditingText: null } && IsVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
        if (e.PropertyName is nameof(IEditorSession.EffectiveTool) or nameof(IEditorSession.Tool)) UpdateCursor();
        if (e.PropertyName is nameof(IEditorSession.EditingText) or nameof(IEditorSession.EffectiveTool) or nameof(IEditorSession.Tool)
            or nameof(IEditorSession.State) or null or "")
            QueueTextEditorUpdate();
    }

    private void OnNeedsRender(object? sender, EventArgs e)
    {
        InvalidateVisual();     // overlays
        RequestFrame();         // pixels
        if (textEditor is not null) QueueTextEditorUpdate();   // the view moved: the editor follows the text
    }

    // ------------------------------------------------------------------------------------------------- layout & DPI

    protected override Size MeasureOverride(Size availableSize)
    {
        textEditor?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdatePixelSize();
        Controller?.ViewDidResize(new Vec2(ActualWidth, ActualHeight));
        RequestFrame();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdatePixelSize();
        RequestFrame();
    }

    private void UpdatePixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        scale = dpi.DpiScaleX;
        if (Controller is { } c && Math.Abs(c.BackingScale - scale) > 1e-9) c.BackingScale = scale;
        int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        if (w == pixelWidth && h == pixelHeight && bitmap is not null) return;
        pixelWidth = w;
        pixelHeight = h;
        // The bitmap's DPI makes its size in DIPs equal the element's, so it lands one pixel per device pixel.
        bitmap = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
    }

    // ------------------------------------------------------------------------------------------------- rendering

    /// <summary>Asks the renderer for a new frame. At most one frame is in flight; a request while one is drawing is
    /// remembered and served as soon as that one is on screen (latest wins).</summary>
    public void RequestFrame()
    {
        if (Session is not { } session || bitmap is null || !IsLoaded) return;
        if (renderInFlight) { renderAgain = true; return; }
        renderInFlight = true;
        renderAgain = false;
        int w = pixelWidth, h = pixelHeight, stride = w * 4;
        if (backBuffer.Length != stride * h) backBuffer = new byte[stride * h];
        var buffer = backBuffer;
        var frame = session.Canvas.Renderer.CaptureFrame(w, h);       // UI thread: an immutable snapshot
        var token = closing.Token;
        Task.Run(() =>
        {
            bool more = false;
            try { more = frame.Render(buffer, stride, token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => Session?.Report(ex)); }
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(buffer, frame.PixelWidth, frame.PixelHeight, stride, more));
        }, token);
    }

    private void Present(byte[] buffer, int w, int h, int stride, bool more)
    {
        renderInFlight = false;
        if (bitmap is not null && bitmap.PixelWidth == w && bitmap.PixelHeight == h)
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), buffer, stride, 0);
        else
            renderAgain = true;     // the canvas was resized while drawing: draw again at the new size
        if (renderAgain || more) RequestFrame();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // The surround first, so a resize never flashes a hole before the next frame arrives.
        dc.DrawRectangle((Brush)FindResource("Glimmer.BackgroundBrush"), null, new Rect(RenderSize));
        if (bitmap is not null) dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth / scale, bitmap.PixelHeight / VisualTreeHelper.GetDpi(this).DpiScaleY));
        if (Session is { } session) overlay.Draw(dc, session);
    }

    // ------------------------------------------------------------------------------------------------- accessibility

    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasAutomationPeer(this);

    /// <summary>The canvas as a screen reader meets it: an image named "Canvas" whose value says the size, the layers,
    /// the zoom, the active layer, whether there is a selection and the tool (CanvasNSView.accessibilityValue).</summary>
    private sealed class CanvasAutomationPeer(CanvasSurface owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => "GlimmerCanvas";
        protected override string GetNameCore() => Loc.T("Canvas");
        protected override string GetAutomationIdCore() => "canvas";
        protected override bool IsKeyboardFocusableCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        public string Value
        {
            get
            {
                if (owner.Session is not { } s) return "";
                var state = s.State;
                int zoom = (int)Math.Round(s.Canvas.Zoom * 100);
                var text = Loc.F("{0} by {1} pixels, {2} layers, zoom {3} percent", state.Width, state.Height, state.AllLayers.Count(), zoom);
                if (state.ActiveLayer is { } layer) text += ", " + Loc.F("active layer {0}", layer.Name);
                if (state.Selection is not null) text += ", " + Loc.T("has a selection");
                return text + ", " + Loc.F("tool {0}", Loc.T(s.Tool.Title()));
            }
        }

        public bool IsReadOnly => true;
        public void SetValue(string value) => throw new InvalidOperationException();
    }
}
