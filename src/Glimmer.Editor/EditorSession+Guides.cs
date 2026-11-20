// Ported from App/EditorSession+Guides.swift
using Glimmer.Core;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    // MARK: Guides, grid, snapping

    public void AddGuide(GuideAxis axis, double position) =>
        Edit("New Guide", s => s with { Aids = s.Aids with { Guides = s.Aids.Guides.Add(new Guide(axis, position)) } });

    public void MoveGuide(Guid id, double position, bool live)
    {
        DocumentState Apply(DocumentState state)
        {
            var guides = state.Aids.Guides;
            for (int i = 0; i < guides.Length; i++)
                if (guides[i].Id == id) return state with { Aids = state.Aids with { Guides = guides.SetItem(i, guides[i] with { Position = position }) } };
            return state;
        }
        if (live) Live("Move Guide", Apply);
        else Edit("Move Guide", Apply);
    }

    public void RemoveGuide(Guid id) =>
        Edit("Delete Guide", s => s with { Aids = s.Aids with { Guides = s.Aids.Guides.RemoveAll(g => g.Id == id) } });

    /// <summary>Snaps a document point to guides, the grid and the document edges (within a few screen pixels).</summary>
    public Vec2 Snapped(Vec2 p)
    {
        if (!State.Aids.Snap) return p;
        return new Vec2(p.X + (SnapDelta([p.X], vertical: true) ?? 0), p.Y + (SnapDelta([p.Y], vertical: false) ?? 0));
    }

    /// <summary>How far to shift a box so one of its edges or its centre lands on a snap line.</summary>
    public Vec2 SnapOffset(Vec2 origin, Vec2 size)
    {
        if (!State.Aids.Snap) return Vec2.Zero;
        return new Vec2(SnapDelta([origin.X, origin.X + size.X / 2, origin.X + size.X], vertical: true) ?? 0,
                        SnapDelta([origin.Y, origin.Y + size.Y / 2, origin.Y + size.Y], vertical: false) ?? 0);
    }

    /// <summary>The smallest shift that puts one of <paramref name="values"/> on a snap line (canvas edges and centre,
    /// guides, the grid), if any is within reach.</summary>
    private double? SnapDelta(double[] values, bool vertical)
    {
        double tol = 6 / Math.Max(Canvas.Transform.Scale, 0.001);
        double extent = vertical ? State.Width : State.Height;
        var lines = new List<double> { 0.0, extent, extent / 2 };
        foreach (var g in State.Aids.Guides) if ((g.Axis == GuideAxis.Vertical) == vertical) lines.Add(g.Position);
        double? best = null;
        foreach (var v in values)
        {
            var candidates = new List<double>(lines);
            if (State.Aids.Grid.IsVisible)
            {
                // Snap to the finer lines too.
                double s = State.Aids.Grid.Spacing / Math.Max(State.Aids.Grid.Subdivisions, 1);
                if (s > 0.5) candidates.Add((v / s).Rounded() * s);
            }
            foreach (var line in candidates)
                if (Math.Abs(line - v) < Math.Min(tol, Math.Abs(best ?? double.PositiveInfinity))) best = line - v;
        }
        return best;
    }
}
