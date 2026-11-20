// Ported from Packages/GlimmerKit/Sources/GlimmerAI/OnDeviceRouter.swift
using System.Text;

namespace Glimmer.AI;

/// Routes text typed into the omnibar with an on-device language model, before anything is
/// sent to OpenRouter: a request that one editor command covers runs straight away, "make a
/// picture of …" goes to Generate, and everything else goes to the (paid) planner.
///
/// On the Mac this is Apple's Foundation Models (Apple Intelligence). Windows has nothing like
/// it, so the model behind the router is a plug (`IOnDeviceRouteModel`) and the one that ships
/// reports "not available": `IsAvailable` is false and callers fall back to the planner, exactly
/// as on a Mac without Apple Intelligence. The routing logic and its checks are the original's.
public static class OnDeviceRouter
{
    public sealed record Candidate(string Id, string Title, string Subtitle);

    public abstract record Decision
    {
        private Decision() { }

        /// Run this candidate (by id).
        public sealed record Run(string Id) : Decision;
        /// Generate a new image from this prompt.
        public sealed record Generate(string Prompt) : Decision;
        /// Hand the request to the planner.
        public sealed record Assistant : Decision;
    }

    public enum RouteAction { Run, Generate, Assistant }

    /// What the model answers (Swift: `@Generable struct Route`).
    public sealed record Route(
        /// run, generate or assistant
        RouteAction Action,
        /// For run: the id of the one option that does what the user asked. Otherwise an empty string.
        string OptionID,
        /// For generate: a detailed image prompt in English. Otherwise an empty string.
        string Prompt);

    /// Raised by the router when no on-device model is there to ask.
    public sealed class UnavailableException : Exception
    {
        public UnavailableException() : base("No on-device language model is available on this computer.") { }
    }

    /// The model behind the router: asked once, with the fixed instructions and the prompt, for a route.
    /// A real one has to answer in the form of `Route` (an action, an option id, an image prompt).
    public interface IModel
    {
        bool IsAvailable { get; }
        Task<(string Action, string OptionID, string Prompt)> RespondAsync(string instructions, string prompt, CancellationToken ct);
    }

    /// Windows has no Apple Intelligence: always "not available".
    public sealed class UnavailableModel : IModel
    {
        public bool IsAvailable => false;
        public Task<(string Action, string OptionID, string Prompt)> RespondAsync(string instructions, string prompt, CancellationToken ct) =>
            throw new UnavailableException();
    }

    public static IModel Model { get; set; } = new UnavailableModel();

    public static bool IsAvailable => Model.IsAvailable;

    internal const string Instructions = """
You route requests typed into the command bar of a photo editor. Decide one action:
- run: exactly ONE of the listed options fully does what the user asked by itself (for example "invert", "flip horizontal", "new layer", "select all", "black and white", the brush tool). Give its id.
  A general request to fix, improve, correct or enhance the photo ("fix the colors", "make it look better", "improve the photo", "fixa bilden") is the Auto Enhance option when it is listed.
- generate: the user wants a brand-new picture created from a description ("a cat astronaut", "make an image of a castle at sunset"), not a change to the current photo. Write a good prompt.
- assistant: anything else: several steps, specific amounts or settings ("brighten it by 20%", "warmer and with a vignette"), changes to the photo's content ("remove the person", "make it snowy"), or questions.
When in doubt, answer assistant.
""";

    /// Decides what to do with `text`. `candidates` should be the best fuzzy
    /// matches from the command palette (a dozen or so). Throws `UnavailableException` when
    /// there is no on-device model (callers then use the assistant, as `try?` did).
    public static async Task<Decision> RouteAsync(string text, IReadOnlyList<Candidate> candidates, CancellationToken ct = default)
    {
        var options = string.Join("\n", candidates.Select(c => $"- id: {c.Id} — {c.Title} ({c.Subtitle})"));
        var prompt = $"OPTIONS:\n{(options.Length == 0 ? "(none)" : options)}\n\nREQUEST: {text}";
        var (action, optionId, imagePrompt) = await Model.RespondAsync(Instructions, prompt, ct).ConfigureAwait(false);
        var parsed = action.Trim().ToLowerInvariant() switch
        {
            "run" => RouteAction.Run,
            "generate" => RouteAction.Generate,
            _ => RouteAction.Assistant,
        };
        return Decide(new Route(parsed, optionId, imagePrompt), text, candidates);
    }

    /// A request that names more than one thing ("black and white with a vignette",
    /// "warmer, then sharpen") is a plan, never a single command.
    internal static bool IsCompound(string text)
    {
        var lower = " " + text.ToLowerInvariant() + " ";
        var joiners = new[] { ",", ";", " with ", " then ", " plus ", " also ", " med ", " sedan ", " samt ", " och sen " };
        if (joiners.Any(j => lower.Contains(j, StringComparison.Ordinal))) return true;
        // "and" joins two requests unless it is part of a name such as "black and white".
        var withoutNames = lower.Replace("black and white", "bw").Replace("svart och vit", "bw");
        if (withoutNames.Contains(" and ", StringComparison.Ordinal) || withoutNames.Contains(" och ", StringComparison.Ordinal)) return true;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 8;
    }

    /// Validates the model's answer: unknown ids and empty prompts fall back to the
    /// assistant, and so does a single command for a request that asks for several things.
    internal static Decision Decide(Route route, string text, IReadOnlyList<Candidate> candidates)
    {
        switch (route.Action)
        {
            case RouteAction.Run:
            {
                var id = route.OptionID.Trim(' ', '\t');
                if (IsCompound(text)) return new Decision.Assistant();
                return candidates.Any(c => c.Id == id) ? new Decision.Run(id) : new Decision.Assistant();
            }
            case RouteAction.Generate:
            {
                var prompt = route.Prompt.Trim();
                return new Decision.Generate(prompt.Length == 0 ? text : prompt);
            }
            default:
                return new Decision.Assistant();
        }
    }
}
