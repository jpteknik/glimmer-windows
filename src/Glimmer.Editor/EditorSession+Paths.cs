// Ported from App/EditorSession+Paths.swift
using System.Numerics;
using Glimmer.Core;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

public static class DocumentStatePaths
{
    /// <summary>Text bound to a path follows it: refresh the baseline of every text layer on <paramref name="id"/>.</summary>
    public static DocumentState SyncText(this DocumentState state, Guid id)
    {
        if (state.Paths.FirstOrDefault(p => p.Id == id)?.Polygons().FirstOrDefault() is not { Length: > 1 } line) return state;
        foreach (var layer in state.AllLayers)
        {
            if (layer.Content is not TextContent t || t.Text.OnPathID != id || (t.Text.OnPath is { } on && on.SequenceEqual(line))) continue;
            state = state.UpdateLayer(layer.Id, l => l with { Content = new TextContent(t.Text with { OnPath = [.. line] }) });
        }
        return state;
    }
}

public sealed partial class EditorSession
{
    // MARK: Pen tool and paths

    public VectorPath? ActivePath
    {
        get
        {
            if (State.Paths.FirstOrDefault(p => p.Id == activePathID) is { } p) return p;
            return ShapeLayerForActivePath?.VectorMask;
        }
    }

    /// <summary>The shape layer whose vector mask is being edited, if any.</summary>
    public Layer? ShapeLayerForActivePath => activePathID is { } id ? State.AllLayers.FirstOrDefault(l => l.VectorMask?.Id == id) : null;

    /// <summary>Click: drag an existing anchor/handle, close the open subpath, or add an anchor.</summary>
    public void PenMouseDown(Vec2 doc, double viewTolerance)
    {
        CommitLiveEdit();
        double tol = viewTolerance / Math.Max(Canvas.Transform.Scale, 0.001);
        if (ActivePath is { } path)
        {
            for (int si = 0; si < path.Subpaths.Length; si++)
            {
                var sub = path.Subpaths[si];
                for (int ai = 0; ai < sub.Anchors.Length; ai++)
                {
                    var a = sub.Anchors[ai];
                    if (EditorHelpers.Distance(a.HandleOut, doc) < tol && a.HandleOut != a.Point) { penDrag = (si, ai, 2); return; }
                    if (EditorHelpers.Distance(a.HandleIn, doc) < tol && a.HandleIn != a.Point) { penDrag = (si, ai, 1); return; }
                    if (EditorHelpers.Distance(a.Point, doc) < tol)
                    {
                        // Clicking the first anchor of the open subpath closes it.
                        if (ai == 0 && si == path.Subpaths.Length - 1 && !sub.IsClosed && sub.Anchors.Length > 2 && penOpen)
                        {
                            int closing = si;
                            UpdatePath(p => p with { Subpaths = p.Subpaths.SetItem(closing, p.Subpaths[closing] with { IsClosed = true }) });
                            penOpen = false;
                            return;
                        }
                        penDrag = (si, ai, 0);
                        return;
                    }
                }
            }
        }
        if (ActivePath is null)
        {
            var created = new VectorPath($"Path {State.Paths.Length + 1}");
            Edit("New Path", s => s with { Paths = s.Paths.Add(created) });
            ActivePathID = created.Id;
        }
        bool open = penOpen;
        UpdatePath(path =>
        {
            if (!open || path.Subpaths.IsEmpty || path.Subpaths[^1].IsClosed) path = path with { Subpaths = path.Subpaths.Add(new Subpath()) };
            var last = path.Subpaths[^1];
            return path with { Subpaths = path.Subpaths.SetItem(path.Subpaths.Length - 1, last with { Anchors = last.Anchors.Add(new PathAnchor(doc)) }) };
        });
        penOpen = true;
        if (ActivePath is { } now)
        {
            int si = now.Subpaths.Length - 1;
            penDrag = (si, now.Subpaths[si].Anchors.Length - 1, 3);   // 3 = pull out handles
        }
    }

    public void PenDragged(Vec2 doc, bool breakSymmetry)
    {
        if (penDrag is not { } drag) return;
        var (si, ai, part) = drag;
        UpdatePath(path =>
        {
            if (si >= path.Subpaths.Length || ai >= path.Subpaths[si].Anchors.Length) return path;
            var a = path.Subpaths[si].Anchors[ai];
            switch (part)
            {
                case 0:
                    {
                        var d = doc - a.Point;
                        a = new PathAnchor(a.Point + d, a.HandleIn + d, a.HandleOut + d);
                        break;
                    }
                case 1:
                    a = a with { HandleIn = doc, HandleOut = breakSymmetry ? a.HandleOut : a.Point * 2 - doc };
                    break;
                case 2:
                    a = a with { HandleOut = doc, HandleIn = breakSymmetry ? a.HandleIn : a.Point * 2 - doc };
                    break;
                default:
                    a = a with { HandleOut = doc, HandleIn = breakSymmetry ? a.Point : a.Point * 2 - doc };
                    break;
            }
            var sub = path.Subpaths[si];
            return path with { Subpaths = path.Subpaths.SetItem(si, sub with { Anchors = sub.Anchors.SetItem(ai, a) }) };
        }, live: true);
    }

    public void PenMouseUp()
    {
        if (penDrag is not null) CommitLiveEdit();
        penDrag = null;
    }

    public void FinishPathSegment() => penOpen = false;

    /// <summary>Esc while drawing: the unfinished subpath goes (and the path, if that was all of it).</summary>
    public void DiscardUnfinishedPath()
    {
        if (!penOpen || ActivePath is not { } path || path.Subpaths.IsEmpty || path.Subpaths[^1].IsClosed)
        {
            FinishPathSegment();
            return;
        }
        penOpen = false;
        var id = path.Id;
        Edit("Discard Path", state =>
        {
            int i = state.Paths.IndexOf(state.Paths.FirstOrDefault(p => p.Id == id)!);
            if (i < 0) return state;
            var p = state.Paths[i];
            p = p with { Subpaths = p.Subpaths.RemoveAt(p.Subpaths.Length - 1) };
            return state with { Paths = p.Subpaths.IsEmpty ? state.Paths.RemoveAt(i) : state.Paths.SetItem(i, p) };
        });
        if (!State.Paths.Any(p => p.Id == id)) ActivePathID = null;
    }

    public void UpdatePath(Func<VectorPath, VectorPath> change, bool live = false)
    {
        if (activePathID is not { } id) return;
        DocumentState Apply(DocumentState state)
        {
            int i = -1;
            for (int k = 0; k < state.Paths.Length; k++) if (state.Paths[k].Id == id) { i = k; break; }
            if (i >= 0)
            {
                state = state with { Paths = state.Paths.SetItem(i, change(state.Paths[i])) };
                return state.SyncText(id);
            }
            if (state.AllLayers.FirstOrDefault(l => l.VectorMask?.Id == id) is { } layer)
            {
                // Shape layer: edit the vector mask and re-rasterize the mask from it.
                return state.UpdateLayer(layer.Id, l => l.VectorMask is { } path ? WithVectorMask(l, change(path)) : l);
            }
            return state;
        }
        if (live) Live("Edit Path", Apply);
        else Edit("Edit Path", Apply);
    }

    public static Selection SelectionFrom(VectorPath path)
    {
        var shapes = path.Polygons().Where(p => p.Length > 2).Select(p => Selection.Polygon(p)).ToList();
        if (shapes.Count == 0) return new Selection();
        var s = shapes[0];
        foreach (var o in shapes.Skip(1)) s = s.Combined(o, Selection.Operation.Add);
        return s;
    }

    public void MakeSelectionFromPath(Guid id)
    {
        if (State.Paths.FirstOrDefault(p => p.Id == id) is not { } path) return;
        var s = SelectionFrom(path);
        Edit("Make Selection", st => st with { Selection = s });
    }

    public void FillPath(Guid id)
    {
        if (ActiveLayer is not { IsRaster: true } layer || State.Paths.FirstOrDefault(p => p.Id == id) is not { } path)
        {
            ErrorMessage = "Select a pixel layer to fill the path on.";
            return;
        }
        var s = SelectionFrom(path);
        if (s.Bounds(RegionOpsPublic.Expand(State.Bounds, 4096)) is not { } bounds) return;
        try
        {
            var filled = new PixelOps().Run(PixelOps.Mode.Paint, layer.Tiles, selection: s, coords: bounds.TileCoords, color: new Vector4(foreground.Linear, 1));
            Edit("Fill Path", st => st.UpdateLayer(layer.Id, l => l with { Tiles = filled }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void StrokePath(Guid id)
    {
        if (ActiveLayer is not { IsRaster: true } layer || State.Paths.FirstOrDefault(p => p.Id == id) is not { } path)
        {
            ErrorMessage = "Select a pixel layer to stroke the path on.";
            return;
        }
        try
        {
            var settings = brush with { PressureSize = false };
            var engine = new StrokeEngine(layer.Tiles, new StrokeEngine.Target.Pixels(), false, new Vector4(foreground.Linear, 1), (float)brush.Opacity, selection: null);
            var polygons = path.Polygons();
            for (int i = 0; i < polygons.Length; i++)
            {
                var poly = polygons[i];
                if (poly.Length == 0) continue;
                var sampler = new StrokeSampler(settings);
                var dabs = sampler.Begin(new StrokeSampler.Sample(poly[0]));
                foreach (var p in poly.Skip(1)) dabs.AddRange(sampler.Move(new StrokeSampler.Sample(p)));
                if (i < path.Subpaths.Length && path.Subpaths[i].IsClosed) dabs.AddRange(sampler.Move(new StrokeSampler.Sample(poly[0])));
                engine.Add(dabs);
            }
            engine.Finish();
            var result = engine.Result;
            Edit("Stroke Path", st => st.UpdateLayer(layer.Id, l => l with { Tiles = result }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void DeletePath(Guid id)
    {
        Edit("Delete Path", state =>
        {
            state = state with { Paths = state.Paths.RemoveAll(p => p.Id == id) };
            // Text on the deleted path keeps its shape but is no longer bound to anything.
            foreach (var layer in state.AllLayers)
            {
                if (layer.Content is not TextContent t || t.Text.OnPathID != id) continue;
                state = state.UpdateLayer(layer.Id, l => l with { Content = new TextContent(t.Text with { OnPathID = null }) });
            }
            return state;
        });
        if (activePathID == id) ActivePathID = null;
    }

    public void RenamePath(Guid id, string name) =>
        Edit("Rename Path", state =>
        {
            for (int i = 0; i < state.Paths.Length; i++)
                if (state.Paths[i].Id == id) return state with { Paths = state.Paths.SetItem(i, state.Paths[i] with { Name = name }) };
            return state;
        });
}
