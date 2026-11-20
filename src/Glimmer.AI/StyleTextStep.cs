// Ported from Packages/GlimmerKit/Sources/GlimmerAI/StyleTextStep.swift
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// The <c>style_text</c> step on a text layer, apart from finding the layer: which characters,
/// what style, and what to report.
/// </summary>
public static class StyleTextStep
{
    public sealed class Refusal(string reason, string message) : Exception(message), IEquatable<Refusal>
    {
        public string Reason { get; } = reason;
        public bool Equals(Refusal? other) => other is not null && other.Reason == Reason && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as Refusal);
        public override int GetHashCode() => HashCode.Combine(Reason, Message);
    }

    /// <summary>One styled stretch, in characters (grapheme clusters).</summary>
    public readonly record struct CharRange(int From, int Length);

    /// <summary>What the step reports: <c>{"matches": n, "ranges": [{"from", "length"}]}</c>.</summary>
    public sealed record Info(int Matches, IReadOnlyList<CharRange> Ranges)
    {
        /// <summary>The Foundation-like form (<c>matches</c> as an int, <c>ranges</c> as a list of dictionaries), as the Swift step returns it.</summary>
        public Dictionary<string, object?> ToDictionary() => new()
        {
            ["matches"] = Matches,
            ["ranges"] = Ranges.Select(r => (object?)new Dictionary<string, object?> { ["from"] = r.From, ["length"] = r.Length }).ToList(),
        };
    }

    static string? NilIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Styles <paramref name="text"/> as the step says and returns the styled layer and the step's info
    /// (Swift's <c>apply(_:to: inout)</c> mutates; the layer is immutable here, so the new one is returned).
    /// </summary>
    public static (TextLayer Text, Info Info) Apply(AgentStep step, TextLayer text)
    {
        var fontName = NilIfEmpty(step.String("fontName")?.Trim(' ', '\t'));
        bool? bold = step.Get("bold")?.AsBool;
        double? size = step.Number("size");
        var colorText = step.String("color");
        var color = HexColors.Parse(colorText);
        if (colorText is not null && color is null) throw new Refusal("bad_color", "color must be #RRGGBB.");
        if (size is { } sz && !(sz >= 1 && sz <= 10_000)) throw new Refusal("bad_size", "size must be 1–10000 pixels.");
        if (fontName is null && bold is null && size is null && color is null)
            throw new Refusal("nothing_to_style", "Give fontName, bold, size or color.");
        if (fontName is not null && !TextLayer.FontExists(fontName))
            throw new Refusal("unknown_font", $"No font named “{fontName}” is installed (use its PostScript name).");

        IReadOnlyList<TextRange> ranges;
        if (step.String("match") is { Length: > 0 } match)
        {
            bool all = (step.String("occurrence") ?? "all") != "first";
            ranges = text.Ranges(match, all);
            if (ranges.Count == 0) throw new Refusal("no_match", $"“{match}” isn't in the text.");
        }
        else if (step.Number("from") is { } from)
        {
            double length = step.Number("length") ?? 1;
            if (!(from == Math.Round(from, MidpointRounding.AwayFromZero) && length == Math.Round(length, MidpointRounding.AwayFromZero))
                || Math.Abs(from) > int.MaxValue || Math.Abs(length) > int.MaxValue
                || text.Utf16Range((int)from, (int)length) is not { } r)
                throw new Refusal("bad_range",
                    $"from/length is outside the text, which has {CharacterCount(text)} characters (grapheme clusters).");
            ranges = [r];
        }
        else
        {
            // No words named: the whole text, as when nothing is selected.
            if (text.Utf16Range(0, CharacterCount(text)) is not { } r)
                throw new Refusal("bad_range", "The text is empty.");
            ranges = [r];
        }

        var styled = text;
        foreach (var range in ranges)
        {
            if (fontName is not null) styled = styled.SetStyle(range, fontName: fontName);
            if (bold is { } b)
            {
                var (t2, ok) = styled.SetBold(b, range);
                if (!ok) throw new Refusal("unknown_font", $"The font has no {(b ? "bold" : "regular")} member.");
                styled = t2;
            }
            styled = styled.SetStyle(range, size: size, color: color);
        }
        text = styled;
        return (text, new Info(ranges.Count, ranges.Select(r => { var c = text.CharacterRange(r); return new CharRange(c.From, c.Length); }).ToList()));
    }

    /// <summary>Swift's <c>string.count</c>: user-perceived characters (grapheme clusters).</summary>
    static int CharacterCount(TextLayer text) => System.Globalization.StringInfo.ParseCombiningCharacters(text.String).Length;
}
