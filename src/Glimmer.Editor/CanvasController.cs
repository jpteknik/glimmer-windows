// Ported from App/CanvasController.swift, plus the frame rendering the Mac's CanvasNSView.draw() does with Metal
// (CanvasView.swift), here on the CPU into the WPF canvas's buffer.
using System.Numerics;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>Per-window view state for the canvas: zoom, pan, rotation.</summary>
public sealed class CanvasController : ObservableObject, ICanvasController
{
    private readonly EditorSession session;
    private ViewTransform transform = new();
    private Vec2 documentSize = new(1920, 1080);
    private double backingScale = 1;
    private Vec2 viewSize = Vec2.Zero;
    private bool hasFitted;
    private double gestureRawRotation;

    internal CanvasController(EditorSession session)
    {
        this.session = session;
        Renderer = new CanvasFrameRenderer(session, this);
    }

    public ViewTransform Transform
    {
        get => transform;
        private set
        {
            if (Equals(transform, value)) return;
            transform = value;
            Raise(nameof(Transform), nameof(Zoom), nameof(RotationDegrees));
            RequestRender();
            session.NoteZoomChanged();
        }
    }

    public Vec2 DocumentSize
    {
        get => documentSize;
        set
        {
            if (documentSize == value) return;
            documentSize = value;
            Raise();
            if (KeepZoomOnResize && viewSize.X > 0)
            {
                // Same zoom, new canvas centred (after a crop).
                KeepZoomOnResize = false;
                double scale = transform.Scale;
                Transform = ViewTransform.Fitting(documentSize, viewSize, transform.Rotation).SetScale(scale, viewSize / 2);
                hasFitted = true;
                return;
            }
            hasFitted = false;
            FitIfPossible();
        }
    }

    /// <summary>Drawable pixels per view point.</summary>
    public double BackingScale
    {
        get => backingScale;
        set
        {
            if (!Set(ref backingScale, value)) return;
            Raise(nameof(Zoom));
            RequestRender();
            session.NoteZoomChanged();
        }
    }

    public Vec2 ViewSize
    {
        get => viewSize;
        private set => Set(ref viewSize, value);
    }

    public event EventHandler? NeedsRender;
    /// <summary>The next document size change keeps the zoom instead of fitting the window.</summary>
    public bool KeepZoomOnResize { get; set; }

    /// <summary>Zoom relative to device pixels: 100 % shows one document pixel per screen pixel.</summary>
    public double Zoom => transform.Scale * backingScale;

    private Vec2 ViewCenter => viewSize / 2;

    public void RequestRender() => NeedsRender?.Invoke(this, EventArgs.Empty);

    public ICanvasRenderer Renderer { get; }

    // MARK: View lifecycle

    public void ViewDidResize(Vec2 size)
    {
        var old = viewSize;
        ViewSize = size;
        if (hasFitted && old != Vec2.Zero) Transform = transform.Pan((size - old) / 2);   // keep the centre where it was
        else FitIfPossible();
    }

    private void FitIfPossible()
    {
        if (!(viewSize.X > 0 && viewSize.Y > 0)) return;
        FitToWindow();
        hasFitted = true;
    }

    // MARK: Interaction

    public void Pan(Vec2 delta) => Transform = transform.Pan(delta);

    public void ZoomBy(double factor, Vec2 anchor) => Transform = transform.Zoom(factor, anchor);

    public void BeginRotationGesture() => gestureRawRotation = transform.Rotation;

    /// <summary><paramref name="delta"/> in radians, clockwise positive. Snaps to right angles.</summary>
    public void RotateGesture(double delta, Vec2 anchor)
    {
        gestureRawRotation += delta;
        Transform = transform.SetRotation(ViewTransform.SnappedRotation(gestureRawRotation), anchor);
    }

    /// <summary>View rotation in degrees, clockwise positive.</summary>
    public double RotationDegrees
    {
        get => transform.Rotation * 180 / Math.PI;
        set => Transform = transform.SetRotation(value * Math.PI / 180, ViewCenter);
    }

    public void ToggleFitAndActualSize(Vec2 anchor)
    {
        if (Math.Abs(Zoom - 1) < 0.001) FitToWindow();
        else Transform = transform.SetScale(1 / backingScale, anchor);
    }

    // MARK: Commands

    public bool CanPerform(CommandID id)
    {
        if (id == CommandID.ZoomIn) return Zoom < ViewTransform.MaxScale * backingScale;
        if (id == CommandID.ZoomOut) return Zoom > ViewTransform.MinScale * backingScale;
        if (id == CommandID.ResetViewRotation) return transform.Rotation != 0;
        return true;
    }

    public void Perform(CommandID id)
    {
        if (id == CommandID.ZoomIn) Transform = transform.SetScale(ZoomLadder.Next(Zoom) / backingScale, ViewCenter);
        else if (id == CommandID.ZoomOut) Transform = transform.SetScale(ZoomLadder.Previous(Zoom) / backingScale, ViewCenter);
        else if (id == CommandID.FitToWindow) FitToWindow();
        else if (id == CommandID.ActualSize) Transform = transform.SetScale(1 / backingScale, ViewCenter);
        else if (id == CommandID.RotateViewLeft) Transform = transform.Rotate(-Math.PI / 12, ViewCenter);
        else if (id == CommandID.RotateViewRight) Transform = transform.Rotate(Math.PI / 12, ViewCenter);
        else if (id == CommandID.ResetViewRotation) Transform = transform.SetRotation(0, ViewCenter);
    }

    private void FitToWindow()
    {
        if (!(viewSize.X > 0 && viewSize.Y > 0)) return;
        Transform = ViewTransform.Fitting(documentSize, viewSize, transform.Rotation);
    }

    /// <summary>Tiles of <paramref name="bounds"/> that are on screen, nearest to the view centre first.</summary>
    public IReadOnlyList<TileCoord> VisibleTiles(PixelRect bounds) => VisibleTiles(transform, viewSize, bounds);

    internal static IReadOnlyList<TileCoord> VisibleTiles(ViewTransform transform, Vec2 viewSize, PixelRect bounds)
    {
        if (!(viewSize.X > 0 && viewSize.Y > 0)) return [];
        var visible = transform.VisibleDocumentRect(viewSize).Intersection(bounds);
        var center = transform.DocumentPoint(viewSize / 2);
        double s = TileGeometry.Size;
        return [.. visible.TileCoords.OrderBy(c => (new Vec2(c.X + 0.5, c.Y + 0.5) * s - center).LengthSquared)];
    }

    /// <summary>Document pixel under a view point.</summary>
    public Vec2 DocumentPoint(Vec2 viewPoint) => transform.DocumentPoint(viewPoint);
}

/// <summary>Draws frames for the WPF canvas (CanvasNSView.draw()): composites the visible tiles progressively and
/// presents them with the checkerboard and the surround. One compositor per window, used by one frame at a time.</summary>
internal sealed class CanvasFrameRenderer(EditorSession session, CanvasController controller) : ICanvasRenderer
{
    private readonly DocumentCompositor compositor = new();
    private readonly CanvasRenderer renderer = new();
    private readonly Lock gate = new();

    public ICanvasFrame CaptureFrame(int pixelWidth, int pixelHeight)
    {
        var settings = session.Environment.Settings;
        Vector4 pasteboard = Preferences.CanvasBackgroundGrey(settings) is { } grey
            ? LinearColor.FromSRGB(grey, grey, grey)
            // "Match System" is Glimmer's own near-black, the same as the web app.
            : LinearColor.FromSRGB(Preferences.ThemeBackground.R, Preferences.ThemeBackground.G, Preferences.ThemeBackground.B);
        return new Frame(this, session.State, session.Overrides, controller.Transform, controller.BackingScale, session.ChannelMask,
                         pasteboard, Preferences.CheckerSize(settings), pixelWidth, pixelHeight);
    }

    private sealed class Frame(CanvasFrameRenderer owner, DocumentState state, LayerOverrides overrides, ViewTransform transform,
                               double backingScale, int channelMask, Vector4 pasteboard, double checkerSize, int pixelWidth, int pixelHeight)
        : ICanvasFrame
    {
        public int PixelWidth => pixelWidth;
        public int PixelHeight => pixelHeight;

        public bool Render(Memory<byte> target, int stride, CancellationToken cancellation)
        {
            var viewSize = new Vec2(pixelWidth / backingScale, pixelHeight / backingScale);
            var tiles = CanvasController.VisibleTiles(transform, viewSize, state.Bounds);
            lock (owner.gate)
            {
                cancellation.ThrowIfCancellationRequested();
                owner.compositor.Purge(state.Bounds);
                // Composite progressively so huge documents stay responsive.
                bool more = owner.compositor.Update(state, overrides, tiles, budget: 96,
                                                    detailLevel: DocumentCompositor.DetailLevel(transform.Scale * backingScale));
                var frame = new CanvasFrame(transform, viewSize, backingScale, state.Size, pasteboard, checkerSize);
                // The app owns the buffer (a WriteableBitmap's back buffer or a pooled array): pinned while it's drawn.
                using var pin = target.Pin();
                // No selection: the app draws the marching ants from SelectionOutline.
                owner.renderer.Encode(frame, Drawable(pin, pixelWidth, pixelHeight, stride), tiles, owner.compositor.Texture,
                                      selection: null, time: 0, channelMask: channelMask);
                return more;
            }
        }

        private static unsafe CanvasTarget Drawable(System.Buffers.MemoryHandle pin, int width, int height, int stride) =>
            new((nint)pin.Pointer, width, height, stride);
    }
}
