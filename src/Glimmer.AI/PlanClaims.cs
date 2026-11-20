// Ported from Packages/GlimmerKit/Sources/GlimmerAI/PlanClaims.swift
using System.Text.RegularExpressions;

namespace Glimmer.AI;

/// A sentence saying that something was done must not stand when it was not (SIBLINGS.md,
/// "Påståenden om vad som gjordes"; Jämmer showed "VCA Voices removed." above a step that
/// never ran).
///
/// Glimmer shows the model's sentence above the plan's steps. So:
/// - when every step was left out or refused, the sentence is not shown: only the app's own
///   line about what was not done and why;
/// - when steps remain, the sentence is shown only if it names no operation beyond those
///   steps. Glimmer tells by the words a request names an operation by (`PlanCheck.Words`
///   and `PlanCheck.Kinds`); where a sentence names another operation, or the model planned
///   a step that doesn't exist, the app's own line and the list of steps stand alone;
/// - when a plan stopped part-way, the app's own list of what was done and what was not
///   replaces the sentence.
///
/// The check sees the naming of operations and a small set of completion words, not grammar:
/// "All done!" above steps goes; "Cropped to 16:9." stands, above a list that shows each
/// step's own state.
public static class PlanClaims
{
    /// Stems too common to mean an operation by themselves ("layer" is in most sentences about a document).
    internal static readonly ISet<string> Generic = new HashSet<string>
    {
        "layer", "lager", "text", "size", "storlek", "color", "colour", "färg", "name", "namn", "show", "visa",
        "fix", "better", "auto", "style", "stil", "look", "format", "light", "ljus", "ton", "film", "soft", "mjuk",
        "pop", "stop", "steg", "raw", "scale", "skala", "move", "turn", "pad", "fill", "fyll", "pixel", "effect", "effekt",
        "filter", "level", "cast", "stick", "dark", "bright", "mörk", "width", "height", "bredd", "höjd", "half", "double",
        "bigger", "larger", "smaller", "större", "mindre", "depth", "image", "bild", "balance", "balans", "visible", "synlig",
        "warm", "cool", "cold", "varm", "kall", "tint", "resolution", "upplösning", "hälften", "dubbel", "square", "kvadrat",
    };

    /// The words that name what `step` does.
    internal static List<string> Stems(IPlanStep step)
    {
        var stems = new List<string>();
        if (PlanCheck.Words.TryGetValue(step.Op, out var words)) stems.AddRange(words);
        stems.Add(step.Op);
        stems.Add(step.Op.Replace("_", " "));
        if (step.StringParam("type")?.ToLowerInvariant() is { } kind)
        {
            if (PlanCheck.Kinds.TryGetValue(kind, out var kindWords)) stems.AddRange(kindWords);
            stems.Add(kind);
            stems.Add(kind.Replace("_", " "));
        }
        return stems;
    }

    /// Whether `text` names an operation that is not among `steps`.
    public static bool ClaimsMore(string text, IReadOnlyList<IPlanStep> steps)
    {
        var lower = text.ToLowerInvariant();
        var explained = steps.SelectMany(Stems).ToList();
        bool Stray(IEnumerable<string> stems) =>
            stems.Any(stem => !Generic.Contains(stem) && lower.Contains(stem, StringComparison.Ordinal)
                              && !explained.Any(e => e.Contains(stem, StringComparison.Ordinal) || stem.Contains(e, StringComparison.Ordinal)));
        var ops = new HashSet<string>(steps.Select(s => s.Op));
        var kinds = new HashSet<string>(steps.Select(s => s.StringParam("type")?.ToLowerInvariant()).Where(k => k is not null)!);
        if (PlanCheck.Words.Any(w => !ops.Contains(w.Key) && Stray(w.Value.Concat(new[] { w.Key, w.Key.Replace("_", " ") })))) return true;
        return PlanCheck.Kinds.Any(k => !kinds.Contains(k.Key) && Stray(k.Value));
    }

    /// Words that say the work is finished, English and Swedish. A sentence with one of them above steps
    /// that have not run ("All done!", "Klart!", "I have cropped it") is a false claim in plain sight.
    internal static readonly string[] Completion = { "done", "finished", "completed", "klart", "klar", "färdigt", "färdig", "i have", "i've", "jag har" };

    /// Whether `text` says the work is finished.
    public static bool ClaimsCompletion(string text)
    {
        var lower = text.ToLowerInvariant().Replace("’", "'");
        var words = Regex.Split(lower, @"[^\p{L}']+").Where(w => w.Length > 0);
        var joined = " " + string.Join(" ", words) + " ";
        return Completion.Any(c => joined.Contains(" " + c + " ", StringComparison.Ordinal));
    }

    /// The model's sentence, when it may stand above these steps; null when the app's own line stands alone.
    /// The sentence is shown before the steps run (the plan waits for Run, or runs at once and each step
    /// shows its own state), so one that says the work is finished does not stand either.
    public static string? Shown(string? message, IReadOnlyList<IPlanStep> steps, bool madeUpSteps)
    {
        if (message is null || steps.Count == 0 || madeUpSteps || ClaimsMore(message, steps) || ClaimsCompletion(message)) return null;
        return message;
    }
}
