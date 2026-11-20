// Ported from App/EditorSession+Transform.swift
using System.Collections.Immutable;
using Glimmer.Core;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

// MARK: Free transform

/// <summary>The box a free transform edits: an unrotated, scaled box at (X, Y), rotated about its centre.</summary>
public readonly record struct TransformBox(double X, double Y, double ScaleX, double ScaleY, double Rotation, Vec2 SourceSize)
{
    public AffineTransform Affine => AffineTransform.Make(X, Y, ScaleX, ScaleY, Rotation, SourceSize);

    public Vec2 Size => new(SourceSize.X * ScaleX, SourceSize.Y * ScaleY);
    public Vec2 Centre => new Vec2(X, Y) + Size / 2;

    public Vec2 ToDoc(Vec2 local)
    {
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        return Centre + new Vec2(c * local.X - s * local.Y, s * local.X + c * local.Y);
    }

    public Vec2 ToLocal(Vec2 doc)
    {
        var v = doc - Centre;
        double c = Math.Cos(Rotation), s = Math.Sin(Rotation);
        return new Vec2(c * v.X + s * v.Y, -s * v.X + c * v.Y);
    }

    /// <summary>Corners (tl, tr, br, bl) in document space.</summary>
    public IReadOnlyList<Vec2> Corners
    {
        get
        {
            var h = Size / 2;
            return [ToDoc(new Vec2(-h.X, -h.Y)), ToDoc(new Vec2(h.X, -h.Y)), ToDoc(new Vec2(h.X, h.Y)), ToDoc(new Vec2(-h.X, h.Y))];
        }
    }

    /// <summary>Edge midpoints (t, r, b, l) in document space.</summary>
    public IReadOnlyList<Vec2> Edges
    {
        get
        {
            var h = Size / 2;
            return [ToDoc(new Vec2(0, -h.Y)), ToDoc(new Vec2(h.X, 0)), ToDoc(new Vec2(0, h.Y)), ToDoc(new Vec2(-h.X, 0))];
        }
    }

    /// <summary>Moves one side so the opposite side stays put. sx/sy are -1, 0 or 1.</summary>
    public TransformBox Drag(double sx, double sy, Vec2 doc, bool proportional)
    {
        var h = Size / 2;
        var p = ToLocal(doc);
        var newHalf = h;
        var shift = Vec2.Zero;
        if (sx != 0)
        {
            double fixedX = -sx * h.X;
            double w = Math.Max(Math.Abs(p.X - fixedX), 1);
            newHalf = newHalf with { X = w / 2 };
            shift = shift with { X = fixedX + sx * w / 2 };
        }
        if (sy != 0)
        {
            double fixedY = -sy * h.Y;
            double hh = Math.Max(Math.Abs(p.Y - fixedY), 1);
            newHalf = newHalf with { Y = hh / 2 };
            shift = shift with { Y = fixedY + sy * hh / 2 };
        }
        if (proportional && sx != 0 && sy != 0)
        {
            double k = Math.Max(newHalf.X / h.X, newHalf.Y / h.Y);
            newHalf = h * k;
            shift = new Vec2(-sx * h.X + sx * newHalf.X, -sy * h.Y + sy * newHalf.Y);
        }
        var newCentre = ToDoc(shift);
        return this with
        {
            ScaleX = newHalf.X * 2 / SourceSize.X,
            ScaleY = newHalf.Y * 2 / SourceSize.Y,
            X = newCentre.X - newHalf.X,
            Y = newCentre.Y - newHalf.Y,
        };
    }
}

/// <summary>What a press on the transform box grabbed (EditorSession.TransformHandle).</summary>
public abstract record TransformHandle
{
    public sealed record Move(Vec2 Start, TransformBox Box) : TransformHandle;
    public sealed record Rotate(double StartAngle, TransformBox Box) : TransformHandle;
    public sealed record Scale(double Sx, double Sy) : TransformHandle;
    /// <summary>Free-corner modes: which corner/edge, where the drag started, the quad then.</summary>
    public sealed record Corner(int Index, Vec2 Start, ImmutableArray<Vec2> Quad) : TransformHandle;
    public sealed record Edge(int Index, Vec2 Start, ImmutableArray<Vec2> Quad) : TransformHandle;
    public sealed record MoveQuad(Vec2 Start, ImmutableArray<Vec2> Quad) : TransformHandle;
}

public static class TransformModeExtensions
{
    public static string Title(this TransformMode mode) => mode switch
    {
        TransformMode.ScaleRotate => "Scale & Rotate",
        TransformMode.Distort => "Distort",
        TransformMode.Skew => "Skew",
        _ => "Perspective",
    };
}

/// <summary>A layer inside a transformed group, as it was when the transform began.</summary>
public abstract record GroupMember
{
    /// <summary>Pixels (text rendered to pixels), translated to their own bounds' origin, which sits at Offset in the
    /// group's source space.</summary>
    public sealed record Pixels(Guid Id, TileGrid Source, (int X, int Y) Size, Vec2 Offset) : GroupMember;
    public sealed record Smart(Guid Id, AffineTransform Original) : GroupMember;
    public sealed record Shape(Guid Id, VectorPath Original) : GroupMember;
    /// <summary>Text keeps its layout and gets the transform, so it stays editable; only a non-affine transform (distort,
    /// perspective) would turn it into pixels.</summary>
    public sealed record Text(Guid Id, TextLayer Original) : GroupMember;
    /// <summary>A layer's pixel mask, which follows the transform so it stays in line with the layer. Cover is where the
    /// layer has content (for the quick preview).</summary>
    public sealed record Mask(Guid Id, LayerMask Original, PixelRect Cover) : GroupMember;
}

/// <summary>Free Transform while it is open (EditorSession.TransformSession).</summary>
public sealed record TransformSession : ITransformSession
{
    public TransformSession(Guid layerID, TileGrid source, bool isSmart, TransformBox box)
    {
        LayerID = layerID;
        Source = source;
        IsSmart = isSmart;
        Box = box;
    }

    public Guid LayerID { get; init; }
    public TileGrid Source { get; init; }
    public bool IsSmart { get; init; }
    public TransformBox Box { get; init; }
    public TransformHandle? Handle { get; init; }
    public AffineTransform? OriginalSmartTransform { get; init; }
    /// <summary>Free corners (top-left, top-right, bottom-right, bottom-left) once the layer has been distorted, skewed or
    /// put in perspective.</summary>
    public ImmutableArray<Vec2>? Quad { get; init; }
    public TransformMode Mode { get; init; } = TransformMode.ScaleRotate;
    /// <summary>Shape layers transform their vector mask (no resampling).</summary>
    public VectorPath? OriginalVector { get; init; }
    /// <summary>A group transforms every layer with content inside it with one box (source space = the union of their
    /// bounds, starting at GroupOrigin).</summary>
    public ImmutableArray<GroupMember> Members { get; init; } = [];
    public Vec2 GroupOrigin { get; init; }

    public IReadOnlyList<Vec2> Corners => Quad is { } q ? q : Box.Corners;

    public IReadOnlyList<Vec2> Edges
    {
        get
        {
            var c = Corners;
            return [.. Enumerable.Range(0, 4).Select(i => (c[i] + c[(i + 1) % 4]) / 2)];
        }
    }

    public AffineTransform Transform => Quad is { } q ? AffineTransform.Quad(Box.SourceSize, q) : Box.Affine;

    public bool Contains(Vec2 p)
    {
        // Convex quad: same side of every edge.
        var c = Corners;
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vec2 a = c[i], b = c[(i + 1) % 4];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            if (cross != 0)
            {
                if (sign == 0) sign = cross;
                else if (cross > 0 != sign > 0) return false;
            }
        }
        return true;
    }

    // ITransformSession
    public bool IsRotating => Handle is TransformHandle.Rotate;
    public double Rotation => Box.Rotation;
    public Vec2 Centre => Box.Centre;
    public double ScaleX => Box.ScaleX;
    public double ScaleY => Box.ScaleY;
    public bool IsQuad => Quad is not null;
}

public sealed partial class EditorSession
{
    private TransformSession? transformSession;
    public TransformSession? TransformSession => transformSession;
    ITransformSession? IEditorSession.TransformSession => transformSession;

    internal TransformSession? TransformSessionValue
    {
        get => transformSession;
        set
        {
            transformSession = value;
            Raise(nameof(TransformSession));
        }
    }

    /// <summary>Free Transform on a group (all its layers with content) or on one text layer: one box around their
    /// content; every pixel, text, shape and smart object layer is transformed with it, as one undo step. Adjustment
    /// layers and plain fills have nothing to place. Layer masks (of any layer inside, adjustment layers and groups
    /// included) go through the same transform.</summary>
    private void BeginMemberTransform(Guid id, IEnumerable<Layer> layers)
    {
        var members = new List<GroupMember>();
        var lo = new Vec2(double.PositiveInfinity, double.PositiveInfinity);
        var hi = -lo;
        void Extend(Vec2 a, Vec2 b)
        {
            lo = Vec2.Min(lo, a);
            hi = Vec2.Max(hi, b);
        }
        var pending = new List<(Guid Id, TileGrid Grid, PixelRect Bounds)>();
        foreach (var layer in layers)
        {
            if (layer.Mask is { } mask && layer.VectorMask is null && !mask.Tiles.IsEmpty)
            {
                var cover = State.Bounds;
                if (layer.Content is RasterContent rc && rc.Tiles.TileBounds is { } rb) cover = rb;
                members.Add(new GroupMember.Mask(layer.Id, mask, cover));
            }
            if (layer.Content is TextContent text)
            {
                var b = text.Text.Bounds;
                Extend(new Vec2(b.X, b.Y), new Vec2(b.X + b.Width, b.Y + b.Height));
                members.Add(new GroupMember.Text(layer.Id, text.Text));
                continue;
            }
            switch (layer.Content)
            {
                case RasterContent r:
                    {
                        if (r.Tiles.TileBounds is not { } tb || ContentBounds(r.Tiles, tb) is not { } b) continue;
                        pending.Add((layer.Id, r.Tiles, b));
                        Extend(new Vec2(b.X, b.Y), new Vec2(b.X + b.Width, b.Y + b.Height));
                        break;
                    }
                case SmartContent s:
                    {
                        var z = s.Smart.SourceSize;
                        var corners = new[] { new Vec2(0, 0), new Vec2(z.X, 0), z, new Vec2(0, z.Y) }.Select(s.Smart.Transform.Apply).ToList();
                        Extend(corners.Aggregate(Vec2.Min), corners.Aggregate(Vec2.Max));
                        members.Add(new GroupMember.Smart(layer.Id, s.Smart.Transform));
                        break;
                    }
                case FillContent when layer.VectorMask is { } path:
                    {
                        if (path.ControlBounds is not { Width: > 0, Height: > 0 } r) continue;
                        Extend(new Vec2(r.MinX, r.MinY), new Vec2(r.MaxX, r.MaxY));
                        members.Add(new GroupMember.Shape(layer.Id, path));
                        break;
                    }
            }
        }
        // The group's own mask follows too.
        if (State.LayerById(id) is { IsGroup: true } group && group.Mask is { } groupMask && group.VectorMask is null && !groupMask.Tiles.IsEmpty)
            members.Add(new GroupMember.Mask(id, groupMask, State.Bounds));
        if (!(lo.X < hi.X && lo.Y < hi.Y))
        {
            ErrorMessage = "This group has nothing Free Transform can move.";
            return;
        }
        foreach (var (pid, grid, b) in pending)
        {
            var source = TileOperations.Translate(grid, (-b.X, -b.Y));
            members.Add(new GroupMember.Pixels(pid, source, (b.Width, b.Height), new Vec2(b.X, b.Y) - lo));
        }
        TransformSessionValue = new TransformSession(id, TileGrid.Empty, false, new TransformBox(lo.X, lo.Y, 1, 1, 0, hi - lo))
        {
            Members = [.. members],
            GroupOrigin = lo,
        };
        originalShapeState = State;
        UpdateTransformPreview();
    }

    /// <summary>Places every layer of a transformed group for the current box.</summary>
    private void PreviewGroup(TransformSession t)
    {
        var toGroup = new AffineTransform(tx: -t.GroupOrigin.X, ty: -t.GroupOrigin.Y);
        var docToDoc = t.Transform.Concatenating(toGroup);   // old document position → new
        var pixels = new Dictionary<Guid, TileGrid>();
        var masks = new Dictionary<Guid, TileGrid>();
        var renderer = new SmartObjectRenderer();
        bool affine = docToDoc.IsAffine;
        var limit = RegionOpsPublic.Expand(State.Bounds, 512);
        foreach (var member in t.Members)
        {
            switch (member)
            {
                case GroupMember.Text(var id, var original):
                    if (affine)
                    {
                        var text = original with { Transform = docToDoc.Concatenating(original.Transform ?? AffineTransform.Identity) };
                        Document.State = State.UpdateLayer(id, l => l with { Content = new TextContent(text) });
                    }
                    else
                    {
                        // Distort or perspective: shown as the pixels it would become.
                        Document.State = State.UpdateLayer(id, l => l with { Content = new TextContent(original) });
                        var b = original.Bounds;
                        var source = TileOperations.Translate(DocumentCompositor.Rasterize(original), (-b.X, -b.Y));
                        var smart = new SmartObject(source, b.Width, b.Height, docToDoc.Concatenating(new AffineTransform(tx: b.X, ty: b.Y)));
                        pixels[id] = renderer.Render(smart, limit);
                    }
                    break;
                case GroupMember.Pixels(var id, var source, var size, var offset):
                    pixels[id] = renderer.Render(new SmartObject(source, size.X, size.Y,
                                                                 t.Transform.Concatenating(new AffineTransform(tx: offset.X, ty: offset.Y))), limit);
                    break;
                case GroupMember.Smart(var id, var original):
                    Document.State = State.UpdateLayer(id, l => l.Content is SmartContent sc
                        ? l with { Content = new SmartContent(sc.Smart with { Transform = docToDoc.Concatenating(original) }) }
                        : l);
                    break;
                case GroupMember.Shape(var id, var original):
                    {
                        var path = original.Applying(docToDoc);
                        Document.State = State.UpdateLayer(id, l => WithVectorMask(l, path));
                        break;
                    }
                case GroupMember.Mask(var id, var original, var cover):
                    masks[id] = MaskTransform.Apply(original, docToDoc, limit, exact: false, cover: cover);
                    break;
            }
        }
        Overrides = new LayerOverrides(pixels: pixels.ToImmutableDictionary(), masks: masks.ToImmutableDictionary());
    }

    /// <summary>A shape layer with a new path: the mask is rasterized from it, keeping the mask's settings.</summary>
    internal static Layer WithVectorMask(Layer l, VectorPath path) => l with
    {
        VectorMask = path,
        Mask = path.AsSelection.AsMask with { IsEnabled = l.Mask?.IsEnabled ?? true, Density = l.Mask?.Density ?? 1, Feather = l.Mask?.Feather ?? 0 },
    };

    public void BeginTransform()
    {
        CommitLiveEdit();
        if (ActiveLayer is not { } layer) return;
        try
        {
            if (layer.IsGroup)
            {
                BeginMemberTransform(layer.Id, layer.Subtree.Skip(1));
                return;
            }
            if (layer.IsText)
            {
                // Text keeps its layout and gets the transform: it stays editable.
                BeginMemberTransform(layer.Id, [layer]);
                return;
            }
            if (layer.Mask is { } mask && layer.VectorMask is null && !mask.Tiles.IsEmpty && layer.Content is RasterContent)
            {
                // A pixel layer with a layer mask: the mask goes through the transform too.
                BeginMemberTransform(layer.Id, [layer]);
                return;
            }
            switch (layer.Content)
            {
                case RasterContent r:
                    {
                        if (r.Tiles.TileBounds is not { } tb || ContentBounds(r.Tiles, tb) is not { } bounds) return;
                        var source = TileOperations.Translate(r.Tiles, (-bounds.X, -bounds.Y));
                        TransformSessionValue = new TransformSession(layer.Id, source, false,
                            new TransformBox(bounds.X, bounds.Y, 1, 1, 0, new Vec2(bounds.Width, bounds.Height)));
                        break;
                    }
                case SmartContent s:
                    {
                        var d = s.Smart.Transform.Decompose(s.Smart.SourceSize);
                        var session = new TransformSession(layer.Id, s.Smart.Source, true,
                            new TransformBox(d.X, d.Y, d.ScaleX, d.ScaleY, d.Rotation, s.Smart.SourceSize)) { OriginalSmartTransform = s.Smart.Transform };
                        if (!s.Smart.Transform.IsAffine)
                        {
                            var z = s.Smart.SourceSize;
                            session = session with
                            {
                                Quad = [.. new[] { new Vec2(0, 0), new Vec2(z.X, 0), z, new Vec2(0, z.Y) }.Select(s.Smart.Transform.Apply)],
                                Mode = TransformMode.Distort,
                            };
                        }
                        TransformSessionValue = session;
                        break;
                    }
                case FillContent when layer.VectorMask is { } path:
                    {
                        if (path.ControlBounds is not { Width: > 0, Height: > 0 } r) return;
                        TransformSessionValue = new TransformSession(layer.Id, TileGrid.Empty, false,
                            new TransformBox(r.MinX, r.MinY, 1, 1, 0, new Vec2(r.Width, r.Height))) { OriginalVector = path };
                        originalShapeState = State;
                        break;
                    }
                default:
                    ErrorMessage = "Free Transform works on pixel, text and shape layers, smart objects and groups.";
                    return;
            }
            UpdateTransformPreview();
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Tight bounds of non-transparent pixels (tile bounds refined by reading edge tiles).</summary>
    public PixelRect? ContentBounds(TileGrid grid, PixelRect within) => new Selection(grid).Bounds(within);

    public void UpdateTransformPreview()
    {
        if (transformSession is not { } t) return;
        if (!t.Members.IsEmpty)
        {
            try { PreviewGroup(t); }
            catch (Exception error) { Report(error); }
            return;
        }
        if (t.OriginalVector is { } original && original.ControlBounds is { } r)
        {
            // Source space = the path's bounds at the origin.
            var toSource = new AffineTransform(tx: -r.MinX, ty: -r.MinY);
            var path = original.Applying(t.Transform.Concatenating(toSource));
            Document.State = State.UpdateLayer(t.LayerID, l => WithVectorMask(l, path));
            return;
        }
        try
        {
            var smart = new SmartObject(t.Source, (int)t.Box.SourceSize.X, (int)t.Box.SourceSize.Y, t.Transform);
            var grid = new SmartObjectRenderer().Render(smart, RegionOpsPublic.Expand(State.Bounds, 512));
            Overrides = new LayerOverrides(pixels: [new(t.LayerID, grid)]);
            if (t.IsSmart)
            {
                // Smart objects preview through their own transform.
                Overrides = new LayerOverrides();
                Document.State = State.UpdateLayer(t.LayerID, l => l.Content is SmartContent sc
                    ? l with { Content = new SmartContent(sc.Smart with { Transform = t.Transform }) }
                    : l);
            }
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Text layers a commit would turn into pixels (a distort or perspective box).</summary>
    public int TextLayersToRasterize
    {
        get
        {
            if (transformSession is not { } t) return 0;
            var toGroup = new AffineTransform(tx: -t.GroupOrigin.X, ty: -t.GroupOrigin.Y);
            if (t.Transform.Concatenating(toGroup).IsAffine) return 0;
            return t.Members.Count(m => m is GroupMember.Text);
        }
    }

    /// <summary><paramref name="confirmed"/>: the user agreed to text becoming pixels (asked first, see RasterizeTextConfirmation).</summary>
    public void CommitTransform(bool confirmed = false)
    {
        if (transformSession is not { } t) return;
        if (!confirmed && TextLayersToRasterize > 0)
        {
            RasterizeTextConfirmation = TextLayersToRasterize;   // asks; the box stays until then
            return;
        }
        TransformSessionValue = null;
        if (!t.Members.IsEmpty && originalShapeState is { } before)
        {
            // Smart objects, shapes and editable text are already placed; pixels come from the preview.
            originalShapeState = null;
            foreach (var (id, grid) in overrides.Pixels) Document.State = State.UpdateLayer(id, l => l with { Content = new RasterContent(grid) });
            // Masks: the exact version (a reveal-all mask stays white outside what moved).
            var docToDoc = t.Transform.Concatenating(new AffineTransform(tx: -t.GroupOrigin.X, ty: -t.GroupOrigin.Y));
            foreach (var member in t.Members)
            {
                if (member is not GroupMember.Mask(var id, var original, _)) continue;
                try
                {
                    var tiles = MaskTransform.Apply(original, docToDoc, RegionOpsPublic.Expand(State.Bounds, 512));
                    Document.State = State.UpdateLayer(id, l => l.Mask is { } m ? l with { Mask = m with { Tiles = tiles } } : l);
                }
                catch (Exception error)
                {
                    Report(error);
                }
            }
            Overrides = LayerOverrides.None;
            Document.CommitInteractiveEdit("Free Transform", before);
            return;
        }
        if (t.OriginalVector is not null && originalShapeState is { } shapeBefore)
        {
            originalShapeState = null;
            Document.CommitInteractiveEdit("Free Transform", shapeBefore);
            return;
        }
        if (t.IsSmart)
        {
            // Already applied live; record a single undo step from the original transform.
            var current = State;
            var before2 = State.UpdateLayer(t.LayerID, l => l.Content is SmartContent sc && t.OriginalSmartTransform is { } o
                ? l with { Content = new SmartContent(sc.Smart with { Transform = o }) }
                : l);
            Document.State = current;
            Document.CommitInteractiveEdit("Free Transform", before2);
        }
        else if (overrides.Pixels.TryGetValue(t.LayerID, out var grid))
        {
            Edit("Free Transform", s => s.UpdateLayer(t.LayerID, l => l with { Tiles = grid }));
        }
        Overrides = LayerOverrides.None;
    }

    public void CancelTransform()
    {
        if (transformSession is not { } t) return;
        TransformSessionValue = null;
        if (!t.Members.IsEmpty && originalShapeState is { } before)
        {
            originalShapeState = null;
            Document.State = before;
            Overrides = LayerOverrides.None;
            return;
        }
        if (t.OriginalVector is not null && originalShapeState is { } shapeBefore)
        {
            originalShapeState = null;
            Document.State = shapeBefore;
            return;
        }
        if (t.IsSmart && t.OriginalSmartTransform is { } original)
        {
            Document.State = State.UpdateLayer(t.LayerID, l => l.Content is SmartContent sc
                ? l with { Content = new SmartContent(sc.Smart with { Transform = original }) }
                : l);
        }
        Overrides = LayerOverrides.None;
    }

    public void TransformMouseDown(Vec2 doc, double viewTolerance, bool command = false)
    {
        if (transformSession is not { } t) return;
        double tol = viewTolerance / Math.Max(Canvas.Transform.Scale, 0.001);
        // ⌘ on a corner distorts, like Photoshop, from any mode.
        var mode = command && t.Mode == TransformMode.ScaleRotate ? TransformMode.Distort : t.Mode;
        if (mode != TransformMode.ScaleRotate || t.Quad is not null)
        {
            ImmutableArray<Vec2> quad = [.. t.Corners];
            int corner = IndexNear(quad, doc, tol);
            int edge = corner < 0 ? IndexNear(t.Edges, doc, tol) : -1;
            if (corner >= 0)
            {
                // Skewing from a corner moves its top/bottom edge.
                TransformHandle handle = mode == TransformMode.Skew
                    ? new TransformHandle.Edge(corner < 2 ? 0 : 2, doc, quad)
                    : new TransformHandle.Corner(corner, doc, quad);
                t = t with { Quad = quad, Handle = handle, Mode = mode };
            }
            else if (edge >= 0)
            {
                t = t with { Quad = quad, Handle = new TransformHandle.Edge(edge, doc, quad), Mode = mode };
            }
            else if (t.Contains(doc))
            {
                var q = t.Quad ?? (mode == TransformMode.ScaleRotate ? null : quad);
                t = t with { Quad = q, Handle = q is null ? new TransformHandle.Move(doc, t.Box) : new TransformHandle.MoveQuad(doc, quad) };
            }
            else if (t.Quad is null && NearCorner(doc, t.Corners))
            {
                var v = doc - t.Box.Centre;
                t = t with { Handle = new TransformHandle.Rotate(Math.Atan2(v.Y, v.X), t.Box) };
            }
            else
            {
                ApplyTransformFromClick();
                return;
            }
            TransformSessionValue = t;
            return;
        }
        (double, double)[] signs = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
        (double, double)[] edgeSigns = [(0, -1), (1, 0), (0, 1), (-1, 0)];
        int ci = IndexNear(t.Box.Corners, doc, tol);
        int ei = ci < 0 ? IndexNear(t.Box.Edges, doc, tol) : -1;
        if (ci >= 0)
        {
            t = t with { Handle = new TransformHandle.Scale(signs[ci].Item1, signs[ci].Item2) };
        }
        else if (ei >= 0)
        {
            t = t with { Handle = new TransformHandle.Scale(edgeSigns[ei].Item1, edgeSigns[ei].Item2) };
        }
        else
        {
            var local = t.Box.ToLocal(doc);
            var h = t.Box.Size / 2;
            if (Math.Abs(local.X) <= h.X && Math.Abs(local.Y) <= h.Y)
            {
                t = t with { Handle = new TransformHandle.Move(doc, t.Box) };
            }
            else if (NearCorner(doc, t.Box.Corners))
            {
                var v = doc - t.Box.Centre;
                t = t with { Handle = new TransformHandle.Rotate(Math.Atan2(v.Y, v.X), t.Box) };
            }
            else
            {
                ApplyTransformFromClick();
                return;
            }
        }
        TransformSessionValue = t;
    }

    private static int IndexNear(IReadOnlyList<Vec2> points, Vec2 p, double tolerance)
    {
        for (int i = 0; i < points.Count; i++) if (EditorHelpers.Distance(points[i], p) < tolerance) return i;
        return -1;
    }

    /// <summary>Rotation starts just outside a corner (about 32 points); a click farther away applies the transform.</summary>
    private bool NearCorner(Vec2 doc, IReadOnlyList<Vec2> corners)
    {
        double reach = 32 / Math.Max(Canvas.Transform.Scale, 0.001);
        return corners.Any(c => EditorHelpers.Distance(c, doc) < reach);
    }

    private void ApplyTransformFromClick()
    {
        CommitTransform();
        Tool = ToolID.Move;
    }

    public void TransformDrag(Vec2 doc, bool shift)
    {
        if (transformSession is not { } t || t.Handle is not { } handle) return;
        switch (handle)
        {
            case TransformHandle.Move(var start, var box):
                t = t with { Box = box with { X = box.X + (doc - start).X, Y = box.Y + (doc - start).Y } };
                break;
            case TransformHandle.Rotate(var startAngle, var box):
                {
                    var v = doc - box.Centre;
                    double angle = box.Rotation + Math.Atan2(v.Y, v.X) - startAngle;
                    if (shift) angle = (angle / (Math.PI / 12)).Rounded() * (Math.PI / 12);
                    var centre = box.Centre;
                    var rotated = box with { Rotation = angle };
                    t = t with { Box = rotated with { X = centre.X - rotated.Size.X / 2, Y = centre.Y - rotated.Size.Y / 2 } };
                    break;
                }
            case TransformHandle.Scale(var sx, var sy):
                // Corners scale proportionally unless Shift is held (Photoshop's default).
                t = t with { Box = t.Box.Drag(sx, sy, doc, proportional: sx != 0 && sy != 0 && !shift) };
                break;
            case TransformHandle.MoveQuad(var start, var quad):
                t = t with { Quad = [.. quad.Select(p => p + doc - start)] };
                break;
            case TransformHandle.Corner(var i, var start, var quad):
                {
                    var q = quad.ToBuilder();
                    var delta = doc - start;
                    q[i] = quad[i] + delta;
                    if (t.Mode == TransformMode.Perspective)
                    {
                        // The corner sharing the top/bottom edge mirrors the move along that edge.
                        int partner = i ^ 1;
                        var edge = EditorHelpers.Normalized(quad[partner] - quad[i]);
                        q[partner] = quad[partner] - edge * Vec2.Dot(delta, edge);
                    }
                    var made = q.ToImmutable();
                    if (IsConvex(made)) t = t with { Quad = made };
                    break;
                }
            case TransformHandle.Edge(var i, var start, var quad):
                {
                    // Skew: slide the edge's two corners along the edge direction.
                    var q = quad.ToBuilder();
                    int a = i, b = (i + 1) % 4;
                    var dir = EditorHelpers.Normalized(quad[b] - quad[a]);
                    var delta = t.Mode == TransformMode.Skew ? dir * Vec2.Dot(doc - start, dir) : doc - start;
                    q[a] = quad[a] + delta;
                    q[b] = quad[b] + delta;
                    var made = q.ToImmutable();
                    if (IsConvex(made)) t = t with { Quad = made };
                    break;
                }
        }
        TransformSessionValue = t;
        UpdateTransformPreview();
    }

    public static bool IsConvex(IReadOnlyList<Vec2> q)
    {
        double sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vec2 a = q[i], b = q[(i + 1) % 4], c = q[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) return false;
            if (sign == 0) sign = cross;
            else if (cross > 0 != sign > 0) return false;
        }
        return true;
    }

    public void SetTransformMode(TransformMode mode)
    {
        if (transformSession is not { } t) return;
        t = t with { Mode = mode };
        if (mode != TransformMode.ScaleRotate && t.Quad is null) t = t with { Quad = [.. t.Box.Corners] };
        TransformSessionValue = t;
    }

    public void TransformMouseUp()
    {
        if (transformSession is { } t) TransformSessionValue = t with { Handle = null };
    }

    /// <summary>Numeric edits from the options bar.</summary>
    public void SetTransform(double? scaleXPercent = null, double? scaleYPercent = null, double? degrees = null)
    {
        if (transformSession is not { } t) return;
        var centre = t.Box.Centre;
        var box = t.Box;
        if (scaleXPercent is { } sx) box = box with { ScaleX = Math.Max(sx, 0.1) / 100 };
        if (scaleYPercent is { } sy) box = box with { ScaleY = Math.Max(sy, 0.1) / 100 };
        if (degrees is { } d) box = box with { Rotation = d * Math.PI / 180 };
        box = box with { X = centre.X - box.Size.X / 2, Y = centre.Y - box.Size.Y / 2 };
        TransformSessionValue = t with { Box = box };
        UpdateTransformPreview();
    }

    public void SetTransformScaleXPercent(double value) => SetTransform(scaleXPercent: value);
    public void SetTransformScaleYPercent(double value) => SetTransform(scaleYPercent: value);
    public void SetTransformDegrees(double value) => SetTransform(degrees: value);
}
