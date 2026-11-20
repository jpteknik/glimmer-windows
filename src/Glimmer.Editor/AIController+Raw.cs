// Ported from App/AIController+Raw.swift (GLIMMER_RAW: every build here has RAW development)
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>The agent's develop_raw: a RAW layer developed again with other settings.</summary>
public sealed partial class AIController
{
    /// <summary>What the agent API says about a layer's RAW file.</summary>
    internal static JsonObject? DescribeRaw(Layer layer)
    {
        if (layer.Raw is not { } raw) return null;
        return new JsonObject
        {
            ["fileName"] = raw.FileName, ["byteCount"] = raw.Data.Length, ["settings"] = SettingsJson(raw.Settings),
            ["process"] = raw.Settings.Process, ["pixelsAsDeveloped"] = RawLayer.PixelsAreAsDeveloped(layer),
        };
    }

    /// <summary>The settings as an agent gives them: null values are the camera's own (asShot in raw/info).</summary>
    internal static JsonObject SettingsJson(RawSettings s) => new()
    {
        ["exposure"] = s.Exposure, ["shadows"] = s.Shadows, ["highlightRecovery"] = s.HighlightRecovery,
        ["temperature"] = s.Temperature, ["tint"] = s.Tint, ["contrast"] = s.Contrast, ["sharpness"] = s.Sharpness,
        ["luminanceNoise"] = s.LuminanceNoise, ["colorNoise"] = s.ColorNoise, ["lensCorrection"] = s.LensCorrection,
    };

    /// <summary>GET …/layers/{layer}/raw/info: what the file allows, and the settings as they now are.</summary>
    internal static JsonObject RawInfo(Layer layer)
    {
        if (layer.Raw is not { } raw) throw new StepError("This layer has no RAW file. Only a layer opened from a camera's RAW file has one.", "not_raw");
        var info = RawDevelopment.Info(raw.Data);
        var controls = new JsonArray([.. RawRequest.Controls(info).Select(c => (JsonNode?)new JsonObject
        {
            ["name"] = c.Name, ["min"] = c.Range.Lower, ["max"] = c.Range.Upper, ["supported"] = c.Supported, ["asShot"] = c.AsShot,
        })]);
        var switches = new JsonArray([.. RawRequest.Switches(info).Select(c => (JsonNode?)new JsonObject
        {
            ["name"] = c.Name, ["supported"] = c.Supported, ["asShot"] = c.AsShot,
        })]);
        return new JsonObject
        {
            ["fileName"] = raw.FileName, ["byteCount"] = raw.Data.Length, ["width"] = info.Width, ["height"] = info.Height,
            ["hasLensCorrectionData"] = info.SupportsLensCorrection, ["processes"] = new JsonArray([.. info.Processes.Select(p => (JsonNode?)p)]),
            ["controls"] = controls, ["switches"] = switches, ["settings"] = SettingsJson(raw.Settings),
            ["process"] = raw.Settings.Process, ["pixelsAsDeveloped"] = RawLayer.PixelsAreAsDeveloped(layer),
        };
    }

    private async Task PerformDevelopRaw(AgentStep step)
    {
        var s = session;
        Guid id;
        if (step.String("target")?.Trim() is { Length: > 0 } key)
            id = Refusing(() => CanvasSteps.Target(key, [.. s.State.AllLayers.Select(l => (l.Id, l.Name))]));
        else
            id = s.State.ActiveLayerID ?? throw new StepError("There's no active layer.", "unknown_layer");
        var layer = s.State.LayerById(id) ?? throw new StepError("There's no such layer.", "unknown_layer");
        if (layer.Raw is not { } raw)
            throw new StepError($"“{layer.Name}” has no RAW file to develop: it was not opened from a camera's RAW file. Use add_adjustment to change its tone or color.", "not_raw");
        if (!RawLayer.PixelsAreAsDeveloped(layer) && step.Get("replaceChanges")?.AsBool != true)
            throw new StepError($"“{layer.Name}” has been painted on or changed since it was developed, and developing again replaces those changes. Send replaceChanges: true to do it anyway.", "replaces_changes");
        // A name develop_raw doesn't have is refused, not passed over.
        string[] known = ["target", "exposure", "temperature", "tint", "shadows", "contrast", "sharpness", "luminanceNoise", "colorNoise",
                          "highlightRecovery", "lensCorrection", "asShot", "replaceChanges", "rationale"];
        if (step.Params.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !known.Contains(k)) is { } strayName)
            throw new StepError($"develop_raw has no parameter named {strayName}. It has: exposure, temperature, tint, shadows, contrast, sharpness, luminanceNoise, colorNoise, highlightRecovery, lensCorrection, asShot.", "unknown_control");
        var numbers = new Dictionary<string, double>();
        var switches = new Dictionary<string, bool>();
        foreach (var name in new[] { "exposure", "temperature", "tint", "shadows", "contrast", "sharpness", "luminanceNoise", "colorNoise" })
        {
            if (step.Get(name) is not { } value || value.Kind == JsonKind.Null) continue;
            numbers[name] = value.AsNumber ?? throw new StepError($"{name} must be a number.", "bad_value");
        }
        foreach (var name in new[] { "highlightRecovery", "lensCorrection" })
        {
            if (step.Get(name) is not { } value || value.Kind == JsonKind.Null) continue;
            switches[name] = value.AsBool ?? throw new StepError($"{name} must be true or false.", "bad_value");
        }
        bool asShot = step.Get("asShot")?.AsBool == true;
        if (!asShot && numbers.Count == 0 && switches.Count == 0)
            throw new StepError("Give at least one of exposure, temperature, tint, shadows, contrast, sharpness, luminanceNoise, colorNoise, highlightRecovery, lensCorrection or asShot.", "nothing_to_set");
        RawSettings settings;
        try
        {
            settings = RawRequest.Settings(raw.Settings, RawDevelopment.Info(raw.Data), numbers, switches, asShot);
        }
        catch (RawRequest.Refusal refusal)
        {
            throw new StepError(refusal.Message, refusal.Reason);
        }
        var made = await Detached(() => RawLayer.DevelopedTimed(layer, settings));
        // The layer may have gone or changed while the file was developed.
        if (s.State.LayerById(id)?.Raw is null) throw new StepError("The layer was removed while it was being developed.", "unknown_layer");
        s.Edit("Develop RAW", state => state.UpdateLayer(id, _ => made.Layer));
        StepInfo = new JsonObject
        {
            ["layer"] = id.ToString().ToUpperInvariant(), ["settings"] = SettingsJson(made.Layer.Raw?.Settings ?? settings),
            ["process"] = made.Layer.Raw?.Settings.Process, ["seconds"] = Math.Round((made.DecoderSeconds + made.TileSeconds) * 10) / 10,
        };
    }
}
