// Ported from Packages/GlimmerKit/Sources/GlimmerAI/PlanCheck.swift
namespace Glimmer.AI;

/// A step of a plan, as far as the plan checks look at it: the operation and its text
/// parameters. The Agent port's step type implements it. (In Swift these checks take
/// `AgentStep` and `AgentPlan`, which belong to Agent.swift.)
public interface IPlanStep
{
    /// The operation (`add_text`, `crop`, …).
    string Op { get; }
    /// The step's parameter `key` when it is a string; null otherwise (Swift: `step.string(key)`).
    string? StringParam(string key);
}

/// Whether a request names what a plan would do. A plan made only of the editor's own steps
/// runs at once (Anders's choice: free edits don't wait), but only when every step that
/// changes the document is named by something in the request. "Write a poem about the sea"
/// names no edit: a model that answers it with a text layer gets its plan shown, and it waits
/// for Run. When Glimmer can't tell, it asks. Series rule, 2026-11-16, after Jämmer's
/// assistant planned a change for a request that asked for none.
///
/// Plans are passed as their list of steps; what Swift read off the `AgentPlan` itself (its
/// stray parameters, `StepParameters.stray`) is passed in by the caller.
public static class PlanCheck
{
    /// Steps that leave the document's content as it is (selections, the active layer, the colour to paint with).
    internal static readonly ISet<string> Harmless = new HashSet<string>
    {
        "select_sky", "select_people", "select_subject", "select_all", "deselect", "invert_selection",
        "feather_selection", "select_layer", "select_rect", "set_foreground",
    };

    /// Paid steps wait for Run whatever the request says (`AgentPlan.usesAI`); this check adds nothing for them.
    internal static readonly ISet<string> Paid = new HashSet<string> { "generate_layer", "image_edit_composite", "inpaint_selection" };

    private static readonly Dictionary<string, string[]> Common = new()
    {
        ["auto_enhance"] = new[] { "enhance", "improve", "fix", "better", "auto", "förbättra", "fixa", "bättre", "snygg" },
        ["auto_color"] = new[] { "color", "colour", "färg", "white balance", "vitbalans", "cast", "stick" },
        ["remove_background"] = new[] { "background", "bakgrund", "cut out", "cutout", "frilägg" },
        ["upscale"] = new[] { "upscale", "enlarge", "bigger", "larger", "resolution", "förstora", "större", "upplösning" },
        ["fill"] = new[] { "fill", "fyll" },
        ["new_layer"] = new[] { "layer", "lager" },
        ["transform_layer"] = new[]
        {
            "rotate", "scale", "move", "flip", "skew", "resize", "size", "bigger", "smaller", "tilt", "turn",
            "rotera", "skala", "flytta", "vänd", "vrid", "storlek", "större", "mindre", "luta",
        },
        ["style_text"] = new[] { "bold", "italic", "font", "color", "colour", "size", "underline", "text", "fet", "kursiv", "typsnitt", "färg", "storlek", "understruk" },
        ["crop"] = new[] { "crop", "square", "aspect", "trim", "beskär", "kvadrat", "format" },
        ["resize_image"] = new[]
        {
            "resize", "size", "smaller", "bigger", "larger", "half", "double", "pixel", "scale", "width", "height",
            "storlek", "mindre", "större", "hälften", "dubbel", "skala", "bredd", "höjd",
        },
        ["resize_canvas"] = new[] { "canvas", "border", "margin", "extend", "pad", "arbetsyta", "duk", "kant", "marginal", "utöka" },
        ["set_layer"] = new[]
        {
            "opacity", "transparent", "blend", "hide", "show", "rename", "name", "visible",
            "opacitet", "genomskinlig", "dölj", "visa", "namn", "bland", "synlig",
        },
        ["add_waveform"] = new[] { "waveform", "audio", "sound", "vågform", "ljud" },
        ["add_layer_effect"] = new[] { "shadow", "glow", "stroke", "outline", "effect", "bevel", "skugga", "glöd", "kontur", "effekt" },
        ["apply_preset"] = new[] { "preset", "look", "style", "förinställning", "stil" },
    };

    /// Word stems, English and Swedish, by which a request names an operation.
    public static readonly IReadOnlyDictionary<string, string[]> Words = BuildWords();

    private static IReadOnlyDictionary<string, string[]> BuildWords()
    {
        var all = new Dictionary<string, string[]>(Common);
#if GLIMMER_RAW
        all["develop_raw"] = new[] { "develop", "raw", "exposure", "white balance", "warmer", "cooler", "stop", "framkalla", "exponering", "vitbalans", "varmare", "kallare", "steg" };
#endif
#if GLIMMER_HEAL
        all["heal_selection"] = new[] { "heal", "remove", "erase", "retouch", "laga", "ta bort", "retusch" };
#endif
        return all;
    }

    /// How a request names a kind of adjustment or filter, beyond the kind's own name.
    internal static readonly IReadOnlyDictionary<string, string[]> Kinds = new Dictionary<string, string[]>
    {
        ["black_white"] = new[] { "black and white", "black & white", "black-and-white", "b&w", "monochrome", "grayscale", "greyscale", "svartvit", "svart-vit", "gråskala" },
        ["brightness_contrast"] = new[] { "bright", "dark", "contrast", "light", "ljus", "mörk", "kontrast" },
        ["exposure"] = new[] { "exposure", "bright", "dark", "exponering", "ljus", "mörk" },
        ["levels"] = new[] { "level", "nivå" },
        ["curves"] = new[] { "curve", "kurv" },
        ["hue_saturation"] = new[] { "hue", "saturat", "colorful", "colourful", "color", "colour", "mättnad", "mätta", "nyans", "färg" },
        ["vibrance"] = new[] { "vibran", "saturat", "color", "colour", "pop", "vivid", "färg", "mättnad", "livfull" },
        ["color_balance"] = new[] { "balance", "warm", "cool", "cold", "tint", "varm", "kall", "balans", "ton" },
        ["photo_filter"] = new[] { "warm", "cool", "sepia", "filter", "varm", "kall" },
        ["channel_mixer"] = new[] { "channel", "kanal" },
        ["invert"] = new[] { "invert", "negative", "negativ" },
        ["posterize"] = new[] { "poster" },
        ["threshold"] = new[] { "threshold", "tröskel" },
        ["gradient_map"] = new[] { "gradient", "duotone", "toning" },
        ["gaussian_blur"] = new[] { "blur", "soft", "oskärpa", "suddig", "mjuk" },
        ["lens_blur"] = new[] { "blur", "bokeh", "depth", "background", "oskärpa", "bakgrund", "skärpedjup" },
        ["sharpen"] = new[] { "sharp", "crisp", "skärp" },
        ["vignette"] = new[] { "vignett" },
        ["grain"] = new[] { "grain", "noise", "film", "korn", "brus" },
    };

    private static bool Has(string request, IEnumerable<string> stems) => stems.Any(s => request.Contains(s, StringComparison.Ordinal));

    /// Whether `request` names what `step` does. False for anything Glimmer can't tell.
    public static bool Names(string request, IPlanStep step, Func<string, string?>? commandTitle = null)
    {
        request = request.ToLowerInvariant();
        switch (step.Op)
        {
            case "add_text":
            {
                // The text to add must be in the request itself ("add the text Hello"): a text the model wrote is not.
                var text = step.StringParam("text")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(text)) return false;
                return request.Contains(text, StringComparison.Ordinal);
            }
            case "add_adjustment":
            case "apply_filter":
            {
                var kind = step.StringParam("type")?.ToLowerInvariant();
                if (string.IsNullOrEmpty(kind)) return false;
                if (Kinds.TryGetValue(kind, out var stems) && Has(request, stems)) return true;
                // The kind's own name, word for word ("channel mixer", "posterize").
                var own = kind.Split('_').Where(p => p.Length >= 4).ToList();
                return own.Count > 0 && own.All(p => request.Contains(p, StringComparison.Ordinal));
            }
            case "apply_preset":
            {
                var name = step.StringParam("name")?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(name) && request.Contains(name, StringComparison.Ordinal)) return true;
                return Has(request, Words["apply_preset"]);
            }
            case "crop":
            {
                if (step.StringParam("aspect") is { } aspect && request.Contains(aspect, StringComparison.Ordinal)) return true;
                return Has(request, Words["crop"]);
            }
            case "run_command":
            {
                if (step.StringParam("id") is not { } id || commandTitle?.Invoke(id) is not { } title) return false;
                return NamesCommand(request, title, Array.Empty<string>());
            }
            default:
                return Words.TryGetValue(step.Op, out var words) && Has(request, words);
        }
    }

    /// Operations that stand by themselves: no selection, no settings, nothing later needs them.
    /// When one of these is the only thing in a plan the request didn't ask for, it is held
    /// back as an offer and the rest of the plan runs. (A local model adds Auto Enhance to
    /// "make it black and white" every time; telling it not to in the prompt changed nothing.)
    public static readonly ISet<string> SelfContained = new HashSet<string> { "auto_enhance", "auto_color" };

    /// The plan's steps without its unasked-for extras, and the extras, when that split is safe: every
    /// unnamed step is self-contained and what is left still changes the document. Null when the plan is
    /// named throughout (nothing to split) or holds another unnamed step (it waits whole).
    public static (List<T> Runs, List<T> Offered)? WithoutExtras<T>(IReadOnlyList<T> steps, string request, Func<string, string?>? commandTitle = null)
        where T : class, IPlanStep
    {
        var extras = Unnamed(steps, request, commandTitle);
        if (extras.Count == 0 || !extras.All(e => SelfContained.Contains(e.Op))) return null;
        var rest = steps.Where(s => !extras.Any(e => ReferenceEquals(e, s))).ToList();
        if (!rest.Any(s => !Harmless.Contains(s.Op))) return null;
        return (rest, extras);
    }

    /// The kinds of adjustment or filter that the request's words name.
    internal static List<string> KindsNamed(string request, IEnumerable<string> valid)
    {
        request = request.ToLowerInvariant();
        return valid.Where(kind =>
        {
            if (Kinds.TryGetValue(kind, out var stems) && Has(request, stems)) return true;
            var own = kind.Split('_').Where(p => p.Length >= 4).ToList();
            return own.Count > 0 && own.All(p => request.Contains(p, StringComparison.Ordinal));
        }).ToList();
    }

    /// Said in a step whose missing value was taken from the request.
    public const string FilledNote = "type taken from your request";

    /// A step that needs a kind and came without one (or with one that doesn't exist) gets it
    /// from the request, when the request's words name exactly one kind. "Make it black and
    /// white" with an adjustment and no type means Black & White. Two kinds that fit ("bright"
    /// is Brightness/Contrast and Exposure) fill in nothing: the step then fails as it would.
    /// `fill` returns the step with `type` set to the kind and `FilledNote` added to its rationale.
    /// Returns the steps and how many were filled in.
    public static (List<T> Steps, int Filled) Completed<T>(IReadOnlyList<T> steps, string request, IReadOnlyList<string> adjustments, IReadOnlyList<string> filters,
                                                           Func<T, string> typeOf, Func<T, string, T> fill)
        where T : IPlanStep
    {
        var result = new List<T>();
        var filled = 0;
        foreach (var step in steps)
        {
            IReadOnlyList<string> valid;
            switch (step.Op)
            {
                case "add_adjustment": valid = adjustments; break;
                case "apply_filter": valid = filters; break;
                default: result.Add(step); continue;
            }
            if (step.StringParam("type") is { } given && valid.Contains(given)) { result.Add(step); continue; }
            var named = KindsNamed(request, valid);
            if (named.Count != 1) { result.Add(step); continue; }
            result.Add(fill(step, named[0]));
            filled++;
        }
        return (result, filled);
    }

    /// Whether a plan may run without being shown first: every step that changes the document
    /// is named by the request. (A plan with a paid step waits anyway.) A step with a parameter
    /// its operation doesn't have would be refused: such a plan is shown, never run at once; the
    /// caller says whether the plan or a step has one (`StepParameters.stray`).
    public static bool RunsAtOnce<T>(IReadOnlyList<T> steps, string request, Func<string, string?>? commandTitle = null,
                                     bool planHasStrayParameters = false, Func<T, bool>? stepHasStray = null)
        where T : IPlanStep
    {
        if (planHasStrayParameters) return false;
        if (stepHasStray is not null && steps.Any(stepHasStray)) return false;
        return steps.All(s => Harmless.Contains(s.Op) || Paid.Contains(s.Op) || Names(request, s, commandTitle));
    }

    /// The steps the request doesn't name, for telling the person why the plan waits.
    public static List<T> Unnamed<T>(IReadOnlyList<T> steps, string request, Func<string, string?>? commandTitle = null)
        where T : IPlanStep =>
        steps.Where(s => !Harmless.Contains(s.Op) && !Paid.Contains(s.Op) && !Names(request, s, commandTitle)).ToList();

    /// Whether a request names a single command (the on-device router's choice): the command's
    /// title, one of its search words, or a word of its title of four letters or more.
    public static bool NamesCommand(string request, string title, IEnumerable<string> keywords)
    {
        request = request.ToLowerInvariant();
        title = title.ToLowerInvariant();
        if (request.Contains(title, StringComparison.Ordinal)
            || keywords.Any(k => k.Length > 0 && request.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
            return true;
        var titleWords = System.Text.RegularExpressions.Regex.Split(title, @"[^\p{L}]+").Where(w => w.Length >= 4);
        return titleWords.Any(w => request.Contains(w, StringComparison.Ordinal));
    }
}
