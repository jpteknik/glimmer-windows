// View contract (see ViewContractTypes.cs for the rules every member follows).
// Mirrors App/CanvasController.swift, plus the rendering seam the WPF canvas needs in place of the Mac's
// CAMetalLayer + CanvasRenderer.encode(...).
using System.ComponentModel;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Per-window view state of the canvas: zoom, pan, rotation (CanvasController).</summary>
public interface ICanvasController : INotifyPropertyChanged
{
    /// <summary>Document pixels to view points (DIPs on Windows). Raises PropertyChanged("Transform") and NeedsRender.</summary>
    ViewTransform Transform { get; }
    Vec2 DocumentSize { get; }
    /// <summary>Device pixels per view point: 1.25 at 125 % scaling. The canvas sets it from its DPI.</summary>
    double BackingScale { get; set; }
    Vec2 ViewSize { get; }
    /// <summary>Zoom relative to device pixels: 1 shows one document pixel per screen pixel (Transform.Scale × BackingScale).</summary>
    double Zoom { get; }
    /// <summary>View rotation in degrees, clockwise positive.</summary>
    double RotationDegrees { get; set; }

    /// <summary>Raised on the UI thread whenever the canvas must draw a new frame: the view moved, the document or the
    /// overrides changed, or anything the overlays show changed (Swift: onNeedsRender, plus the session observation in
    /// CanvasNSView.observeSession()).</summary>
    event EventHandler? NeedsRender;
    void RequestRender();

    void ViewDidResize(Vec2 size);
    void Pan(Vec2 delta);
    /// <summary>Swift zoom(by:around:). Keeps the document point under `anchor` (view points) in place.</summary>
    void ZoomBy(double factor, Vec2 anchor);
    void BeginRotationGesture();
    /// <summary>`delta` in radians, clockwise positive. Snaps to right angles.</summary>
    void RotateGesture(double delta, Vec2 anchor);
    void ToggleFitAndActualSize(Vec2 anchor);
    bool CanPerform(CommandID id);
    void Perform(CommandID id);
    Vec2 DocumentPoint(Vec2 viewPoint);

    /// <summary>Draws the document for the canvas surface.</summary>
    ICanvasRenderer Renderer { get; }
}

/// <summary>The engine's canvas renderer as the WPF canvas drives it. The canvas owns a device-pixel bitmap; when it
/// needs a frame it calls CaptureFrame on the UI thread (cheap: it takes the immutable document state, the overrides,
/// the transform, the channel mask and the preferences), then calls Render on a worker thread into a back buffer and
/// copies the result to the screen on the UI thread.</summary>
public interface ICanvasRenderer
{
    /// <summary>UI thread. A snapshot of everything one frame needs, for a target of the given device-pixel size.</summary>
    ICanvasFrame CaptureFrame(int pixelWidth, int pixelHeight);
}

/// <summary>One frame to draw. Must not touch the session: it holds its own immutable snapshot.</summary>
public interface ICanvasFrame
{
    int PixelWidth { get; }
    int PixelHeight { get; }

    /// <summary>Worker thread. Draws the visible part of the document, the transparency checkerboard and the
    /// surround into `target`: 8-bit BGRA, premultiplied, sRGB, device pixels, rows top-down, `stride` bytes apart.
    /// The marching ants are not drawn here (the canvas draws them from IEditorSession.SelectionOutline).
    /// Returns true when compositing is not finished yet (huge documents are composited progressively, as
    /// DocumentCompositor.update(...budget:) did): the canvas then asks for another frame straight away.
    /// Memory rather than Span, so the renderer can split the rows across threads (Parallel.For).</summary>
    bool Render(Memory<byte> target, int stride, CancellationToken cancellation);
}
