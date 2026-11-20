// Ported from App/EditorSession+Liquify.swift
using Glimmer.Core;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

/// <summary>Liquify in progress on one layer.</summary>
public sealed class LiquifySession
{
    public LiquifySession(LiquifyEngine engine, Guid layerID)
    {
        Engine = engine;
        LayerID = layerID;
    }

    public LiquifyEngine Engine { get; }
    public Guid LayerID { get; }
    public Vec2? Last { get; set; }
    /// <summary>Pen pressure of the current event (1 for a mouse).</summary>
    public double Pressure { get; set; } = 1.0;
}

public sealed partial class EditorSession
{
    // MARK: Liquify

    public void BeginLiquify()
    {
        CommitLiveEdit();
        if (ActiveLayer is not { IsRaster: true } layer) return;
        try
        {
            var engine = new LiquifyEngine(layer.Tiles, State.Bounds);
            Liquify = new LiquifySession(engine, layer.Id);
            Tool = ToolID.Move;
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void LiquifyDrag(Vec2 point, bool start, double pressure = 1)
    {
        if (liquify is not { } l) return;
        l.Pressure = pressure;
        double radius = liquifySize / 2;
        if (start || l.Last is null)
        {
            l.Last = point;
            if (liquifyMode != LiquifyEngine.Mode.Forward) LiquifyDab(point, Vec2.Zero);
            return;
        }
        var last = l.Last.Value;
        double distance = EditorHelpers.Distance(point, last);
        // Forward warp: dabs spaced at ~1/8 of the radius; other modes keep working in place.
        double spacing = Math.Max(radius / 8, 1);
        int steps = Math.Max((int)Math.Ceiling(distance / spacing), 1);
        var previous = last;
        for (int i = 1; i <= steps; i++)
        {
            var p = last + (point - last) * (i / (double)steps);
            LiquifyDab(p, p - previous);
            previous = p;
        }
        l.Last = point;
        PublishLiquify();
    }

    public void LiquifyDab(Vec2 p, Vec2 delta)
    {
        if (liquify is not { } l) return;
        l.Engine.Dab(liquifyMode, p, delta, liquifySize / 2, liquifyPressure * l.Pressure);
    }

    public void LiquifyMouseUp()
    {
        if (liquify is { } l) l.Last = null;
        PublishLiquify();
    }

    public void PublishLiquify()
    {
        if (liquify is not { } l) return;
        TileGrid result;
        try { result = LiquifyResult(l); }
        catch { return; }
        Overrides = new LayerOverrides(pixels: [new(l.LayerID, result)]);
    }

    /// <summary>The warped layer; with a selection, only the selected area changes.</summary>
    private TileGrid LiquifyResult(LiquifySession l)
    {
        var warped = l.Engine.Render();
        if (State.Selection is not { } selection || selection.Bounds(State.Bounds) is not { } area) return warped;
        // Warped pixels inside the selection over the untouched layer outside it.
        var ops = new PixelOps();
        var coords = area.TileCoords;
        var inside = ops.Run(PixelOps.Mode.KeepSelected, warped, selection: selection, coords: coords);
        var onlyInside = new TileGrid.Builder();
        foreach (var c in coords) if (inside[c] is { } t) onlyInside[c] = t;
        var outside = ops.Run(PixelOps.Mode.Erase, l.Engine.Original, selection: selection, coords: coords);
        var both = new Layer(Environment.LayerName("Liquify"), new GroupContent(new LayerGroup(
        [
            new Layer(Environment.LayerName("Outside"), new RasterContent(outside)),
            new Layer(Environment.LayerName("Inside"), new RasterContent(onlyInside.ToImmutable())),
        ])));
        var extent = State.Bounds;
        if (outside.TileBounds is { } tb)
        {
            int x0 = Math.Min(extent.X, tb.X), y0 = Math.Min(extent.Y, tb.Y);
            extent = new PixelRect(x0, y0, Math.Max(extent.MaxX, tb.MaxX) - x0, Math.Max(extent.MaxY, tb.MaxY) - y0);
        }
        return DocumentCompositor.Rasterize(both, extent, BlendSpace.Linear);
    }

    public void CommitLiquify()
    {
        if (liquify is not { } l) return;
        Liquify = null;
        if (!l.Engine.IsIdentity)
        {
            TileGrid? result = null;
            try { result = LiquifyResult(l); }
            catch { }
            if (result is { } r) Edit("Liquify", s => s.UpdateLayer(l.LayerID, layer => layer with { Tiles = r }));
        }
        Overrides = LayerOverrides.None;
    }

    public void RestoreAllLiquify()
    {
        if (liquify is not { } l) return;
        l.Engine.Reset();
        l.Last = null;
        Overrides = LayerOverrides.None;
    }

    public void CancelLiquify()
    {
        Liquify = null;
        Overrides = LayerOverrides.None;
    }
}
