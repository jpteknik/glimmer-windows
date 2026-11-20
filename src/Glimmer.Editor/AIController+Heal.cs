// Ported from App/AIController+Heal.swift (GLIMMER_HEAL: Glimmer.Heal is part of every build here)
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Render;
using HealFill = Glimmer.Heal.Heal;

namespace Glimmer.Editor;

/// <summary>Heal Selection: fills the selected area from what surrounds it, on this computer, with no model
/// (Glimmer.Heal).</summary>
public sealed partial class AIController
{
    internal sealed record HealRequest
    {
        public HealFill.Method Method { get; init; } = HealFill.Method.Auto;
        /// <summary>Pixels the selection is grown by first, so an object's edge and halo go with it.</summary>
        public int Expand { get; init; } = 3;
        /// <summary>True: read what is visible, put the fill in a new layer. False: the active pixel layer, in place.</summary>
        public bool SampleAllLayers { get; init; } = true;
        public bool AllowLarge { get; init; }
    }

    /// <summary>Everything the fill needs, read on the UI thread.</summary>
    internal sealed record HealJob(HealFill.Method Method, bool SampleAllLayers, PixelRect Window, PixelRect HoleBounds, float[] Pixels, bool[] Hole,
                                   float[] Weight, double Thickness, int SolidPixels, Guid? LayerID);

    internal sealed record HealResult(HealFill.Method Method, float[] Pixels, int Filled);

    private static StepError HealRefusal(HealFill.Refusal r) => new(r.Message, r.Reason) { Hard = r.Hard };

    private static string MethodName(HealFill.Method method) => method.ToString().ToLowerInvariant();

    /// <summary>Reads the selection and the pixels around it. Throws a StepError with a reason when there is nothing to do.</summary>
    internal HealJob PrepareHeal(HealRequest request)
    {
        var state = session.State;
        var canvas = state.Bounds;
        if (state.Selection is not { } selection || selection.Bounds(canvas) is not { } selected)
            throw new StepError("There's no selection to heal. Select the area to fill first.", "no_selection");
        int expand = Math.Min(50, Math.Max(0, request.Expand));
        var around = new PixelRect(selected.X - expand - 1, selected.Y - expand - 1, selected.Width + 2 * expand + 2, selected.Height + 2 * expand + 2)
            .Intersection(canvas);
        var area = HealFill.MakeArea(selection.Values(around), around.Width, around.Height, expand)
            ?? throw new StepError("The selection is too faint to heal: nothing is selected at least half.", "no_selection");
        if (HealFill.RefusalFor(area.Pixels, area.Thickness, request.AllowLarge) is { } refusal) throw HealRefusal(refusal);
        var holeBounds = new PixelRect(around.X + area.Bounds.X, around.Y + area.Bounds.Y, area.Bounds.Width, area.Bounds.Height);
        int pad = HealFill.Padding(area.Thickness);
        var window = new PixelRect(holeBounds.X - pad, holeBounds.Y - pad, holeBounds.Width + 2 * pad, holeBounds.Height + 2 * pad).Intersection(canvas);

        var hole = new bool[window.Width * window.Height];
        var weight = new float[hole.Length];
        for (int y = 0; y < around.Height; y++)
        {
            for (int x = 0; x < around.Width; x++)
            {
                if (!area.Hole[y * around.Width + x]) continue;
                int i = (around.Y + y - window.Y) * window.Width + (around.X + x - window.X);
                hole[i] = true;
                weight[i] = area.Weight[y * around.Width + x];
            }
        }

        float[] pixels;
        Guid? layerID = null;
        if (request.SampleAllLayers)
        {
            var (buffer, _, rect) = new DocumentCompositor().RenderPixels(state, region: window);
            if (rect != window) throw new InvalidOperationException("The rendered area isn't the area asked for.");
            pixels = new float[buffer.Length];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (float)buffer[i];
        }
        else
        {
            if (state.ActiveLayer is not { IsRaster: true } layer)
                throw new StepError("With sampleAllLayers false, heal_selection works on a pixel layer. Select one, or leave sampleAllLayers on.",
                                    "not_pixel_layer");
            layerID = layer.Id;
            pixels = layer.Tiles.ReadRegion(window);
        }
        return new HealJob(request.Method, request.SampleAllLayers, window, holeBounds, pixels, hole, weight, area.Thickness, area.Pixels, layerID);
    }

    /// <summary>The fill itself; runs anywhere.</summary>
    internal static HealResult Heal(HealJob job, Func<bool> isCancelled)
    {
        var filled = HealFill.Fill(job.Pixels, job.Window.Width, job.Window.Height, job.Hole, job.Thickness, job.Method, isCancelled);
        var b = job.HoleBounds;
        var w = job.Window;
        var output = new float[b.Width * b.Height * 4];
        int count = 0;
        for (int y = 0; y < b.Height; y++)
        {
            for (int x = 0; x < b.Width; x++)
            {
                int i = (b.Y + y - w.Y) * w.Width + (b.X + x - w.X), o = (y * b.Width + x) * 4;
                if (job.Hole[i])
                {
                    count++;
                    float k = job.Weight[i];
                    for (int c = 0; c < 4; c++)
                    {
                        // A new layer carries the fill at the strength of the selection there; in place, the same blend is
                        // done against the layer's own pixel.
                        float v = job.SampleAllLayers ? filled.Pixels[i * 4 + c] * k
                            : job.Pixels[i * 4 + c] + (filled.Pixels[i * 4 + c] - job.Pixels[i * 4 + c]) * k;
                        output[o + c] = (float)(Half)v;
                    }
                }
                else if (!job.SampleAllLayers)
                {
                    for (int c = 0; c < 4; c++) output[o + c] = (float)(Half)job.Pixels[i * 4 + c];
                }
            }
        }
        return new HealResult(filled.Method, output, count);
    }

    /// <summary>Puts the fill into the document as one undo step; returns what the step reports.</summary>
    internal JsonObject ApplyHeal(HealJob job, HealResult result)
    {
        var existing = TileGrid.Empty;
        if (job.LayerID is { } id)
        {
            if (session.State.LayerById(id) is not { IsRaster: true } layer) throw new StepError("The layer being healed is gone.", "unknown_layer");
            existing = layer.Tiles;
        }
        var grid = existing.WriteRegion(job.HoleBounds, result.Pixels);
        Guid layerID;
        if (job.LayerID is { } kept)
        {
            layerID = kept;
            session.Edit("Heal Selection", s => s.UpdateLayer(kept, l => l with { Tiles = grid }));
        }
        else
        {
            var layer = new Layer(LayerName("Heal"), new RasterContent(grid));
            layerID = layer.Id;
            session.Edit("Heal Selection", s => s.InsertLayerAboveActive(layer));
        }
        var info = new JsonObject
        {
            ["method"] = MethodName(result.Method), ["pixels"] = result.Filled, ["width"] = (int)Math.Round(job.Thickness),
            ["layer"] = layerID.ToString().ToUpperInvariant(),
        };
        if (job.Thickness > HealFill.ComfortableWidth) info["quality"] = "may_repeat";
        return info;
    }

    /// <summary>The agent's heal_selection step.</summary>
    private async Task PerformHealSelection(AgentStep step)
    {
        var request = new HealRequest();
        if (step.String("method") is { } name)
        {
            var methods = Enum.GetValues<HealFill.Method>();
            if (!methods.Any(m => MethodName(m) == name))
                throw new StepError($"method must be one of: {string.Join(", ", methods.Select(MethodName))}.", "bad_method");
            request = request with { Method = methods.First(m => MethodName(m) == name) };
        }
        if (step.Number("expand") is { } expand)
        {
            if (!double.IsFinite(expand) || expand < 0 || expand > 50) throw new StepError("expand must be 0…50 pixels.", "bad_expand");
            request = request with { Expand = (int)Math.Round(expand, MidpointRounding.AwayFromZero) };
        }
        request = request with
        {
            SampleAllLayers = step.Get("sampleAllLayers")?.AsBool ?? true,
            AllowLarge = step.Get("allowLarge")?.AsBool ?? false,
        };
        var job = PrepareHeal(request);
        HealResult result;
        try
        {
            result = await Detached(() => Heal(job, () => false));
        }
        catch (HealFill.Refusal refusal)
        {
            throw HealRefusal(refusal);
        }
        StepInfo = ApplyHeal(job, result);
    }

    /// <summary>Edit › Heal Selection. A large area asks first (session.HealWarning).</summary>
    public void HealSelection(bool allowLarge = false)
    {
        try
        {
            var job = PrepareHeal(new HealRequest { AllowLarge = allowLarge });
            RunJob("Heal Selection", token =>
            {
                try
                {
                    return (Heal(job, () => token.IsCancellationRequested), (double?)null);
                }
                catch (HealFill.Refusal refusal)
                {
                    throw new StepError(refusal.Message, refusal.Reason);
                }
            }, result => ApplyHeal(job, result));
        }
        catch (StepError error) when (error.Reason == "area_too_large" && !error.Hard && !allowLarge)
        {
            // In the person's language (the API keeps the English).
            session.HealWarning = PersonMessage.Text(error.Message).ToString();
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }
}
