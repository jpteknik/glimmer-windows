// Ported from App/AIController+Canvas.swift
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>The agent's crop, resize_image, resize_canvas, select_rect and set_layer steps.</summary>
public sealed partial class AIController
{
    private static T Refusing<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (CanvasSteps.Refusal refusal)
        {
            throw new StepError(refusal.Message, refusal.Reason);
        }
    }

    private void PerformCrop(AgentStep step)
    {
        var s = session;
        var box = Refusing(() => CanvasSteps.CropBox(step, s.State.Bounds, s.State.Selection?.Bounds(s.State.Bounds)));
        // An open Crop tool box would be for the old canvas.
        if (s.CropSession is not null) s.CropSessionValue = null;
        s.CropCanvas(box, deletePixels: step.Get("deletePixels")?.AsBool ?? false);
        StepInfo = RectJson(box);
    }

    private void PerformResizeImage(AgentStep step)
    {
        var s = session;
        var size = Refusing(() => CanvasSteps.NewSize(step, s.State.Width, s.State.Height));
        s.ResizeImage(size.Width, size.Height);
        StepInfo = new JsonObject { ["width"] = size.Width, ["height"] = size.Height };
    }

    private void PerformResizeCanvas(AgentStep step)
    {
        var s = session;
        int w = s.State.Width, h = s.State.Height;
        var box = Refusing(() => CanvasSteps.CanvasBox(step, w, h));
        var fillText = step.String("fill");
        var fill = HexColors.Parse(fillText);
        if (fillText is not null && fill is null) throw new StepError("fill must be #RRGGBB.", "bad_color");
        if (s.CropSession is not null) s.CropSessionValue = null;
        if (box.Width != w || box.Height != h) s.CropCanvas(box, deletePixels: false, actionName: "Canvas Size");
        var info = RectJson(box);
        if (fill is { } color)
        {
            if (box.Width > w || box.Height > h)
            {
                var layer = new Layer(LayerName("Canvas Fill"), new FillContent(new Fill.Solid(color)));
                s.Edit("Canvas Size", st => st with { Layers = st.Layers.Insert(0, layer) });
                info["fill"] = "added";
            }
            else
            {
                info["fill"] = "not_needed";
            }
        }
        StepInfo = info;
    }

    private void PerformSelectRect(AgentStep step)
    {
        var s = session;
        var box = Refusing(() => CanvasSteps.Rect(step, s.State.Bounds));
        var r = new CGRectLike(box.X, box.Y, box.MaxX, box.MaxY);
        var shape = step.String("shape") == "ellipse" ? Selection.Ellipse(r) : Selection.Rectangle(r);
        if (step.Number("feather") is { } feather && feather > 0)
            shape = SelectionOps.Apply(new SelectionOps.Operation.Feather(Math.Min(feather, 1000)), shape, s.State.Bounds);
        var mode = step.String("mode") switch
        {
            "add" => Selection.Operation.Add,
            "subtract" => Selection.Operation.Subtract,
            "intersect" => Selection.Operation.Intersect,
            _ => Selection.Operation.Replace,
        };
        s.Edit("Select", state =>
        {
            if (state.Selection is { } current && mode != Selection.Operation.Replace) return state with { Selection = current.Combined(shape, mode) };
            // Nothing selected: adding gives the shape, taking away or intersecting gives nothing.
            return state with { Selection = mode is Selection.Operation.Replace or Selection.Operation.Add ? shape : null };
        });
        StepInfo = RectJson(box);
    }

    private void PerformSetLayer(AgentStep step)
    {
        var s = session;
        Guid id;
        if (step.String("target")?.Trim() is { Length: > 0 } key)
            id = Refusing(() => CanvasSteps.Target(key, [.. s.State.AllLayers.Select(l => (l.Id, l.Name))]));
        else
            id = s.State.ActiveLayerID ?? throw new StepError("There's no active layer.", "unknown_layer");
        var layer = s.State.LayerById(id) ?? throw new StepError("There's no such layer.", "unknown_layer");
        var change = Refusing(() => CanvasSteps.LayerChangeOf(step, layer.IsGroup));
        s.Edit("Layer Properties", state => state.UpdateLayer(id, l => l with
        {
            Opacity = change.Opacity ?? l.Opacity,
            BlendMode = change.BlendMode ?? l.BlendMode,
            IsVisible = change.Visible ?? l.IsVisible,
            Name = change.Name ?? l.Name,
        }));
        StepInfo = new JsonObject { ["layer"] = id.ToString().ToUpperInvariant() };
    }
}
