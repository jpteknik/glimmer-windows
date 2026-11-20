// Ported from App/AIController+Siblings.swift (launching a sibling app stays with the app: they are Mac apps)
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>Glimmer's side of the series' sibling protocol: the assistant's jammer_* and flimmer_* steps, and add_waveform,
/// which draws Jämmer's sound as a shape layer.</summary>
public sealed partial class AIController
{
    /// <summary>Runs a sibling's operation in that app (one undo step there; nothing changes here).</summary>
    private async Task PerformSiblingTool(AgentStep step)
    {
        var siblings = Siblings;
        await Detached(() => siblings.PerformAsync(step.Op, step.Params));
    }

    /// <summary>Fetches the waveform of a Jämmer document and adds it as a solid fill layer with the waveform as its vector
    /// mask: recolourable, movable and styleable, one undo step.</summary>
    private async Task PerformAddWaveform(AgentStep step)
    {
        var s = session;
        var style = step.String("style") == "wave" ? SiblingWaveform.Style.Wave : SiblingWaveform.Style.Bars;
        int bars = (int)Math.Min(Math.Max(step.Number("bars") ?? 64, 16), 400);
        int columns = style == SiblingWaveform.Style.Bars ? bars : Math.Min(Math.Max(s.State.Width / 6, 64), 400);
        var siblings = Siblings;
        var document = step.String("document");
        var wave = await Detached(() => siblings.WaveformAsync(SiblingApp.Jammer, document, columns));
        var rect = WaveformRect(s.State.Width, s.State.Height, step.Number("width"), step.Number("height"), step.String("position"));
        var path = wave.Path(rect, style);
        var color = HexColors.Parse(step.String("color")) ?? s.Foreground;
        var mask = await Detached(() => path.AsSelection.AsMask);
        var layer = new Layer(path.Name, new FillContent(new Fill.Solid(color)), mask: mask, vectorMask: path);
        s.Edit("Add Waveform", st => st.InsertLayerAboveActive(layer));
    }

    /// <summary>Where the waveform goes: centred horizontally, in the middle, top or bottom band.</summary>
    internal static CGRectLike WaveformRect(double width, double height, double? widthShare, double? heightShare, string? position)
    {
        double w = width * Math.Min(Math.Max(widthShare ?? 0.8, 0.1), 1);
        double h = height * Math.Min(Math.Max(heightShare ?? 0.3, 0.05), 1);
        double margin = height * 0.08;
        double y = position switch
        {
            "top" => Math.Min(margin, height - h),
            "bottom" => Math.Max(height - h - margin, 0),
            _ => (height - h) / 2,
        };
        return new CGRectLike((width - w) / 2, y, (width + w) / 2, y + h);
    }

    /// <summary>The assistant's step failed because a sibling isn't open: offer to open it (only on click).</summary>
    private void NoteSiblingFailure(Exception error)
    {
        if (error is SiblingError { Kind: SiblingErrorKind.NotRunning, App: { } app }) session.OfflineSibling = app;
    }
}
