// Windows only. The Mac draws the marching ants in its canvas shader, from the selection's stored tiles
// (CanvasRenderer.swift, ants_fragment). The WPF canvas draws them as an animated overlay instead, so the editor hands
// it the selection's outline as polylines (IEditorSession.SelectionOutline), traced here.
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Traces the edge of a selection (where it crosses one half) as closed polylines in document pixels, by
/// marching squares over the selection sampled every <c>step</c> pixels.</summary>
/// <remarks>
/// Differs from the Mac on purpose in one respect: the Mac's ants run only on stored selection tiles, so Select All (a
/// selection with no tiles) shows none. Here the document's edge counts as the edge of what is selected, so Select All
/// shows ants around the canvas, as Photoshop does.
/// </remarks>
public static class SelectionOutlineTracer
{
    /// <summary>The coarsest sampling that still looks right: about one sample per device pixel, as a power of two.</summary>
    public static int StepFor(double zoom, PixelRect area, long maxNodes = 4_000_000)
    {
        int step = 1;
        if (zoom > 0 && zoom < 1)
        {
            double docPerDevice = 1 / zoom;
            while (step * 2 <= docPerDevice && step < 64) step *= 2;
        }
        while ((long)Math.Ceiling(area.Width / (double)step) * (long)Math.Ceiling(area.Height / (double)step) > maxNodes && step < 4096) step *= 2;
        return step;
    }

    public static IReadOnlyList<IReadOnlyList<Vec2>> Trace(Selection selection, PixelRect document, int step)
    {
        var region = selection.DefaultValue >= 0.5f ? document : (selection.Tiles.TileBounds?.Intersection(document) ?? new PixelRect(0, 0, 0, 0));
        if (region.IsEmpty || step < 1) return [];
        int nx = (region.Width + step - 1) / step, ny = (region.Height + step - 1) / step;
        // Values at the nodes, with a ring of zeros around them (outside the region nothing is selected).
        int w = nx + 2, h = ny + 2;
        var v = new float[w * h];
        var sampler = new Sampler(selection);
        for (int j = 0; j < ny; j++)
        {
            int py = Math.Min(region.Y + j * step + step / 2, region.MaxY - 1);
            for (int i = 0; i < nx; i++)
            {
                int px = Math.Min(region.X + i * step + step / 2, region.MaxX - 1);
                v[(j + 1) * w + (i + 1)] = sampler.At(px, py);
            }
        }
        // Node (gi, gj) of the padded grid sits at the centre of its sample block.
        Vec2 Node(int gi, int gj) => new(region.X + (gi - 1) * step + step / 2.0, region.Y + (gj - 1) * step + step / 2.0);
        // A crossing on the edge from node a to node b, where the value is one half.
        Vec2 Cross(int ai, int aj, int bi, int bj)
        {
            float a = v[aj * w + ai], b = v[bj * w + bi];
            double t = Math.Abs(b - a) < 1e-12 ? 0.5 : (0.5 - a) / (b - a);
            t = Math.Clamp(t, 0, 1);
            var pa = Node(ai, aj);
            var pb = Node(bi, bj);
            return pa + (pb - pa) * t;
        }
        // Edges are keyed: horizontal (i, j)→(i+1, j) as 2·index, vertical (i, j)→(i, j+1) as 2·index+1.
        long H(int i, int j) => 2L * (j * (long)w + i);
        long V(int i, int j) => 2L * (j * (long)w + i) + 1;
        var points = new Dictionary<long, Vec2>();
        var links = new Dictionary<long, (long A, long B)>();
        void Link(long from, long to)
        {
            links[from] = links.TryGetValue(from, out var l) ? (l.A, to) : (to, -1);
            links[to] = links.TryGetValue(to, out var m) ? (m.A, from) : (from, -1);
        }
        void Segment(long e1, Vec2 p1, long e2, Vec2 p2)
        {
            points[e1] = p1;
            points[e2] = p2;
            Link(e1, e2);
        }
        for (int j = 0; j < h - 1; j++)
        {
            for (int i = 0; i < w - 1; i++)
            {
                bool tl = v[j * w + i] >= 0.5f, tr = v[j * w + i + 1] >= 0.5f, br = v[(j + 1) * w + i + 1] >= 0.5f, bl = v[(j + 1) * w + i] >= 0.5f;
                int c = (tl ? 8 : 0) | (tr ? 4 : 0) | (br ? 2 : 0) | (bl ? 1 : 0);
                if (c == 0 || c == 15) continue;
                // The four edges of the cell: top, right, bottom, left.
                long top = H(i, j), right = V(i + 1, j), bottom = H(i, j + 1), left = V(i, j);
                Vec2 Top() => Cross(i, j, i + 1, j);
                Vec2 Right() => Cross(i + 1, j, i + 1, j + 1);
                Vec2 Bottom() => Cross(i, j + 1, i + 1, j + 1);
                Vec2 Left() => Cross(i, j, i, j + 1);
                switch (c)
                {
                    case 1: case 14: Segment(left, Left(), bottom, Bottom()); break;
                    case 2: case 13: Segment(bottom, Bottom(), right, Right()); break;
                    case 3: case 12: Segment(left, Left(), right, Right()); break;
                    case 4: case 11: Segment(top, Top(), right, Right()); break;
                    case 6: case 9: Segment(top, Top(), bottom, Bottom()); break;
                    case 7: case 8: Segment(left, Left(), top, Top()); break;
                    case 5:
                    case 10:
                        {
                            // A saddle: the centre decides which corners are joined.
                            float centre = (v[j * w + i] + v[j * w + i + 1] + v[(j + 1) * w + i + 1] + v[(j + 1) * w + i]) / 4;
                            bool joined = centre >= 0.5f;
                            if ((c == 5) == joined)
                            {
                                Segment(left, Left(), top, Top());
                                Segment(bottom, Bottom(), right, Right());
                            }
                            else
                            {
                                Segment(left, Left(), bottom, Bottom());
                                Segment(top, Top(), right, Right());
                            }
                            break;
                        }
                }
            }
        }
        // Walk the links into loops.
        var outlines = new List<IReadOnlyList<Vec2>>();
        var visited = new HashSet<long>();
        foreach (var start in links.Keys)
        {
            if (!visited.Add(start)) continue;
            var loop = new List<Vec2> { points[start] };
            long previous = start, current = links[start].A;
            while (current != -1 && current != start && visited.Add(current))
            {
                loop.Add(points[current]);
                var (a, b) = links[current];
                long next = a == previous ? b : a;
                previous = current;
                current = next;
            }
            if (loop.Count >= 2) outlines.Add(loop);
        }
        return outlines;
    }

    /// <summary>Selection values at single pixels, reading each tile once.</summary>
    private sealed class Sampler(Selection selection)
    {
        private readonly Dictionary<TileCoord, float[]?> tiles = [];
        private readonly Dictionary<TileCoord, float> solid = [];
        private TileCoord lastCoord = new(int.MinValue, int.MinValue);
        private float[]? lastPixels;
        private float lastSolid;

        public float At(int x, int y)
        {
            int s = TileGeometry.Size;
            var coord = new TileCoord(PixelRect.FloorDiv(x, s), PixelRect.FloorDiv(y, s));
            if (coord != lastCoord)
            {
                lastCoord = coord;
                if (!tiles.TryGetValue(coord, out lastPixels))
                {
                    var tile = selection.Tiles[coord];
                    if (tile is null || tile.IsSolid)
                    {
                        lastPixels = null;
                        solid[coord] = tile?.SolidColor.W ?? selection.DefaultValue;
                    }
                    else
                    {
                        var px = new float[TileGeometry.PixelCount];
                        var halves = tile.Pixels.Span;
                        for (int i = 0; i < px.Length; i++) px[i] = (float)halves[i * 4 + 3];
                        lastPixels = px;
                    }
                    tiles[coord] = lastPixels;
                }
                lastSolid = lastPixels is null ? solid[coord] : 0;
            }
            return lastPixels is null ? lastSolid : lastPixels[(y - coord.Y * s) * s + (x - coord.X * s)];
        }
    }
}
