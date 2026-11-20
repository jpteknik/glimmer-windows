// Ported from App/EditorSession+Tools.swift
using System.Collections.Immutable;
using System.Numerics;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

/// <summary>Painting, selection tools and pixel commands.</summary>
public sealed partial class EditorSession
{
    // MARK: Colors

    public void SwapColors()
    {
        (Foreground, Background) = (background, foreground);
        (ForegroundOpacity, BackgroundOpacity) = (backgroundOpacity, foregroundOpacity);
    }

    public void ResetColors()
    {
        Foreground = new SRGBColor(0, 0, 0);
        Background = new SRGBColor(1, 1, 1);
        ForegroundOpacity = 1;
        BackgroundOpacity = 1;
    }

    private static float Luminance(SRGBColor c)
    {
        var l = c.Linear;
        return 0.2126f * l.X + 0.7152f * l.Y + 0.0722f * l.Z;
    }

    private static Vector4 Linear1(SRGBColor c) => new(c.Linear, 1);

    // MARK: Brush and eraser

    /// <summary>True when the brush would paint the active layer's mask.</summary>
    public bool PaintsMask => editingMask && ActiveLayer?.Mask is not null;

    public void BeginStroke(PointerSample sample, bool erase, bool straightLine)
    {
        var point = sample.Position;
        CommitLiveEdit();
        if (RetouchTools.Contains(tool))
        {
            BeginRetouch(point, sample.Pressure);
            return;
        }
        if (ActiveLayer is not { } layer) return;
        bool maskTarget = PaintsMask;
        if (!(maskTarget || layer.IsRaster))
        {
            ErrorMessage = layer.IsGroup || layer.IsAdjustment
                ? "Select a pixel layer, or click a layer mask to paint it."
                : "This layer can't be painted directly. Rasterize it first (Layer › Rasterize Layer).";
            return;
        }
        try
        {
            var tiles = maskTarget ? (layer.Mask?.Tiles ?? TileGrid.Empty) : layer.Tiles;
            StrokeEngine.Target target = maskTarget ? new StrokeEngine.Target.Mask(layer.Mask?.DefaultValue ?? 1) : new StrokeEngine.Target.Pixels();
            float value = Luminance(erase ? background : foreground);
            var engine = new StrokeEngine(tiles, target, erase, Linear1(foreground),
                                          (float)(brush.Opacity * (erase || maskTarget ? 1 : foregroundOpacity)), value, State.Selection);
            var sampler = new StrokeSampler(brush);
            var dabs = sampler.Begin(new StrokeSampler.Sample(point, sample.Pressure, sample.Tilt, sample.Rotation));
            if (straightLine && lastStrokePoint is { } from)
            {
                // Shift-click continues with a straight line from the last stroke.
                sampler.Begin(new StrokeSampler.Sample(from, sample.Pressure));
                dabs = sampler.Move(new StrokeSampler.Sample(point, sample.Pressure, sample.Tilt, sample.Rotation));
            }
            engine.Add(dabs);
            stroke = (engine, sampler, layer.Id, maskTarget);
            PublishStroke();
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void ContinueStroke(PointerSample sample)
    {
        if (smudge is not null)
        {
            ContinueSmudge(sample.Position, sample.Pressure);
            return;
        }
        if (stroke is not { } s) return;
        var dabs = s.Sampler.Move(new StrokeSampler.Sample(sample.Position, sample.Pressure, sample.Tilt, sample.Rotation));
        if (dabs.Count > 0)
        {
            s.Engine.Add(dabs);
            PublishStroke();
        }
        lastStrokePoint = sample.Position;
    }

    private void PublishStroke()
    {
        if (stroke is not { } s) return;
        Overrides = s.Mask
            ? new LayerOverrides(masks: [new(s.Layer, s.Engine.Result)])
            : new LayerOverrides(pixels: [new(s.Layer, s.Engine.Result)]);
    }

    public void EndStroke(Vec2 point) => EndStroke((Vec2?)point);

    public void EndStroke(Vec2? point)
    {
        if (smudge is not null)
        {
            EndSmudge();
            return;
        }
        if (stroke is not { } s) return;
        stroke = null;
        s.Engine.Finish();
        var result = s.Engine.Result;
        bool erase = tool == ToolID.Eraser;
        UiText name = RetouchTools.Contains(tool) ? RetouchActionName : (s.Mask ? "Paint Mask" : (erase ? "Eraser" : "Brush Stroke"));
        Edit(name, state => state.UpdateLayer(s.Layer, layer => s.Mask
            ? (layer.Mask is { } m ? layer with { Mask = m with { Tiles = result } } : layer)
            : layer with { Tiles = result }));
        Overrides = LayerOverrides.None;
        if (point is { } p) lastStrokePoint = p;
    }

    public void AdjustBrushSize(double factor) =>
        Brush = brush with { Size = Math.Min(Math.Max((brush.Size * factor).Rounded(), 1), 2000) };

    // MARK: Lasso

    public void BeginLasso(Vec2 point, InputModifiers modifiers)
    {
        CommitLiveEdit();
        lassoOperation = OperationFor(modifiers);
        lasso = [point];
        Raise(nameof(Lasso));
    }

    public void ExtendLasso(Vec2 point)
    {
        if (lasso is not { Count: > 0 } points) return;
        if (EditorHelpers.Distance(points[^1], point) > 1 / Math.Max(Canvas.Transform.Scale, 0.01))
        {
            points.Add(point);
            Raise(nameof(Lasso));
        }
    }

    /// <summary>Polygon lasso: adds a vertex, or closes when clicking near the start.</summary>
    public void PolygonClick(Vec2 point, InputModifiers modifiers, bool doubleClick)
    {
        if (lasso is not { } points)
        {
            BeginLasso(point, modifiers);
            return;
        }
        double closeDistance = 8 / Math.Max(Canvas.Transform.Scale, 0.01);
        if (doubleClick || (points.Count > 2 && EditorHelpers.Distance(points[0], point) < closeDistance))
        {
            FinishLasso();
            return;
        }
        points.Add(point);
        Raise(nameof(Lasso));
    }

    public void FinishLasso()
    {
        if (lasso is not { } points) return;
        lasso = null;
        Raise(nameof(Lasso));
        if (points.Count <= 2)
        {
            if (lassoOperation == Selection.Operation.Replace && State.Selection is not null) Edit("Deselect", s => s with { Selection = null });
            return;
        }
        ApplySelection(Selection.Polygon(points), lassoOperation, "Lasso");
    }

    public void CancelLasso()
    {
        lasso = null;
        Raise(nameof(Lasso));
    }

    public static Selection.Operation OperationFor(InputModifiers modifiers) =>
        (modifiers.HasFlag(InputModifiers.Shift), modifiers.HasFlag(InputModifiers.Option)) switch
        {
            (true, true) => Selection.Operation.Intersect,
            (true, false) => Selection.Operation.Add,
            (false, true) => Selection.Operation.Subtract,
            _ => Selection.Operation.Replace,
        };

    public void ApplySelection(Selection shape, Selection.Operation operation, UiText name)
    {
        if (selectionFeather > 0)
        {
            try { shape = SelectionOps.Apply(new SelectionOps.Operation.Feather(selectionFeather), shape, State.Bounds); }
            catch { }
        }
        Edit(name, state =>
        {
            if (state.Selection is { } current && operation != Selection.Operation.Replace)
                return state with { Selection = current.Combined(shape, operation) };
            return state with { Selection = operation == Selection.Operation.Subtract ? null : shape };
        });
    }

    // MARK: Magic wand and paint bucket

    /// <summary>Straight sRGB pixels the wand/bucket sample (document or active layer).</summary>
    private RGBAImage SamplePixels()
    {
        if (sampleAllLayers || ActiveLayer is null)
        {
            using var flat = ImageExporter.FlattenedSRGB(State);
            return RGBAImage.FromSKBitmap(flat);
        }
        using var image = ImageExporter.LayerImageSRGB(ActiveLayer, State);
        return RGBAImage.FromSKBitmap(image);
    }

    private Selection? RegionMask(Vec2 point)
    {
        var seed = ((int)Math.Floor(point.X), (int)Math.Floor(point.Y));
        if (seed.Item1 < 0 || seed.Item2 < 0 || seed.Item1 >= State.Width || seed.Item2 >= State.Height) return null;
        var image = SamplePixels();
        var mask = FloodFill.Mask(image.Pixels, image.Width, image.Height, seed, (float)wandTolerance, wandContiguous);
        return Selection.From(mask, State.Bounds);
    }

    public void MagicWand(Vec2 point, InputModifiers modifiers)
    {
        CommitLiveEdit();
        try
        {
            if (RegionMask(point) is not { } region) return;
            ApplySelection(region, OperationFor(modifiers), "Magic Wand");
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void PaintBucket(Vec2 point)
    {
        CommitLiveEdit();
        if (ActiveLayer is not { IsRaster: true } layer)
        {
            ErrorMessage = "Select a pixel layer to fill.";
            return;
        }
        try
        {
            if (RegionMask(point) is not { } region) return;
            if (State.Selection is { } selection) region = region.Combined(selection, Selection.Operation.Intersect);
            if (region.Bounds(State.Bounds) is not { } bounds) return;
            var ops = new PixelOps();
            var coords = bounds.TileCoords;
            var filled = ops.Run(PixelOps.Mode.Paint, layer.Tiles, selection: null, coords: coords,
                                 coverage: ops.Coverage(region, coords), fullCoverage: false,
                                 color: Linear1(foreground), opacity: (float)(brush.Opacity * foregroundOpacity));
            var id = layer.Id;
            Edit("Paint Bucket", s => s.UpdateLayer(id, l => l with { Tiles = filled }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Fills the selection (or the whole layer) with a colour, no tolerance involved.</summary>
    public void Fill(SRGBColor color, double opacity, UiText name)
    {
        CommitLiveEdit();
        if (ActiveLayer is not { IsRaster: true } layer)
        {
            ErrorMessage = "Select a pixel layer to fill.";
            return;
        }
        try
        {
            var area = State.Selection?.Bounds(State.Bounds) ?? State.Bounds;
            var filled = new PixelOps().Run(PixelOps.Mode.Paint, layer.Tiles, selection: State.Selection, coords: area.TileCoords,
                                            color: Linear1(color), opacity: (float)(brush.Opacity * opacity));
            var id = layer.Id;
            Edit(name, s => s.UpdateLayer(id, l => l with { Tiles = filled }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Paints the current pattern into the selection (or the whole layer).</summary>
    public void FillWithPattern()
    {
        CommitLiveEdit();
        if (ActiveLayer is not { IsRaster: true } layer)
        {
            ErrorMessage = "Select a pixel layer to fill.";
            return;
        }
        try
        {
            var pattern = new Layer(Environment.LayerName("Pattern"), new FillContent(new Fill.Pattern(new PatternFill
            {
                Kind = patternKind, Scale = patternScale, Foreground = foreground, Background = background,
            })), mask: State.Selection?.AsMask);
            var pixels = new Layer(Environment.LayerName("Pixels"), new RasterContent(layer.Tiles));
            var both = new Layer(Environment.LayerName("Fill"), new GroupContent(new LayerGroup([pixels, pattern])));
            // Keep pixels hanging outside the canvas: rasterize over their extent too.
            var area = State.Bounds;
            if (layer.Tiles.TileBounds is { } t)
            {
                int x0 = Math.Min(area.X, t.X), y0 = Math.Min(area.Y, t.Y);
                area = new PixelRect(x0, y0, Math.Max(area.MaxX, t.MaxX) - x0, Math.Max(area.MaxY, t.MaxY) - y0);
            }
            var filled = DocumentCompositor.Rasterize(both, area, State.BlendSpace);
            var id = layer.Id;
            Edit("Pattern Fill", s => s.UpdateLayer(id, l => l with { Tiles = filled }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    // MARK: Gradient tool

    /// <summary>Creates a gradient fill layer from the drag (foreground → background or transparent), masked by the
    /// selection. Non-destructive and editable later.</summary>
    public void FinishGradient()
    {
        if (gradientDrag is not { } drag) return;
        GradientDrag = null;
        if (EditorHelpers.Distance(drag.Start, drag.End) <= 2) return;
        var fill = new GradientFill(drag.Start, drag.End, CurrentGradientStops, gradientStyle, gradientReverse);
        var mask = State.Selection?.AsMask;
        Edit("Gradient", s => s.InsertLayerAboveActive(new Layer(Environment.LayerName("Gradient"), new FillContent(new Fill.Gradient(fill)), mask: mask)));
    }

    /// <summary>The stops the Gradient tool draws with.</summary>
    public ImmutableArray<GradientStop> CurrentGradientStops
    {
        get
        {
            if (gradientStops is { Length: >= 2 } custom) return custom;
            return gradientToTransparent
                ? [new GradientStop(0, foreground, foregroundOpacity), new GradientStop(1, foreground, 0)]
                : [new GradientStop(0, foreground, foregroundOpacity), new GradientStop(1, background, backgroundOpacity)];
        }
    }

    // MARK: Eyedropper

    public void SampleColor(Vec2 point, bool intoBackground)
    {
        var p = new PixelRect((int)Math.Floor(point.X), (int)Math.Floor(point.Y), 1, 1);
        if (p.Intersection(State.Bounds).Width <= 0) return;
        try
        {
            var (buffer, _, _) = new DocumentCompositor().RenderPixels(State, region: p);
            float a = (float)buffer[3];
            if (!(a > 0)) return;
            double Encode(Half c)
            {
                double v = Math.Min(Math.Max((float)c / a, 0), 1);
                return v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            }
            var color = new SRGBColor(Encode(buffer[0]), Encode(buffer[1]), Encode(buffer[2]));
            if (intoBackground) Background = color; else Foreground = color;
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    // MARK: Selection modify

    public void ModifySelection(CommandID id, double radius)
    {
        if (State.Selection is not { } selection || !(radius > 0)) return;
        SelectionOps.Operation op = id == CommandID.FeatherSelection ? new SelectionOps.Operation.Feather(radius)
            : id == CommandID.GrowSelection ? new SelectionOps.Operation.Grow(radius)
            : new SelectionOps.Operation.Shrink(radius);
        try
        {
            var result = SelectionOps.Apply(op, selection, State.Bounds);
            UiText name = id == CommandID.FeatherSelection ? "Feather" : id == CommandID.GrowSelection ? "Expand Selection" : "Contract Selection";
            Edit(name, s => s with { Selection = result });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    // MARK: Copy, cut, paste, clear

    /// <summary>The active layer's pixels inside the selection.</summary>
    private TileGrid? SelectedPixels()
    {
        if (ActiveLayer is not { IsRaster: true } layer) return null;
        // No selection: the whole layer.
        if (State.Selection is not { } selection) return layer.Tiles;
        if (selection.Bounds(State.Bounds) is not { } bounds) return null;
        var coords = bounds.TileCoords.Where(c => layer.Tiles[c] is not null).ToList();
        var result = new PixelOps().Run(PixelOps.Mode.KeepSelected, layer.Tiles, selection: selection, coords: coords);
        var kept = new TileGrid.Builder();
        foreach (var c in coords) if (result[c] is { } t) kept[c] = t;
        return kept.ToImmutable();
    }

    public void CopySelection(bool cut)
    {
        try
        {
            if (SelectedPixels() is not { } grid) return;
            var bounds = State.Selection is { } sel ? sel.Bounds(State.Bounds) : grid.TileBounds?.Intersection(State.Bounds);
            if (bounds is not { IsEmpty: false } area) return;
            // System clipboard: a picture of the copied area.
            var single = new DocumentState(State.Width, State.Height, layers: [new Layer("copy", new RasterContent(grid))]);
            using (var flat = ImageExporter.FlattenedSRGB(single))
            using (var cropped = new SkiaSharp.SKBitmap())
            {
                if (flat.ExtractSubset(cropped, new SkiaSharp.SKRectI(area.X, area.Y, area.MaxX, area.MaxY)))
                    Environment.Clipboard.SetImage(ToRgbaImage(cropped));
            }
            clipboard = (grid, Environment.Clipboard.SequenceNumber);
            if (cut)
            {
                if (State.Selection is not null) ClearSelectedPixels("Cut");
                else if (ActiveLayer?.Id is { } id) Edit("Cut", s => s.UpdateLayer(id, l => l with { Tiles = TileGrid.Empty }));
            }
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void ClearSelectedPixels() => ClearSelectedPixels("Clear");

    public void ClearSelectedPixels(UiText actionName)
    {
        if (ActiveLayer is not { IsRaster: true } layer || State.Selection is not { } selection || selection.Bounds(State.Bounds) is not { } bounds) return;
        try
        {
            var coords = bounds.TileCoords.Where(c => layer.Tiles[c] is not null).ToList();
            var cleared = new PixelOps().Run(PixelOps.Mode.Erase, layer.Tiles, selection: selection, coords: coords);
            var id = layer.Id;
            Edit(actionName, s => s.UpdateLayer(id, l => l with { Tiles = cleared }));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void LayerViaCopy()
    {
        if (State.Selection is null)
        {
            Perform(CommandID.DuplicateLayer);
            return;
        }
        try
        {
            if (SelectedPixels() is not { } grid) return;
            Edit("New Layer via Copy", s => s.InsertLayerAboveActive(new Layer(s.NextLayerName(Environment.LayerName("Layer")), new RasterContent(grid))));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Glimmer's own pixels are still what the clipboard holds.</summary>
    private bool OwnClipboardIsCurrent => clipboard is { } clip && clip.ChangeCount == Environment.Clipboard.SequenceNumber;

    private bool CanPaste => OwnClipboardIsCurrent || Environment.Clipboard.ContainsImage || Environment.Clipboard.GetImageFiles().Count > 0;

    /// <summary>Pastes Glimmer's own clipboard in place when it's current, otherwise an image from the clipboard.</summary>
    public bool Paste()
    {
        if (OwnClipboardIsCurrent && clipboard is { } clip)
        {
            Edit("Paste", s => s.InsertLayerAboveActive(new Layer(Environment.LayerName("Pasted Layer"), new RasterContent(clip.Grid))));
            return true;
        }
        return PlaceFromClipboard();
    }
}
