// Ported from App/AIController+Transform.swift
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>The agent's transform_layer step: Free Transform without the mouse, with the same rules (text stays editable
/// unless corners would rasterize it; layer masks follow the transform).</summary>
public sealed partial class AIController
{
    private void PerformTransformLayer(AgentStep step)
    {
        var s = session;
        if (step.String("target")?.Trim() is { Length: > 0 } key)
        {
            var target = FindLayer(key) ?? throw new StepError($"No layer named “{key}”.");
            s.SetActiveLayer(target.Id);
        }
        var layer = s.ActiveLayer ?? throw new StepError("There's no layer to transform.");
        List<Vec2>? corners = null;
        if (step.Get("corners")?.AsArray is { } list)
        {
            var points = list.Select(v => v.AsArray is { Length: 2 } xy && xy[0].AsNumber is { } x && xy[1].AsNumber is { } y ? new Vec2(x, y) : (Vec2?)null)
                             .OfType<Vec2>().ToList();
            if (points.Count != 4 || points.Count != list.Length)
                throw new StepError("corners needs four [x, y] points: top-left, top-right, bottom-right, bottom-left.", "bad_corners");
            // In the order given the outline must be convex and not cross itself.
            if (!EditorSession.IsConvex(points))
                throw new StepError("These corners cross each other; give them top-left, top-right, bottom-right, bottom-left.", "bad_corners");
            corners = points;
        }
        var errorBefore = s.ErrorMessage;
        s.BeginTransform();
        if (s.TransformSession is not { } t)
        {
            // BeginTransform explains in ErrorMessage (shown in the window); report it here instead.
            var message = s.ErrorMessage?.ToString() ?? "This layer can't be transformed.";
            s.ErrorMessage = errorBefore;
            throw new StepError(message, "cannot_transform");
        }
        if (corners is not null)
        {
            s.TransformSessionValue = t with { Quad = [.. corners], Mode = TransformMode.Distort };
            s.UpdateTransformPreview();
        }
        else
        {
            s.SetTransform(step.Number("scaleX"), step.Number("scaleY"), step.Number("degrees"));
            var move = new Vec2(step.Number("moveX") ?? 0, step.Number("moveY") ?? 0);
            if (move != default && s.TransformSession is { } moved)
            {
                s.TransformSessionValue = moved with { Box = moved.Box with { X = moved.Box.X + move.X, Y = moved.Box.Y + move.Y } };
                s.UpdateTransformPreview();
            }
            double skewX = (step.Number("skewX") ?? 0) * Math.PI / 180, skewY = (step.Number("skewY") ?? 0) * Math.PI / 180;
            if ((skewX != 0 || skewY != 0) && s.TransformSession is { } skewed)
            {
                // Skew moves opposite edges apart, about the box's centre (still affine).
                var c = skewed.Box.Corners;
                var centre = skewed.Box.Centre;
                s.TransformSessionValue = skewed with
                {
                    Quad = [.. c.Select(p => new Vec2(p.X + Math.Tan(skewX) * (p.Y - centre.Y), p.Y + Math.Tan(skewY) * (p.X - centre.X)))],
                    Mode = TransformMode.Skew,
                };
                s.UpdateTransformPreview();
            }
        }
        int rasterizing = s.TextLayersToRasterize;
        if (rasterizing > 0 && step.Get("allowRasterize")?.AsBool != true)
        {
            s.CancelTransform();
            throw new StepError($"{rasterizing} text layer{(rasterizing == 1 ? "" : "s")} would become pixels; pass allowRasterize: true to allow it.",
                                "would_rasterize_text");
        }
        var id = layer.Id;
        s.CommitTransform(confirmed: true);
        if (s.State.LayerById(id) is { } after && AgentRouter.LayerBounds(after) is { } b) StepInfo = new JsonObject { ["bounds"] = RectJson(b) };
    }
}
