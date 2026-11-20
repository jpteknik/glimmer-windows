// Ported from Packages/GlimmerKit/Sources/GlimmerAI/LocalModelEvaluation.swift
namespace Glimmer.AI;

/// Glimmer's own test of a text model before it is offered as the Assistant's model
/// (the-apps/PROVIDERS.md, rule 6): everyday requests in English and Swedish, each with the
/// tool call that answers it. A model passes when it gets enough of them right, with no
/// made-up tools, fast enough to wait for.
///
public static class LocalModelEvaluation
{
    public sealed record Case(string Goal, string Language,
                              /// The operation that answers it, and parameter values that must be there (as text; numbers within 1 %).
                              string Op, IReadOnlyDictionary<string, string> Params)
    {
        /// Other parameter sets that give the same result (half the size of the 2400 × 1600 test
        /// document is `scale: 0.5`, and it is also `width: 1200`).
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Alternatives { get; init; } = Array.Empty<IReadOnlyDictionary<string, string>>();
        /// A question to answer in words: no step may be planned, and the answer may hold no
        /// figure that the editor state didn't give (`AssistantNumbers`).
        public bool AnswerOnly { get; init; }
        /// A request that names no edit ("write a poem about the sea"): whatever is answered,
        /// no step may be planned, because nothing in the document was asked to change.
        public bool ChangesNothing { get; init; }
        /// The request names its step plainly, so the plan must be one that runs at once
        /// (`PlanCheck`): the check on unnamed steps must not quietly become "always ask".
        public bool MustRunAtOnce { get; init; }
        /// A known gap, measured and not counted in the verdict: the check sees figures, not
        /// claims, so "it is free" gets through. Held when the answer doesn't call a paid
        /// feature free, cheap or expensive, and points to Settings or says that it costs.
        public bool KnownGap { get; init; }
        /// The active layer is one opened from a camera's RAW file (the editor state says "raw",
        /// and `develop_raw` is among the tools offered).
        public bool RawLayer { get; init; }
        /// An operation the plan must not use (developing a layer that has no RAW file).
        public string? MustNotUse { get; init; }
    }

    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> P(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    /// The cases the verdict counts.
    public static IReadOnlyList<Case> Scored => Cases.Where(c => !c.KnownGap).ToList();

    public static readonly IReadOnlyList<Case> Cases = new List<Case>
    {
        new("Make the photo black and white", "en", "add_adjustment", P(("type", "black_white"))),
        new("Gör bilden svartvit", "sv", "add_adjustment", P(("type", "black_white"))),
        new("Crop the image to 16:9", "en", "crop", P(("aspect", "16:9"))),
        new("Beskär bilden till en kvadrat", "sv", "crop", P(("aspect", "1:1"))),
        new("Remove the background", "en", "remove_background", NoParams),
        new("Ta bort bakgrunden", "sv", "remove_background", NoParams),
        new("Add the text Hello at the top", "en", "add_text", P(("text", "Hello"), ("position", "top"))),
        new("Lägg till texten Hej längst ner", "sv", "add_text", P(("text", "Hej"), ("position", "bottom"))),
        new("Improve the photo automatically", "en", "auto_enhance", NoParams),
        new("Gör bilden hälften så stor", "sv", "resize_image", P(("scale", "0.5")))
        {
            Alternatives = new[] { P(("width", "1200")), P(("height", "800")), P(("width", "1200"), ("height", "800")) },
        },
        new("Make the layer half transparent", "en", "set_layer", P(("opacity", "0.5"))),
        new("Markera motivet", "sv", "select_subject", NoParams),
        new("How big is this document, in pixels?", "en", "", NoParams) { AnswerOnly = true },
        new("Hur många lager har dokumentet och vad kostar det att förstora det?", "sv", "", NoParams) { AnswerOnly = true },
        new("Make it black and white", "en", "add_adjustment", P(("type", "black_white"))) { MustRunAtOnce = true },
        new("Write a poem about the sea", "en", "", NoParams) { ChangesNothing = true },
        new("Berätta ett skämt", "sv", "", NoParams) { ChangesNothing = true },
        new("Thanks, that looks good", "en", "", NoParams) { ChangesNothing = true },
        // Generating goes to a paid service, so "free" would be wrong. (Upscaling was the first
        // question here, and it is in fact done on the Mac at no cost: a bad test of the gap.)
        new("Is generating a new image free?", "en", "", NoParams) { AnswerOnly = true, KnownGap = true },
    };

    /// Requests about a RAW layer. Not part of `Cases`: `develop_raw` is offered only in the full
    /// tool list and only when the active layer is a RAW layer, so these say something only for
    /// a model that gets the full list. The last one is an ordinary layer, where developing must
    /// not be chosen whatever the wording. Written before any model was run on them.
    public static readonly IReadOnlyList<Case> RawCases = new List<Case>
    {
        new("Make it a stop brighter", "en", "develop_raw", P(("exposure", "1"))) { RawLayer = true },
        new("Gör bilden varmare", "sv", "develop_raw", NoParams) { RawLayer = true },
        new("Make it a stop brighter", "en", "", NoParams) { MustNotUse = "develop_raw" },
    };

    public sealed record Outcome(string Goal, string Language, double Seconds, bool Passed,
                                 /// What the model did, for the report.
                                 string Got)
    {
        /// A known gap: shown in the report, left out of the verdict.
        public bool KnownGap { get; init; }
        /// Whether the answer would have passed as the model wrote it, before the figures check took anything out.
        /// (`Passed` is as the person gets it, which is what the verdict counts.)
        public bool PassedAsWritten { get; init; }
    }

    public sealed class Report
    {
        public string Model { get; }
        public IReadOnlyList<Outcome> Outcomes { get; }

        public Report(string model, IEnumerable<Outcome> outcomes)
        {
            Model = model;
            Outcomes = outcomes.ToList();
        }

        /// What the verdict counts, and the known gaps beside it.
        public IReadOnlyList<Outcome> Scored => Outcomes.Where(o => !o.KnownGap).ToList();
        public IReadOnlyList<Outcome> Gaps => Outcomes.Where(o => o.KnownGap).ToList();
        public int Passed => Scored.Count(o => o.Passed);

        /// Passed and total of the counted outcomes in `language`.
        public (int Passed, int Total) PassedIn(string language)
        {
            var some = Scored.Where(o => o.Language == language).ToList();
            return (some.Count(o => o.Passed), some.Count);
        }

        public double Slowest => Scored.Select(o => o.Seconds).DefaultIfEmpty(0).Max();

        public double Median
        {
            get
            {
                var s = Scored.Select(o => o.Seconds).OrderBy(x => x).ToList();
                return s.Count == 0 ? 0 : s[s.Count / 2];
            }
        }

        /// How many of the counted answers were right as the model wrote them, before the figures check.
        public int PassedAsWritten => Scored.Count(o => o.PassedAsWritten);

        /// " As the model wrote them: 18 of 18; as the person gets them: 16 of 18." when the two differ, else nothing.
        public string BothWaysLine =>
            PassedAsWritten == Passed ? "" : $" As the model wrote them: {PassedAsWritten} of {Scored.Count}; as the person gets them: {Passed} of {Scored.Count}.";

        /// "known gaps: 0 of 1 held" for the report's last line.
        public string GapLine => Gaps.Count == 0 ? "" : $" Known gaps (not counted): {Gaps.Count(o => o.Passed)} of {Gaps.Count} held.";

        /// At least five in six right, overall and in each language (15 of 18, 9 of 10 in English,
        /// 7 of 8 in Swedish), and none slower than a minute.
        public bool Passes
        {
            get
            {
                var en = PassedIn("en");
                var sv = PassedIn("sv");
                return Passed * 6 >= Scored.Count * 5 && en.Passed * 6 >= en.Total * 5 && sv.Passed * 6 >= sv.Total * 5 && Slowest <= SlowestAllowed;
            }
        }
    }

    public const double SlowestAllowed = 60.0;

    /// Whether `plan` answers `test`: the right operation with the right values, and nothing made up.
    public static bool Judge(AgentPlan plan, Case test)
    {
        if (test.MustNotUse is { } forbidden) return !plan.Steps.Any(s => s.Op == forbidden) && !plan.DroppedOps.Contains(forbidden);
        if (test.KnownGap)
        {
            var said = plan.Message ?? "";
            // Held: no step, no figure, no "free"/"cheap"/"expensive", and it either points to
            // Settings or says that it costs (which Glimmer's own list of operations states).
            var lower = said.ToLowerInvariant();
            var pointsOrPaid = new[] { "settings", "inställningar", "costs money", "costs credits", "paid", "kostar pengar", "not free", "inte gratis" }.Any(w => lower.Contains(w, StringComparison.Ordinal));
            return plan.Steps.IsEmpty && plan.RemovedFigures.IsEmpty
                && !AssistantNumbers.ClaimsACost(lower.Replace("not free", "").Replace("inte gratis", "")) && pointsOrPaid;
        }
        // Nothing may change without confirmation: no step, or a plan that waits for Run.
        if (test.ChangesNothing) return plan.DroppedOps.IsEmpty && (plan.Steps.IsEmpty || !plan.RunsAtOnce(test.Goal));
        // What was asked for must run at once: the whole plan, or the plan without a self-contained
        // extra that is held back as an offer. The step that runs is the one judged below.
        if (test.MustRunAtOnce && !plan.RunsAtOnce(test.Goal))
        {
            if (plan.WithoutExtras(test.Goal) is not { } split) return false;
            plan = split.Runs;
        }
        if (test.AnswerOnly) return plan.Steps.IsEmpty && plan.DroppedOps.IsEmpty && plan.RemovedFigures.IsEmpty;
        if (!plan.DroppedOps.IsEmpty || plan.Steps.FirstOrDefault(s => s.Op == test.Op) is not { } step) return false;
        // Every step must be a real operation with only parameters that operation has.
        foreach (var s in plan.Steps)
        {
            if (AgentPlanner.Op(s.Op) is not { } op) return false;
            var known = op.Params.Select(p => p.Name).Append("rationale").ToHashSet();
            if (!s.Params.Keys.ToHashSet().IsSubsetOf(known)) return false;
        }
        bool Has(IReadOnlyDictionary<string, string> wanted)
        {
            foreach (var (key, want) in wanted)
            {
                if (double.TryParse(want, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && step.Number(key) is { } got)
                {
                    if (Math.Abs(got - number) > Math.Max(0.01, Math.Abs(number) * 0.01)) return false;
                }
                else if (!string.Equals(step.String(key), want, StringComparison.InvariantCultureIgnoreCase)) return false;
            }
            return true;
        }
        // An alternative must be met exactly: no other sizing parameter beside it.
        var given = step.Params.Keys.Where(k => k != "rationale").ToHashSet();
        return Has(test.Params) || test.Alternatives.Any(a => Has(a) && given.SetEquals(a.Keys));
    }

    static string Display(AgentValue v) => v.Kind switch
    {
        JsonKind.String => v.AsString!,
        JsonKind.Number => v.AsNumber!.Value == Math.Round(v.AsNumber.Value) ? ((long)v.AsNumber.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : v.AsNumber.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        JsonKind.Bool => v.AsBool!.Value ? "true" : "false",
        _ => "…",
    };

    static string Prefix(string s, int n) => s.Length <= n ? s : s[..n];

    public static async Task<Report> RunAsync(ITextModel model, IReadOnlyList<Case>? cases = null, Action<Outcome>? progress = null, CancellationToken ct = default)
    {
        cases ??= Cases;
        var context = new AgentContext(2400, 1600, 1, false, "pixel", "Photo");
        var outcomes = new List<Outcome>();
        foreach (var test in cases)
        {
            var start = DateTime.UtcNow;
            bool passed = false, asWritten = false;
            string got;
            try
            {
                var c = context;
                if (test.RawLayer) c = c with { ActiveLayerKind = "raw", ActiveLayerName = "IMG_0042" };
                var (plan, _) = await AgentPlanner.PlanAsync(test.Goal, c, [], model, ct: ct).ConfigureAwait(false);
                passed = Judge(plan, test);
                // The same answer judged as the model wrote it: what the figures check took out counted as not taken.
                asWritten = Judge(plan with { RemovedFigures = [] }, test);
                got = plan.Steps.IsEmpty ? "no tool call" + (plan.Message is { } m ? ": " + Prefix(m, 240) : "")
                    : string.Join(" ", plan.Steps.Select(s => $"{s.Op}(" + string.Join(", ", s.Params.Where(p => p.Key != "rationale").Select(p => $"{p.Key}: {Display(p.Value)}").Order(StringComparer.Ordinal)) + ")"));
                if (plan.FilledFromRequest > 0) got += " | type filled in from the request";
                if (!plan.RunsAtOnce(test.Goal) && !plan.Steps.IsEmpty)
                    got += plan.WithoutExtras(test.Goal) is { } split ? $" | runs at once without {string.Join(", ", split.Offered.Select(o => o.Op))}, which is offered" : " | waits for Run";
                if (!plan.RemovedFigures.IsEmpty)
                {
                    got += $" | figures nobody gave it: {string.Join(", ", plan.RemovedFigures)}";
                    // The sentence as the model wrote it, so a reader of the test can judge what was held back.
                    if (plan.MessageAsWritten is { } written) got += $" | as the model wrote it: “{Prefix(written, 300)}”";
                }
                if (!plan.DroppedOps.IsEmpty) got += $" | made-up tools: {string.Join(", ", plan.DroppedOps)}";
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                got = "error: " + Prefix(error.Message, 120);
            }
            var outcome = new Outcome(test.Goal, test.Language, (DateTime.UtcNow - start).TotalSeconds, passed, got) { KnownGap = test.KnownGap, PassedAsWritten = asWritten };
            outcomes.Add(outcome);
            progress?.Invoke(outcome);
        }
        return new Report(model.ModelID, outcomes);
    }
}
