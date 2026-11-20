// Ported from App/SaveFailures.swift

namespace Glimmer.Editor;

/// <summary>Tells the person why a document couldn't be saved (or copied for recovery).</summary>
/// <remarks>
/// On the Mac the system shows its own sheet for a failed save, and Glimmer's explanation (how much room is needed,
/// that nothing was lost) follows once that sheet is gone, once per document until a save works. Windows has no system
/// sheet: the explanation is the alert. A save the person asked for always says what went wrong; the background
/// recovery copy only the first time, until a save works.
/// </remarks>
public sealed class SaveFailures
{
    private readonly HashSet<string> failing = [];

    /// <summary>The alert for a failed save of <paramref name="name"/>, or null when it was said already and
    /// <paramref name="always"/> is false.</summary>
    public UiText? Failed(string message, string? advice, string name, bool always = true)
    {
        bool first = failing.Add(name);
        if (!first && !always) return null;
        return new UiText("{0}\n\n{1}\n\n{2}", new UiText("The document couldn't be saved"), UiText.Verbatim(message),
                          advice is null ? new UiText("The document is still open and nothing was lost. Try saving it somewhere else.")
                                         : PersonMessage.Text(advice));
    }

    public void Saved(string name) => failing.Remove(name);
}
