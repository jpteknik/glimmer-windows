// Ported from App/PersonMessage.swift
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Glimmer.Editor;

/// <summary>An error's words for a person, in the person's language. The package's refusals (a damaged Photoshop file, a
/// RAW file the system can't develop) are written once, in English, and that is what the agent API answers with. A
/// window says the same thing through the string catalogue when it has it there; a message with a figure or a name in
/// it is shown as it is. (Found 2026-11-16: every such refusal was English in the Swedish app.)</summary>
/// <remarks>On Windows the catalogue is the app's table keyed by the English text with .NET placeholders ({0}); the
/// session hands errors over as <see cref="UiText"/> and the app translates them, with <see cref="Translate"/> for
/// messages that carry values.</remarks>
public static class PersonMessage
{
    /// <summary>The error's English words as a text the app can look up.</summary>
    public static UiText Text(Exception error) => Text(EnglishText(error));

    /// <summary>A text the app can look up; one with braces of its own is shown as it is (it can't be a template).</summary>
    public static UiText Text(string english) =>
        english.Contains('{') || english.Contains('}') ? UiText.Verbatim(english) : new UiText(english);

    /// <summary>LocalizedError.errorDescription, else the description: the exception's Message.</summary>
    public static string EnglishText(Exception error) => error.Message;

    /// <summary>
    /// The translation of an English message: the table's entry when it has the message as it is, otherwise the entry
    /// whose fixed words the message has ("This RAW file is {0}, more than Glimmer keeps in a document."), said with the
    /// same values; the English when neither.
    /// </summary>
    public static string Translate(string english, IReadOnlyDictionary<string, string?> table)
    {
        if (table.TryGetValue(english, out var found) && found is not null) return found;
        foreach (var entry in Patterns(table))
        {
            var match = entry.Pattern.Match(english);
            if (!match.Success) continue;
            var values = Enumerable.Range(1, match.Groups.Count - 1).Select(i => match.Groups[i].Value).ToList();
            return Filled(entry.Translation, entry.Order, values);
        }
        return english;
    }

    private static readonly Regex Place = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);
    private static readonly ConditionalWeakTable<object, IReadOnlyList<PatternEntry>> Cache = new();

    private sealed record PatternEntry(Regex Pattern, string Translation, int[] Order, int Own);

    /// <summary>The translations with placeholders that the table has, as patterns for the English they translate, the
    /// most specific first. (Made once per table and only read afterwards.)</summary>
    private static IReadOnlyList<PatternEntry> Patterns(IReadOnlyDictionary<string, string?> table) => Cache.GetValue(table, _ =>
    {
        var output = new List<PatternEntry>();
        foreach (var (key, translation) in table)
        {
            if (translation is null || !Place.IsMatch(key)) continue;
            var pieces = Place.Split(key);   // the captured numbers sit between the pieces
            var fixedParts = new List<string>();
            var order = new List<int>();
            for (int i = 0; i < pieces.Length; i++)
            {
                if (i % 2 == 0) fixedParts.Add(pieces[i].Replace("{{", "{").Replace("}}", "}"));
                else order.Add(int.Parse(pieces[i], System.Globalization.CultureInfo.InvariantCulture));
            }
            int own = fixedParts.Sum(p => p.Length);
            if (fixedParts.Count < 2 || own < 12) continue;
            var pattern = new Regex("^" + string.Join("(.+?)", fixedParts.Select(Regex.Escape)) + "$",
                                    RegexOptions.CultureInvariant | RegexOptions.Singleline);
            output.Add(new PatternEntry(pattern, translation, [.. order], own));
        }
        return output.OrderByDescending(e => e.Own).ToList();
    });

    /// <summary>A translation with its values put in: {n} takes the value the English had at the place of {n}.</summary>
    private static string Filled(string translation, int[] order, List<string> values)
    {
        var byNumber = new Dictionary<int, string>();
        for (int i = 0; i < order.Length && i < values.Count; i++) byNumber[order[i]] = values[i];
        return Place.Replace(translation, m =>
            byNumber.TryGetValue(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), out var v) ? v : "");
    }
}
