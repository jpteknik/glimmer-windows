// Ported from App/AIController+TextStyle.swift
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.Editor;

/// <summary>The agent's style_text step: words in a text layer get their own font, boldness, size or colour, as when
/// they're selected in the editor and changed in the options bar.</summary>
public sealed partial class AIController
{
    private void PerformStyleText(AgentStep step)
    {
        var s = session;
        if (step.String("target")?.Trim() is { Length: > 0 } key)
        {
            var target = FindLayer(key) ?? throw new StepError($"No layer named “{key}”.");
            s.SetActiveLayer(target.Id);
        }
        if (s.ActiveLayer is not { Content: TextContent { Text: var text } } layer) throw new StepError("The layer isn't a text layer.", "not_text");
        (TextLayer Text, StyleTextStep.Info Info) styled;
        try
        {
            styled = StyleTextStep.Apply(step, text);
        }
        catch (StyleTextStep.Refusal refusal)
        {
            throw new StepError(refusal.Message, refusal.Reason);
        }
        StepInfo = new JsonObject
        {
            ["matches"] = styled.Info.Matches,
            ["ranges"] = new JsonArray([.. styled.Info.Ranges.Select(r => (JsonNode?)new JsonObject { ["from"] = r.From, ["length"] = r.Length })]),
        };
        var id = layer.Id;
        s.Edit("Style Text", state => state.UpdateLayer(id, l => l with { Content = new TextContent(styled.Text) }));
    }
}
