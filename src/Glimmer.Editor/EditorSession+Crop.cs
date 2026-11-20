// Ported from App/EditorSession+Crop.swift (and the Resize action of App/CanvasSizeSheet.swift)
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>Crop while it is open (EditorSession.CropSession).</summary>
public sealed record CropSession : ICropSession
{
    public CropSession(CGRectLike rect) => Rect = rect;

    public CGRectLike Rect { get; init; }
    /// <summary>width / height, null = free.</summary>
    public double? Aspect { get; init; }
    /// <summary>0…7 handles, 8 = move, 9 = new rect.</summary>
    public int? Dragging { get; init; }
    public Vec2 Anchor { get; init; }
    /// <summary>Straighten angle (radians). Rect lives in a frame rotated by Angle about Pivot.</summary>
    public double Angle { get; init; }
    public Vec2 Pivot { get; init; }
    /// <summary>Box size before straightening (the auto-fit scales from this).</summary>
    public Vec2? BaseSize { get; init; }
    /// <summary>width / height when the drag began (⇧ keeps it).</summary>
    public double DragAspect { get; init; } = 1.0;

    public Vec2 Local(Vec2 doc) => Rotate(doc - Pivot, -Angle) + Pivot;
    public Vec2 World(Vec2 local) => Rotate(local - Pivot, Angle) + Pivot;

    public IReadOnlyList<Vec2> Corners =>
        [World(new Vec2(Rect.MinX, Rect.MinY)), World(new Vec2(Rect.MaxX, Rect.MinY)), World(new Vec2(Rect.MaxX, Rect.MaxY)), World(new Vec2(Rect.MinX, Rect.MaxY))];

    public static Vec2 Rotate(Vec2 v, double a) => new(Math.Cos(a) * v.X - Math.Sin(a) * v.Y, Math.Sin(a) * v.X + Math.Cos(a) * v.Y);
}

public enum CanvasFlip { Horizontal, Vertical, RotateRight, RotateLeft, Rotate180 }

public sealed partial class EditorSession
{
    // MARK: Crop

    private CropSession? cropSession;
    public CropSession? CropSession => cropSession;
    ICropSession? IEditorSession.CropSession => cropSession;

    internal CropSession? CropSessionValue
    {
        get => cropSession;
        set
        {
            cropSession = value;
            Raise(nameof(CropSession));
        }
    }

    /// <summary>Straighten: rotates the crop box about its centre (degrees, −45…45).</summary>
    public void SetCropAngle(double degrees)
    {
        if (cropSession is not { } c) return;
        var centre = c.World(new Vec2((c.Rect.MinX + c.Rect.MaxX) / 2, (c.Rect.MinY + c.Rect.MaxY) / 2));
        if (Math.Abs(c.Angle) < 1e-9 || c.BaseSize is null) c = c with { BaseSize = new Vec2(c.Rect.Width, c.Rect.Height) };
        var b = c.BaseSize!.Value;
        double a = degrees * Math.PI / 180;
        // Largest box of the original proportions that stays inside the image at this angle.
        double ca = Math.Abs(Math.Cos(a)), sa = Math.Abs(Math.Sin(a));
        double halfW = Math.Min(centre.X, State.Width - centre.X), halfH = Math.Min(centre.Y, State.Height - centre.Y);
        double k = Math.Min(1, Math.Min(2 * halfW / Math.Max(b.X * ca + b.Y * sa, 1e-9), 2 * halfH / Math.Max(b.X * sa + b.Y * ca, 1e-9)));
        double w = b.X * Math.Max(k, 0.01), h = b.Y * Math.Max(k, 0.01);
        CropSessionValue = c with
        {
            Pivot = centre,
            Rect = new CGRectLike(centre.X - w / 2, centre.Y - h / 2, centre.X + w / 2, centre.Y + h / 2),
            Angle = a,
        };
    }

    public void BeginCrop()
    {
        CommitLiveEdit();
        CropSessionValue = new CropSession(new CGRectLike(0, 0, State.Width, State.Height));
    }

    /// <summary>Cancel in the options bar and Esc on the canvas.</summary>
    public void CancelCrop() => CropSessionValue = null;

    public void CropMouseDown(Vec2 docPoint, double viewTolerance)
    {
        if (cropSession is not { } c) return;
        var doc = c.Local(docPoint);
        double tol = viewTolerance / Math.Max(Canvas.Transform.Scale, 0.001);
        var r = c.Rect;
        var handles = CropHandles(r);
        c = c with { DragAspect = r.Height > 0 ? r.Width / r.Height : 1 };
        // A crop that still covers the whole image has nothing to move: dragging inside draws a new box.
        bool isWholeImage = c.Angle == 0 && r.MinX <= 0.5 && r.MinY <= 0.5 && r.MaxX >= State.Width - 0.5 && r.MaxY >= State.Height - 0.5;
        int hit = -1;
        for (int i = 0; i < handles.Count; i++) if (EditorHelpers.Distance(handles[i], doc) < tol) { hit = i; break; }
        if (hit >= 0) c = c with { Dragging = hit };
        else if (!isWholeImage && doc.X > r.MinX && doc.X < r.MaxX && doc.Y > r.MinY && doc.Y < r.MaxY) c = c with { Dragging = 8, Anchor = doc };
        else c = c with { Dragging = 9, Anchor = doc, Rect = new CGRectLike(doc.X, doc.Y, doc.X, doc.Y) };
        CropSessionValue = c;
    }

    public IReadOnlyList<Vec2> CropHandles(CGRectLike r)
    {
        double mx = (r.MinX + r.MaxX) / 2, my = (r.MinY + r.MaxY) / 2;
        return [new(r.MinX, r.MinY), new(mx, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, my),
                new(r.MaxX, r.MaxY), new(mx, r.MaxY), new(r.MinX, r.MaxY), new(r.MinX, my)];
    }

    /// <summary>Ends a crop drag. A click or a tiny new box puts the crop back on the whole image.</summary>
    public void CropMouseUp()
    {
        if (cropSession is not { } c) return;
        double tiny = 8 / Math.Max(Canvas.Transform.Scale, 0.001);
        if (c.Dragging == 9 && (c.Rect.Width < tiny || c.Rect.Height < tiny))
            c = c with { Rect = new CGRectLike(0, 0, State.Width, State.Height), Angle = 0, BaseSize = null };
        CropSessionValue = c with { Dragging = null };
    }

    public void CropDrag(Vec2 docPoint, bool fromCentre = false, bool constrain = false)
    {
        if (cropSession is not { } c || c.Dragging is not { } handle) return;
        var doc = c.Local(docPoint);
        var r = c.Rect;
        switch (handle)
        {
            case 8:
                {
                    var d = doc - c.Anchor;
                    r = new CGRectLike(r.MinX + d.X, r.MinY + d.Y, r.MaxX + d.X, r.MaxY + d.Y);
                    c = c with { Anchor = doc };
                    break;
                }
            case 9:
                r = fromCentre
                    ? new CGRectLike(2 * c.Anchor.X - doc.X, 2 * c.Anchor.Y - doc.Y, doc.X, doc.Y)
                    : new CGRectLike(c.Anchor.X, c.Anchor.Y, doc.X, doc.Y);
                break;
            default:
                {
                    double x0 = r.MinX, y0 = r.MinY, x1 = r.MaxX, y1 = r.MaxY;
                    if (handle is 0 or 6 or 7) x0 = doc.X;
                    if (handle is 2 or 3 or 4) x1 = doc.X;
                    if (handle is 0 or 1 or 2) y0 = doc.Y;
                    if (handle is 4 or 5 or 6) y1 = doc.Y;
                    if (fromCentre)
                    {
                        // ⌥: the opposite edge mirrors the dragged one around the centre.
                        double cx = (r.MinX + r.MaxX) / 2, cy = (r.MinY + r.MaxY) / 2;
                        if (handle is 0 or 6 or 7) x1 = 2 * cx - x0;
                        if (handle is 2 or 3 or 4) x0 = 2 * cx - x1;
                        if (handle is 0 or 1 or 2) y1 = 2 * cy - y0;
                        if (handle is 4 or 5 or 6) y0 = 2 * cy - y1;
                    }
                    r = new CGRectLike(x0, y0, x1, y1);
                    break;
                }
        }
        // ⇧: a square for a new box, the starting proportions when resizing.
        double? aspect = c.Aspect ?? (constrain ? (handle == 9 ? 1 : c.DragAspect) : null);
        if (aspect is { } ratio && handle != 8 && r.Width > 0)
        {
            // Keep the ratio by adjusting the height around the dragged edge.
            double h = r.Width / ratio;
            r = handle is 0 or 1 or 2 || (handle == 9 && doc.Y < c.Anchor.Y)
                ? new CGRectLike(r.MinX, r.MaxY - h, r.MaxX, r.MaxY)
                : new CGRectLike(r.MinX, r.MinY, r.MaxX, r.MinY + h);
        }
        CropSessionValue = c with { Rect = r, BaseSize = null };
    }

    public void SetCropAspect(double? aspect)
    {
        if (cropSession is not { } c) return;
        c = c with { Aspect = aspect };
        if (aspect is { } a)
        {
            var r = c.Rect;
            double h = r.Width / a;
            c = c with { Rect = new CGRectLike(r.MinX, (r.MinY + r.MaxY) / 2 - h / 2, r.MaxX, (r.MinY + r.MaxY) / 2 + h / 2) };
        }
        CropSessionValue = c;
    }

    /// <summary>Resizes the canvas to the crop rectangle. Every layer, mask, path and guide shifts; pixels outside go
    /// when CropDeletesPixels is on.</summary>
    public void ApplyCrop()
    {
        if (cropSession is not { } c) return;
        if (Math.Abs(c.Angle) > 1e-6)
        {
            ApplyRotatedCrop(c);
            return;
        }
        CropSessionValue = null;
        int x = (int)c.Rect.MinX.Rounded(), y = (int)c.Rect.MinY.Rounded();
        int w = (int)c.Rect.Width.Rounded(), h = (int)c.Rect.Height.Rounded();
        if (w < 1 || h < 1) return;
        try
        {
            CropCanvas(new PixelRect(x, y, w, h), cropDeletesPixels);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Resizes the canvas to <paramref name="rect"/> (the Crop tool's box, Canvas Size, or an agent's crop step).</summary>
    public void CropCanvas(PixelRect rect, bool deletePixels, UiText? actionName = null)
    {
        int x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        var offset = (-x, -y);
        var layers = State.Layers.Select(l => TileOperations.Translate(l, offset)).ToList();
        if (deletePixels) layers = Trimmed(layers, new PixelRect(0, 0, w, h), new PixelOps());
        // Keep the zoom and centre the new canvas, as the web app does.
        if (w != State.Width || h != State.Height) Canvas.KeepZoomOnResize = true;
        var d = new Vec2(-x, -y);
        Edit(actionName ?? "Crop", state => state with
        {
            Layers = [.. layers],
            Width = w,
            Height = h,
            Selection = null,
            Paths = [.. state.Paths.Select(path => path with
            {
                Subpaths = [.. path.Subpaths.Select(sub => sub with
                {
                    Anchors = [.. sub.Anchors.Select(a => new PathAnchor(a.Point + d, a.HandleIn + d, a.HandleOut + d))],
                })],
            })],
            Aids = state.Aids with
            {
                Guides = [.. state.Aids.Guides.Select(g => g with { Position = g.Position + (g.Axis == GuideAxis.Vertical ? d.X : d.Y) })],
            },
        });
    }

    /// <summary>Pixel layers (and masks) without what lies outside <paramref name="rect"/>.</summary>
    private List<Layer> Trimmed(IEnumerable<Layer> layers, PixelRect rect, PixelOps ops)
    {
        var keep = Selection.Rectangle(new CGRectLike(rect.X, rect.Y, rect.MaxX, rect.MaxY));
        TileGrid Trim(TileGrid grid, bool clearPartial)
        {
            if (grid.TileBounds is not { } tb) return grid;
            var output = new TileGrid.Builder();
            var partial = new List<TileCoord>();
            foreach (var c in tb.Intersection(rect).TileCoords)
            {
                if (grid[c] is not { } tile) continue;
                var r = c.Rect;
                if (r.X >= rect.X && r.Y >= rect.Y && r.MaxX <= rect.MaxX && r.MaxY <= rect.MaxY) output[c] = tile;
                else partial.Add(c);
            }
            if (partial.Count == 0) return output.ToImmutable();
            if (clearPartial)
            {
                var cut = ops.Run(PixelOps.Mode.KeepSelected, grid, selection: keep, coords: partial);
                foreach (var c in partial) if (cut[c] is { } t) output[c] = t;
            }
            else
            {
                foreach (var c in partial) output[c] = grid[c];
            }
            return output.ToImmutable();
        }
        return layers.Select(layer =>
        {
            var l = layer;
            switch (layer.Content)
            {
                case RasterContent r: l = l with { Content = new RasterContent(Trim(r.Tiles, clearPartial: true)) }; break;
                case GroupContent g: l = l with { Content = new GroupContent(g.Group with { Children = [.. Trimmed(g.Group.Children, rect, ops)] }) }; break;
            }
            // Mask tiles off the canvas go; partly covered ones stay (the pixels under them are gone anyway).
            if (l.Mask is { } m) l = l with { Mask = m with { Tiles = Trim(m.Tiles, clearPartial: false) } };
            return l;
        }).ToList();
    }

    /// <summary>Straightened crop: every layer is resampled into the rotated box. Pixels outside the box are dropped (they'd
    /// no longer line up with the canvas). Text layers are rasterized; smart objects and gradients keep their parameters.</summary>
    public void ApplyRotatedCrop(CropSession c)
    {
        CropSessionValue = null;
        int w = (int)c.Rect.Width.Rounded(), h = (int)c.Rect.Height.Rounded();
        if (w < 1 || h < 1) return;
        var centre = c.World(new Vec2((c.Rect.MinX + c.Rect.MaxX) / 2, (c.Rect.MinY + c.Rect.MaxY) / 2));
        // p' = R(−angle)(p − centre) + size / 2
        double phi = -c.Angle;
        double cs = Math.Cos(phi), sn = Math.Sin(phi);
        var t = new Vec2(w / 2.0, h / 2.0) - new Vec2(cs * centre.X - sn * centre.Y, sn * centre.X + cs * centre.Y);
        var rot = new AffineTransform(a: cs, b: sn, c: -sn, d: cs, tx: t.X, ty: t.Y);
        TransformCanvas(rot, w, h, "Crop");
    }

    /// <summary>Flips or rotates the whole image (all layers, masks, paths).</summary>
    public void FlipCanvas(CanvasFlip kind)
    {
        CommitLiveEdit();
        double w = State.Width, h = State.Height;
        var (t, nw, nh, name) = kind switch
        {
            CanvasFlip.Horizontal => (new AffineTransform(a: -1, d: 1, tx: w), State.Width, State.Height, "Flip Horizontal"),
            CanvasFlip.Vertical => (new AffineTransform(a: 1, d: -1, ty: h), State.Width, State.Height, "Flip Vertical"),
            // (x, y) → (h − y, x): a quarter turn clockwise on screen (y down).
            CanvasFlip.RotateRight => (new AffineTransform(a: 0, b: 1, c: -1, d: 0, tx: h), State.Height, State.Width, "Rotate 90° Clockwise"),
            CanvasFlip.RotateLeft => (new AffineTransform(a: 0, b: -1, c: 1, d: 0, ty: w), State.Height, State.Width, "Rotate 90° Counterclockwise"),
            _ => (new AffineTransform(a: -1, d: -1, tx: w, ty: h), State.Width, State.Height, "Rotate 180°"),
        };
        TransformCanvas(t, nw, nh, name);
    }

    /// <summary>Resamples every layer through <paramref name="rot"/> into a canvas of w × h. Pixels outside it are
    /// dropped; text layers are rasterized.</summary>
    public void TransformCanvas(AffineTransform rot, int width, int height, UiText actionName)
    {
        try
        {
            TransformCanvas(rot, width, height, actionName, scaling: false);
            Canvas.Perform(CommandID.FitToWindow);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Image Size: every layer scaled to a canvas of width × height. Text and smart objects stay editable, and
    /// guides are scaled along. Throws when the size is refused (the Image Size sheet's checks).</summary>
    public void ResizeImage(int width, int height)
    {
        CheckCanvasSize(width, height);
        if (width == State.Width && height == State.Height) return;
        CommitLiveEdit();
        var scale = new AffineTransform(a: width / (double)State.Width, d: height / (double)State.Height);
        TransformCanvas(scale, width, height, "Image Size", scaling: true);
        Canvas.Perform(CommandID.FitToWindow);
    }

    /// <summary>Canvas Size…: the image stays where the anchor is ((column, row), 0-2 each). New room is transparent; what
    /// a smaller canvas cuts off is kept outside it. Throws when the size is refused.</summary>
    public void ResizeCanvas(int width, int height, int anchorX, int anchorY)
    {
        CheckCanvasSize(width, height);
        var box = CanvasSteps.CanvasBox((State.Width, State.Height), (width, height), (anchorX, anchorY));
        if (width != State.Width || height != State.Height) CropCanvas(box, deletePixels: false, actionName: "Canvas Size");
    }

    /// <summary>The checks of the Image Size and Canvas Size sheets, as an error.</summary>
    private static void CheckCanvasSize(int width, int height)
    {
        if (width < 1 || height < 1 || width > CanvasSteps.MaxSide || height > CanvasSteps.MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), $"Each side must be 1 to {CanvasSteps.MaxSide} pixels.");
        if (DocumentLimits.Refusal(width, height) is { } tooLarge) throw new InvalidOperationException(tooLarge);
    }

    /// <summary><paramref name="scaling"/>: rot only scales, so text keeps its letters (the transform is stored with it),
    /// guides move along and layer styles scale with the image.</summary>
    private void TransformCanvas(AffineTransform rot, int w, int h, UiText actionName, bool scaling)
    {
        var limit = new PixelRect(0, 0, w, h);
        var docBounds = State.Bounds;
        var renderer = new SmartObjectRenderer();
        TileGrid Resample(TileGrid grid, float? fillTo = null)
        {
            if (fillTo is { } v)
            {
                // Materialize a mask's default inside the old canvas.
                var b = grid.ToBuilder();
                foreach (var coord in docBounds.TileCoords) if (b[coord] is null) b[coord] = Tile.Solid(new System.Numerics.Vector4(v));
                grid = b.ToImmutable();
            }
            if (grid.TileBounds is not { } tb) return TileGrid.Empty;
            var source = TileOperations.Translate(grid, (-tb.X, -tb.Y));
            var m = Concat(rot, new AffineTransform(tx: tb.X, ty: tb.Y));
            return renderer.Render(new SmartObject(source, tb.Width, tb.Height, m), limit);
        }
        Layer RotateLayer(Layer layer)
        {
            var l = layer;
            if (scaling) l = l with { Effects = layer.Effects.Scaled(Math.Sqrt(Math.Abs(rot.A) * Math.Abs(rot.D))) };
            if (layer.VectorMask is { } path)
            {
                var p = path.Applying(rot);
                l = l with
                {
                    VectorMask = p,
                    Mask = p.AsSelection.AsMask with
                    {
                        IsEnabled = layer.Mask?.IsEnabled ?? true, Density = layer.Mask?.Density ?? 1, Feather = layer.Mask?.Feather ?? 0,
                    },
                };
            }
            else if (layer.Mask is { } mask)
            {
                l = l with { Mask = mask with { Tiles = Resample(mask.Tiles, mask.DefaultValue > 0 ? mask.DefaultValue : null) } };
            }
            switch (layer.Content)
            {
                case RasterContent r:
                    l = l with { Content = new RasterContent(Resample(r.Tiles)) };
                    break;
                case TextContent t when scaling:
                    l = l with { Content = new TextContent(t.Text with { Transform = Concat(rot, t.Text.Transform ?? AffineTransform.Identity) }) };
                    break;
                case TextContent:
                    {
                        var raster = DocumentCompositor.Rasterize(layer, docBounds, State.BlendSpace);
                        l = l with { Content = new RasterContent(Resample(raster)) };
                        break;
                    }
                case SmartContent s:
                    l = l with { Content = new SmartContent(s.Smart with { Transform = Concat(rot, s.Smart.Transform) }) };
                    break;
                case FillContent { Fill: Fill.Gradient g }:
                    l = l with { Content = new FillContent(new Fill.Gradient(g.Value with { Start = rot.Apply(g.Value.Start), End = rot.Apply(g.Value.End) })) };
                    break;
                case GroupContent g:
                    l = l with { Content = new GroupContent(g.Group with { Children = [.. g.Group.Children.Select(RotateLayer)] }) };
                    break;
            }
            return l;
        }
        var layers = State.Layers.Select(RotateLayer).ToList();
        Edit(actionName, state => state with
        {
            Layers = [.. layers],
            Width = w,
            Height = h,
            Selection = null,
            Aids = state.Aids with
            {
                Guides = !scaling ? [] : [.. state.Aids.Guides.Select(g => g with { Position = g.Position * (g.Axis == GuideAxis.Vertical ? rot.A : rot.D) })],
            },
            Paths = [.. state.Paths.Select(path => path with
            {
                Subpaths = [.. path.Subpaths.Select(sub => sub with
                {
                    Anchors = [.. sub.Anchors.Select(a => new PathAnchor(rot.Apply(a.Point), rot.Apply(a.HandleIn), rot.Apply(a.HandleOut)))],
                })],
            })],
        });
    }

    /// <summary>outer ∘ inner (inner applied first).</summary>
    public static AffineTransform Concat(AffineTransform outer, AffineTransform inner) => outer.Concatenating(inner);
}
