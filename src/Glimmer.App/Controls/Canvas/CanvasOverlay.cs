// Ported from App/CanvasView.swift (CanvasNSView.updateMarqueeOverlay, updateToolChrome, updateBrushRing,
// updateHints) and RulerView. The Mac keeps these as CAShapeLayers over the Metal layer; here they are drawn with a
// DrawingContext over the canvas bitmap, in view points (y-down, so nothing is flipped). The marching ants are drawn
// here too, from the session's selection outline, with a dash offset WPF animates on its render thread: the ants
// march without the canvas drawing a single frame.
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glimmer.Core;
using Glimmer.Editor;

namespace Glimmer.App.Controls.Canvas;

internal sealed class CanvasOverlay
{
    private readonly CanvasSurface owner;
    private readonly Pen antsBase, antsDashes, marqueeBase, marqueeDashes, chromeLines, chromeHandleStroke, guide, grid, minorGrid, paths, hint, ring, ringShadow, pointStroke;
    private readonly Brush chromeHandleFill, cropShade, hintFill, positive, negative, rulerBackground, rulerSeparator, rulerTicks, rulerText, labelBackground;

    private static readonly Color Accent = Theme.Accent;
    private static readonly Color SystemCyan = Color.FromRgb(0x40, 0xc8, 0xe0);
    private static readonly Color SystemOrange = Color.FromRgb(0xff, 0x9f, 0x0a);
    private static readonly Color SystemGreen = Color.FromRgb(0x30, 0xd1, 0x58);
    private static readonly Color SystemRed = Color.FromRgb(0xff, 0x45, 0x3a);
    private static readonly Color SystemBlue = Color.FromRgb(0x0a, 0x84, 0xff);

    public CanvasOverlay(CanvasSurface owner)
    {
        this.owner = owner;
        static Pen P(Color c, double w, double alpha = 1, double[]? dashes = null)
        {
            var pen = new Pen(new SolidColorBrush(Theme.WithAlpha(c, alpha)), w) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Miter };
            if (dashes is not null) pen.DashStyle = new DashStyle(dashes, 0);
            pen.Freeze();
            return pen;
        }
        static Brush B(Color c, double alpha = 1) => Theme.Frozen(Theme.WithAlpha(c, alpha));

        antsBase = P(Colors.White, 1);
        // Dashes are in pen widths: 4 on, 4 off. The offset marches unless Windows' animations are off.
        var marching = new DashStyle([4.0, 4.0], 0);
        if (SystemParameters.ClientAreaAnimation)
            marching.BeginAnimation(DashStyle.OffsetProperty, new DoubleAnimation(0, -8, new Duration(TimeSpan.FromSeconds(0.8))) { RepeatBehavior = RepeatBehavior.Forever });
        antsDashes = new Pen(Brushes.Black, 1) { DashStyle = marching };
        marqueeBase = P(Colors.White, 1);
        marqueeDashes = P(Colors.Black, 1, 1, [4, 4]);
        chromeLines = P(Accent, 1);
        chromeHandleStroke = P(Accent, 1);
        chromeHandleFill = Brushes.White;
        cropShade = B(Colors.Black, 0.5);
        guide = P(SystemCyan, 1);
        grid = P(Colors.Gray, 0.5, 0.45);
        minorGrid = P(Colors.Gray, 0.5, 0.2);
        paths = P(SystemBlue, 1, 0.45);
        hint = P(SystemOrange, 2, 1, [3, 1.5]);
        hintFill = B(SystemOrange, 0.12);
        ring = P(Colors.White, 1);
        ringShadow = P(Colors.Black, 2.2, 0.55);
        positive = B(SystemGreen);
        negative = B(SystemRed);
        pointStroke = P(Colors.White, 2);
        rulerBackground = B(Theme.Panel, 0.92);
        rulerSeparator = B(Theme.Edge);
        rulerTicks = B(Theme.Muted, 0.6);
        rulerText = B(Theme.Muted);
        labelBackground = B(Colors.Black, 0.7);
    }

    private static Point Pt(Vec2 v) => new(v.X, v.Y);

    public void Draw(DrawingContext dc, IEditorSession s)
    {
        var t = s.Canvas.Transform;
        Point V(Vec2 d) => Pt(t.ViewPoint(d));
        var size = owner.RenderSize;
        var viewRect = t.VisibleDocumentRect(new Vec2(size.Width, size.Height));

        // The selection: white under black marching dashes.
        foreach (var outline in s.SelectionOutline)
        {
            if (outline.Count < 2) continue;
            var g = Polyline(outline.Select(V), closed: false);
            dc.DrawGeometry(null, antsBase, g);
            dc.DrawGeometry(null, antsDashes, g);
        }

        // Every saved path, faintly (the active one is drawn below with its handles).
        var saved = new StreamGeometry();
        using (var ctx = saved.Open())
            foreach (var path in s.State.Paths.Where(p => p.Id != s.ActivePathID))
                foreach (var poly in path.Polygons().Where(p => p.Length > 1))
                    AddPolyline(ctx, poly.Select(V), false);
        dc.DrawGeometry(null, paths, saved);

        DrawGrid(dc, s, V, t);
        DrawGuides(dc, s, V, viewRect);

        var lines = new StreamGeometry();
        var handles = new StreamGeometry();
        using (var lc = lines.Open())
        using (var hc = handles.Open())
        {
            if (s.TransformSession is { } ts)
            {
                AddPolyline(lc, ts.Corners.Select(V), true);
                foreach (var p in ts.Corners.Concat(ts.Edges)) AddSquare(hc, V(p), 7);
            }
            if (s.CropSession is { } crop)
            {
                // Shade outside the crop (even-odd: the big rectangle minus the crop's corners).
                var shade = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var sc = shade.Open())
                {
                    AddPolyline(sc, new[] { new Vec2(-1e5, -1e5), new Vec2(1e5, -1e5), new Vec2(1e5, 1e5), new Vec2(-1e5, 1e5) }.Select(V), true, filled: true);
                    AddPolyline(sc, crop.Corners.Select(V), true, filled: true);
                }
                dc.DrawGeometry(cropShade, null, shade);
                AddPolyline(lc, crop.Corners.Select(V), true);
                var r = crop.Rect;
                int thirds = Math.Abs(crop.Angle) > 1e-6 ? 6 : 3;   // a finer grid helps when straightening
                for (int i = 1; i < thirds; i++)
                {
                    double fx = r.MinX + r.Width * i / thirds, fy = r.MinY + r.Height * i / thirds;
                    AddLine(lc, V(crop.World(new Vec2(fx, r.MinY))), V(crop.World(new Vec2(fx, r.MaxY))));
                    AddLine(lc, V(crop.World(new Vec2(r.MinX, fy))), V(crop.World(new Vec2(r.MaxX, fy))));
                }
                foreach (var h in s.CropHandles(r)) AddSquare(hc, V(crop.World(h)), 7);
            }
            if ((s.EffectiveTool == ToolID.Pen || s.ActivePathID is not null) && s.ActivePath is { } path)
            {
                foreach (var sub in path.Subpaths.Where(sp => sp.Anchors.Length > 0))
                {
                    var poly = new VectorPath("", [sub], Guid.Empty).Polygons()[0];
                    AddPolyline(lc, poly.Select(V), false);
                    if (s.EffectiveTool != ToolID.Pen) continue;
                    foreach (var a in sub.Anchors)
                    {
                        if (a.HandleIn != a.Point) AddLine(lc, V(a.HandleIn), V(a.Point));
                        if (a.HandleOut != a.Point) AddLine(lc, V(a.Point), V(a.HandleOut));
                        AddSquare(hc, V(a.Point), 6);
                        foreach (var h in new[] { a.HandleIn, a.HandleOut }.Where(h => h != a.Point)) AddSquare(hc, V(h), 4);
                    }
                }
            }
            // HoverPoint is in view points; the marker is asked for at the document point under the pointer.
            if (s.Liquify is null && s.CloneMarker(s.HoverPoint is { } hp ? t.DocumentPoint(hp) : null) is { } marker)
            {
                var c = V(marker);
                double rr = Math.Max(s.Brush.Size * t.Scale / 2, 4);
                AddEllipse(lc, c, rr);
                AddLine(lc, new Point(c.X - 5, c.Y), new Point(c.X + 5, c.Y));
                AddLine(lc, new Point(c.X, c.Y - 5), new Point(c.X, c.Y + 5));
            }
        }
        DrawHints(dc, s, V);
        dc.DrawGeometry(null, chromeLines, lines);
        dc.DrawGeometry(chromeHandleFill, chromeHandleStroke, handles);

        // Live angle while rotating a transform, at the box centre (corners are often off screen).
        if (s.TransformSession is { IsRotating: true } rotating)
            DrawLabel(dc, string.Format(CultureInfo.CurrentCulture, "{0:0.0}°", rotating.Rotation * 180 / Math.PI), V(rotating.Centre));

        DrawMarquee(dc, s, V, t);
        DrawMagicPoints(dc, s, V);
        DrawBrushRing(dc, s, t);
        if (s.State.Aids.RulersVisible) DrawRulers(dc, t, size);
    }

    private void DrawGrid(DrawingContext dc, IEditorSession s, Func<Vec2, Point> V, ViewTransform t)
    {
        var aids = s.State.Aids;
        if (!aids.Grid.IsVisible) return;
        double w = s.State.Width, h = s.State.Height, spacing = aids.Grid.Spacing;
        if (spacing * t.Scale >= 6)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                for (double x = 0; x <= w; x += spacing) AddLine(c, V(new Vec2(x, 0)), V(new Vec2(x, h)));
                for (double y = 0; y <= h; y += spacing) AddLine(c, V(new Vec2(0, y)), V(new Vec2(w, y)));
            }
            dc.DrawGeometry(null, grid, g);
        }
        // Fainter lines between the main ones, when there is room for them.
        int sub = Math.Max(aids.Grid.Subdivisions, 1);
        double step = spacing / sub;
        if (sub > 1 && step * t.Scale >= 6)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                for (int i = 1; i * step <= Math.Max(w, h); i++)
                {
                    if (i % sub == 0) continue;
                    double p = i * step;
                    if (p <= w) AddLine(c, V(new Vec2(p, 0)), V(new Vec2(p, h)));
                    if (p <= h) AddLine(c, V(new Vec2(0, p)), V(new Vec2(w, p)));
                }
            dc.DrawGeometry(null, minorGrid, g);
        }
    }

    private void DrawGuides(DrawingContext dc, IEditorSession s, Func<Vec2, Point> V, PixelRect viewRect)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            void Add(GuideAxis axis, double position)
            {
                if (axis == GuideAxis.Vertical) AddLine(c, V(new Vec2(position, viewRect.Y)), V(new Vec2(position, viewRect.MaxY)));
                else AddLine(c, V(new Vec2(viewRect.X, position)), V(new Vec2(viewRect.MaxX, position)));
            }
            foreach (var guideLine in s.State.Aids.Guides) Add(guideLine.Axis, guideLine.Position);
            if (s.GuideDrag is { } drag) Add(drag.Axis, drag.Position);
        }
        dc.DrawGeometry(null, guide, g);
    }

    /// <summary>Boxes from Find Distractions.</summary>
    private void DrawHints(DrawingContext dc, IEditorSession s, Func<Vec2, Point> V)
    {
        foreach (var d in s.AI.Distractions)
        {
            var r = d.PixelRect(s.State.Width, s.State.Height);
            var g = Polyline(new[] { new Vec2(r.X, r.Y), new Vec2(r.MaxX, r.Y), new Vec2(r.MaxX, r.MaxY), new Vec2(r.X, r.MaxY) }.Select(V), true, filled: true);
            dc.DrawGeometry(hintFill, hint, g);
        }
    }

    /// <summary>The marquee, lasso, gradient drag or shape being drawn: white under black dashes.</summary>
    private void DrawMarquee(DrawingContext dc, IEditorSession s, Func<Vec2, Point> V, ViewTransform t)
    {
        Geometry? g = null;
        if (s.ShapeDrag is { } d)
        {
            double x0 = Math.Min(d.Start.X, d.End.X), y0 = Math.Min(d.Start.Y, d.End.Y), x1 = Math.Max(d.Start.X, d.End.X), y1 = Math.Max(d.Start.Y, d.End.Y);
            g = s.ShapeKind switch
            {
                ShapeKind.Line => Polyline(new[] { V(d.Start), V(d.End) }, false),
                ShapeKind.Ellipse => Polyline(EllipsePoints(new Vec2((x0 + x1) / 2, (y0 + y1) / 2), (x1 - x0) / 2, (y1 - y0) / 2, 48).Select(V), true),
                ShapeKind.RoundedRectangle => Polyline(RoundedRectPoints(x0, y0, x1, y1, s.ShapeCornerRadius).Select(V), true),
                _ => Polyline(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) }.Select(V), true),
            };
        }
        else if (s.Lasso is { Count: > 0 } points)
        {
            var all = points.ToList();
            if (s.EffectiveTool == ToolID.PolygonLasso && s.HoverPoint is { } hover) all.Add(t.DocumentPoint(hover));
            g = Polyline(all.Select(V), false);
        }
        else if (s.GradientDrag is { } gd)
        {
            var sg = new StreamGeometry();
            using (var c = sg.Open())
            {
                AddLine(c, V(gd.Start), V(gd.End));
                AddEllipse(c, V(gd.Start), 4);
                AddEllipse(c, V(gd.End), 4);
            }
            g = sg;
        }
        else if (s.Marquee is { } m)
        {
            var (x0, y0, x1, y1) = m.Rect;
            // An ellipse in document space, mapped through the (possibly rotated) view.
            g = m.Ellipse
                ? Polyline(EllipsePoints(new Vec2((x0 + x1) / 2, (y0 + y1) / 2), (x1 - x0) / 2, (y1 - y0) / 2, 64).Select(V), true)
                : Polyline(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) }.Select(V), true);
        }
        if (g is null) return;
        dc.DrawGeometry(null, marqueeBase, g);
        dc.DrawGeometry(null, marqueeDashes, g);
    }

    /// <summary>Magic Select click points: green on the object, red for what to leave out.</summary>
    private void DrawMagicPoints(DrawingContext dc, IEditorSession s, Func<Vec2, Point> V)
    {
        foreach (var p in s.AI.MagicPoints) dc.DrawEllipse(p.Positive ? positive : negative, pointStroke, V(p.Location), 5, 5);
    }

    /// <summary>The brush size ring under the pointer, or a target while Alt sets a clone or healing source.</summary>
    private void DrawBrushRing(DrawingContext dc, IEditorSession s, ViewTransform t)
    {
        if (s.HoverPoint is not { } hover) return;
        var c = Pt(hover);
        var tool = s.EffectiveTool;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (owner.OptionHeld && tool is ToolID.CloneStamp or ToolID.HealingBrush)
            {
                AddEllipse(ctx, c, 7);
                AddLine(ctx, new Point(c.X - 12, c.Y), new Point(c.X + 12, c.Y));
                AddLine(ctx, new Point(c.X, c.Y - 12), new Point(c.X, c.Y + 12));
            }
            else
            {
                bool paints = tool is ToolID.Brush or ToolID.Eraser or ToolID.CloneStamp or ToolID.HealingBrush or ToolID.Dodge or ToolID.Burn
                              or ToolID.Blur or ToolID.Sharpen or ToolID.Smudge or ToolID.PatternStamp || s.Liquify is not null;
                if (!paints) return;
                double size = s.Liquify is not null ? s.LiquifySize : s.Brush.Size;
                double r = Math.Max(size * t.Scale / 2, 1);
                AddEllipse(ctx, c, r);
                if (r < 4)
                {
                    AddLine(ctx, new Point(c.X - 6, c.Y), new Point(c.X + 6, c.Y));
                    AddLine(ctx, new Point(c.X, c.Y - 6), new Point(c.X, c.Y + 6));
                }
            }
        }
        dc.DrawGeometry(null, ringShadow, g);
        dc.DrawGeometry(null, ring, g);
    }

    private void DrawLabel(DrawingContext dc, string text, Point centre)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White,
                                   VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        var box = new Rect(centre.X - 30, centre.Y - 9, 60, 18);
        dc.DrawRoundedRectangle(labelBackground, null, box, 4, 4);
        dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));
    }

    /// <summary>Pixel rulers along the top and left edges (RulerView). Purely visual: drags that start on them become
    /// guides in the canvas's own input handling.</summary>
    private void DrawRulers(DrawingContext dc, ViewTransform t, Size size)
    {
        const double th = 18;
        dc.DrawRectangle(rulerBackground, null, new Rect(0, 0, size.Width, th));
        dc.DrawRectangle(rulerBackground, null, new Rect(0, 0, th, size.Height));
        dc.DrawRectangle(rulerSeparator, null, new Rect(0, th - 1, size.Width, 1));
        dc.DrawRectangle(rulerSeparator, null, new Rect(th - 1, 0, 1, size.Height));
        // Pick a step that leaves ~60 points between labelled ticks.
        double[] steps = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
        double step = steps.FirstOrDefault(s => s * t.Scale >= 60, 10000);
        double pixelsPerDip = VisualTreeHelper.GetDpi(owner).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        FormattedText Label(double v) => new(((int)v).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                             typeface, 9, rulerText, pixelsPerDip);
        var topLeft = t.DocumentPoint(new Vec2(0, 0));
        var bottomRight = t.DocumentPoint(new Vec2(size.Width, size.Height));
        for (double x = Math.Floor(Math.Min(topLeft.X, bottomRight.X) / step) * step; x <= Math.Max(topLeft.X, bottomRight.X); x += step)
        {
            for (int i = 0; i < 10; i++)
            {
                double vx = t.ViewPoint(new Vec2(x + step * i / 10, topLeft.Y)).X;
                if (vx < th) continue;
                double h = i == 0 ? th : i == 5 ? 7 : 4;
                dc.DrawRectangle(rulerTicks, null, new Rect(vx, th - h, 1, h));
            }
            double lx = t.ViewPoint(new Vec2(x, topLeft.Y)).X;
            if (lx >= th) dc.DrawText(Label(x), new Point(lx + 2, 1));
        }
        for (double y = Math.Floor(Math.Min(topLeft.Y, bottomRight.Y) / step) * step; y <= Math.Max(topLeft.Y, bottomRight.Y); y += step)
        {
            for (int i = 0; i < 10; i++)
            {
                double vy = t.ViewPoint(new Vec2(topLeft.X, y + step * i / 10)).Y;
                if (vy < th) continue;
                double w = i == 0 ? th : i == 5 ? 7 : 4;
                dc.DrawRectangle(rulerTicks, null, new Rect(th - w, vy, w, 1));
            }
            double ly = t.ViewPoint(new Vec2(topLeft.X, y)).Y;
            if (ly < th) continue;
            dc.PushTransform(new RotateTransform(90, 1, ly + 2));
            dc.DrawText(Label(y), new Point(1, ly + 2 - 10));
            dc.Pop();
        }
        dc.DrawRectangle(rulerBackground, null, new Rect(0, 0, th, th));
    }

    // ------------------------------------------------------------------------------------------------- geometry

    private static StreamGeometry Polyline(IEnumerable<Point> points, bool closed, bool filled = false)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) AddPolyline(c, points, closed, filled);
        g.Freeze();
        return g;
    }

    private static void AddPolyline(StreamGeometryContext c, IEnumerable<Point> points, bool closed, bool filled = false)
    {
        using var e = points.GetEnumerator();
        if (!e.MoveNext()) return;
        c.BeginFigure(e.Current, filled, closed);
        while (e.MoveNext()) c.LineTo(e.Current, true, false);
    }

    private static void AddLine(StreamGeometryContext c, Point a, Point b)
    {
        c.BeginFigure(a, false, false);
        c.LineTo(b, true, false);
    }

    private static void AddSquare(StreamGeometryContext c, Point centre, double size)
    {
        double h = size / 2;
        c.BeginFigure(new Point(centre.X - h, centre.Y - h), true, true);
        c.PolyLineTo([new Point(centre.X + h, centre.Y - h), new Point(centre.X + h, centre.Y + h), new Point(centre.X - h, centre.Y + h)], true, false);
    }

    private static void AddEllipse(StreamGeometryContext c, Point centre, double r)
    {
        c.BeginFigure(new Point(centre.X - r, centre.Y), false, true);
        c.ArcTo(new Point(centre.X + r, centre.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        c.ArcTo(new Point(centre.X - r, centre.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
    }

    private static IEnumerable<Vec2> EllipsePoints(Vec2 c, double rx, double ry, int n) =>
        Enumerable.Range(0, n + 1).Select(i => { double a = (double)i / n * 2 * Math.PI; return c + new Vec2(Math.Cos(a) * rx, Math.Sin(a) * ry); });

    /// <summary>EditorSession.roundedRectPolygon: a rounded rectangle as a polygon (8 points a corner).</summary>
    private static IEnumerable<Vec2> RoundedRectPoints(double x0, double y0, double x1, double y1, double radius)
    {
        double r = Math.Min(radius, Math.Min(x1 - x0, y1 - y0) / 2);
        (Vec2 Centre, double Start)[] corners = [(new(x1 - r, y0 + r), -90), (new(x1 - r, y1 - r), 0), (new(x0 + r, y1 - r), 90), (new(x0 + r, y0 + r), 180)];
        foreach (var (centre, start) in corners)
            for (int i = 0; i <= 8; i++)
            {
                double a = (start + 90.0 * i / 8) * Math.PI / 180;
                yield return centre + new Vec2(Math.Cos(a) * r, Math.Sin(a) * r);
            }
    }
}
