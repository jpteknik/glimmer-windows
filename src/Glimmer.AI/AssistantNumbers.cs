// Ported from Packages/GlimmerKit/Sources/GlimmerAI/AssistantNumbers.swift
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Glimmer.AI;

/// The Assistant never states a number that looks like it comes from Glimmer (a size, a
/// count, a zoom, a colour value, a price, a time) unless Glimmer gave it that number: in
/// the editor state it was sent, in what the person wrote, or in the plan's own steps. A
/// model can state an invented figure with full confidence, and the reader can't tell.
///
/// Sentences with a number nobody gave it are taken out of the Assistant's text, and the
/// plan records what was taken out.
///
/// The rule was written against a model that invents figures; it must not punish one that
/// does the sum (the first real model, 2026-11-16, lost "2400 × 1350" beside a crop to 16:9
/// and "1200 × 800" beside "half as large"). So a figure also counts as given when it is in a
/// closed set of results the app itself works out from the document's size before it reads
/// the answer (`Worked`), or, in a sentence that names an operation or its subject, in that
/// operation's own description, which was sent with the request. The check is membership,
/// never "looks plausible". What is said about the document itself (its megapixels, its shape
/// as a ratio) is backed only by the document's facts and the worked set, each kind of figure
/// only where it is said as that kind: "a 4:3 image" for a 3:2 document goes.
///
/// A figure given in words counts as given: "one and a half" in the request backs "1.5" in
/// the answer (`NumbersInWords`; Jämmer lost true sentences that way, 2026-11-16).
///
/// Free as before: 0, 1 and the numbers up to the count of the plan's steps ("2 steps", a
/// numbered list of the steps), and counting words written as words.
///
/// Held hardest, whatever the arithmetic: money, how long something takes, sizes on disk,
/// quality figures and anything about a service. A sentence about one of those keeps its
/// figure only if the request or the editor state itself gave it.
public static class AssistantNumbers
{
    // Digits with optional thousands groups (space, comma or dot before three digits) and a decimal part.
    private static readonly Regex NumberPattern = new(@"\d{1,3}(?:[  ,.]\d{3})+(?![\d])|\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex GroupedPattern = new(@"^\d{1,3}(?:[  ,.]\d{3})+$", RegexOptions.CultureInvariant);

    /// The numbers written in `text` ("2 400", "2,400" and "2400" are one number; "0,5" is 0.5).
    public static List<double> Numbers(string text)
    {
        var found = new List<double>();
        foreach (Match match in NumberPattern.Matches(text))
        {
            var token = match.Value;
            var grouped = GroupedPattern.IsMatch(token);
            var plain = grouped ? new string(token.Where(char.IsDigit).ToArray()) : token.Replace(",", ".");
            if (double.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) found.Add(value);
        }
        return found;
    }

    /// Said once in an answer that lost a sentence, so it doesn't just read oddly.
    public const string Notice = "Glimmer left out a figure it could not verify.";

    /// `text` with `Notice` after it when something was removed (once, however many sentences went).
    public static string? Noted(string? text, bool removed)
    {
        if (!removed) return text;
        return string.Join(" ", new[] { text, Notice }.Where(p => p is not null));
    }

    /// Words of a claim about cost that carries no figure ("it is free"): nothing the model
    /// can know. The evaluation measures it as a known gap; nothing is removed for it.
    public static bool ClaimsACost(string text)
    {
        var lower = text.ToLowerInvariant();
        return new[] { "free", "gratis", "no cost", "no charge", "costs nothing", "kostar inget", "kostar ingenting", "kostnadsfri", "cheap", "billig", "expensive", "dyr" }
            .Any(w => lower.Contains(w, StringComparison.Ordinal));
    }

    private static readonly string[] Money =
    {
        "$", "€", "£", "kr", "sek", "usd", "eur", "cent", "cents", "credit", "dollar", "euro", "kronor", "krona", "öre", "price", "pris",
    };

    /// Subjects whose figures only the app's own measured or stated values may give.
    private static readonly string[] Held = Money.Concat(new[]
    {
        "second", "seconds", "sekund", "sekunder", "minute", "minutes", "minut", "minuter", "hour", "hours", "timme", "timmar", "ms",
        "kb", "mb", "gb", "byte", "bytes", "kilobyte", "megabyte", "gigabyte", "quality", "kvalitet",
        "openrouter", "berget", "grunden", "openai", "google", "anthropic", "hedra", "ollama",
    }).ToArray();

    /// Whether the sentence has one of the words as a word of its own ("cent" is not in "centred"; "credit" is in "credits").
    private static bool Has(string sentence, IEnumerable<string> words)
    {
        var lower = sentence.ToLowerInvariant();
        var tokens = new HashSet<string>();
        var current = new StringBuilder();
        foreach (var c in lower)
        {
            if (char.IsLetter(c))
            {
                current.Append(c);
            }
            else
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                if (c is '$' or '€' or '£') tokens.Add(c.ToString());
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return words.Any(word => tokens.Contains(word) || (word.Length >= 5 && tokens.Any(t => t.StartsWith(word, StringComparison.Ordinal))));
    }

    /// What counts as given besides the request and the editor state.
    public sealed class Extra
    {
        /// Sizes worked out from the document's: given in any sentence that is not about a held subject.
        public List<double> Sizes { get; set; } = new();
        /// The document's megapixels (rounded as a person would say them): given only beside the word.
        public List<double> Megapixels { get; set; } = new();
        /// The document's shape in lowest terms: given only when written as that ratio ("3:2").
        public (int, int)? Ratio { get; set; }
        /// Each operation's own figures, with the words that name it: given only in a sentence that has one of the words.
        public List<(IReadOnlyList<string> Names, IReadOnlyList<double> Figures)> Operations { get; set; } = new();

        public static Extra None => new();
    }

    private static double RoundAway(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// The figures the app itself works out from a document's size before it reads an answer: a
    /// closed set. Halves, doubles and four times each side; the other side for an aspect ratio
    /// the request names ("16:9", "square"); a percentage the request names, of each side; the
    /// megapixels; and the size as a ratio in lowest terms (2400 × 1600 is 3:2).
    public static Extra Worked(int width, int height, string request)
    {
        var extra = new Extra();
        if (width <= 0 || height <= 0) return extra;
        double w = width, h = height;
        foreach (var factor in new[] { 0.5, 2.0, 4.0 })
            extra.Sizes.AddRange(new[] { RoundAway(w * factor), RoundAway(h * factor) });
        // Aspect ratios the request names: the largest centred box of that shape, as `crop` cuts it.
        var aspects = new List<double>();
        foreach (Match m in Regex.Matches(request, @"(\d+(?:[.,]\d+)?)\s*:\s*(\d+(?:[.,]\d+)?)"))
        {
            if (double.TryParse(m.Groups[1].Value.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(m.Groups[2].Value.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x > 0 && y > 0)
                aspects.Add(x / y);
        }
        var lower = request.ToLowerInvariant();
        if (new[] { "square", "kvadrat" }.Any(w2 => lower.Contains(w2, StringComparison.Ordinal))) aspects.Add(1);
        foreach (var a in aspects)
        {
            double cw = w, ch = RoundAway(w / a);
            if (ch > h) { ch = h; cw = RoundAway(h * a); }
            extra.Sizes.AddRange(new[] { cw, ch });
        }
        foreach (Match m in Regex.Matches(lower, @"(\d+(?:[.,]\d+)?)\s*(?:%|percent|procent)"))
        {
            if (double.TryParse(m.Groups[1].Value.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0)
                extra.Sizes.AddRange(new[] { RoundAway(w * p / 100), RoundAway(h * p / 100) });
        }
        var megapixels = w * h / 1_000_000;
        extra.Megapixels = new List<double> { RoundAway(megapixels), RoundAway(megapixels * 10) / 10, RoundAway(megapixels * 100) / 100 };
        var g = Gcd(width, height);
        if (width / g <= 100 && height / g <= 100) extra.Ratio = (width / g, height / g);
        return extra;
    }

    // MARK: Figures given in words

    private static readonly Dictionary<string, double> Units = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
        ["noll"] = 0, ["en"] = 1, ["ett"] = 1, ["två"] = 2, ["tre"] = 3, ["fyra"] = 4, ["fem"] = 5, ["sex"] = 6, ["sju"] = 7, ["åtta"] = 8, ["nio"] = 9, ["tio"] = 10,
        ["elva"] = 11, ["tolv"] = 12, ["tretton"] = 13, ["fjorton"] = 14, ["femton"] = 15, ["sexton"] = 16, ["sjutton"] = 17, ["arton"] = 18, ["aderton"] = 18, ["nitton"] = 19,
    };

    private static readonly Dictionary<string, double> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["tjugo"] = 20, ["trettio"] = 30, ["fyrtio"] = 40, ["femtio"] = 50, ["sextio"] = 60, ["sjuttio"] = 70, ["åttio"] = 80, ["nittio"] = 90,
    };

    private static readonly HashSet<string> Hundreds = new() { "hundred", "hundra" };
    private static readonly HashSet<string> Thousands = new() { "thousand", "tusen" };
    private static readonly HashSet<string> Halves = new() { "half", "halv", "halva", "halvt", "hälften", "halvera", "halve" };
    private static readonly HashSet<string> Quarters = new() { "quarter", "kvart", "fjärdedel" };
    private static readonly Dictionary<string, double> Doubles = new()
    {
        ["double"] = 2, ["twice"] = 2, ["dubbel"] = 2, ["dubbla"] = 2, ["dubbelt"] = 2, ["triple"] = 3, ["tredubbla"] = 3, ["tredubbel"] = 3,
    };

    /// One number word, also a Swedish one written together ("tjugofem", "tvåhundra").
    private static double? WordValue(string token)
    {
        if (Units.TryGetValue(token, out var u)) return u;
        if (Tens.TryGetValue(token, out var t)) return t;
        foreach (var (ten, value) in Tens)
        {
            if (token.StartsWith(ten, StringComparison.Ordinal) && token.Length > ten.Length
                && Units.TryGetValue(token[ten.Length..], out var unit) && unit >= 1 && unit <= 9)
                return value + unit;
        }
        foreach (var hundred in Hundreds)
        {
            if (token.EndsWith(hundred, StringComparison.Ordinal) && token.Length > hundred.Length
                && Units.TryGetValue(token[..^hundred.Length], out var unit) && unit >= 1 && unit <= 9)
                return unit * 100;
        }
        return null;
    }

    /// The figures a text gives in words, English and Swedish: "one and a half" (1.5), "minus arton" (18, as the
    /// digits are read, without a sign), "twenty-five", "tvåhundra", "half" (0.5), "a quarter" (0.25), "double" (2).
    /// Used for what was GIVEN only: a figure said in words in the request backs the same figure in digits in the
    /// answer. Words in the answer are not figures to check.
    public static List<double> NumbersInWords(string text)
    {
        var tokens = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+").Where(t => t.Length > 0).ToList();
        var found = new List<double>();
        var i = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (Halves.Contains(token)) { found.Add(0.5); i++; continue; }
            if (Quarters.Contains(token)) { found.Add(0.25); i++; continue; }
            if (Doubles.TryGetValue(token, out var d)) { found.Add(d); i++; continue; }
            if (WordValue(token) is not { } value) { i++; continue; }
            i++;
            // "twenty five", "two hundred (and) fifty", "three thousand".
            if (value >= 20 && value < 100 && value % 10 == 0 && i < tokens.Count && Units.TryGetValue(tokens[i], out var unit) && unit >= 1 && unit <= 9) { value += unit; i++; }
            if (i < tokens.Count && Hundreds.Contains(tokens[i]))
            {
                value *= 100; i++;
                if (i < tokens.Count && (tokens[i] == "and" || tokens[i] == "och") && i + 1 < tokens.Count && WordValue(tokens[i + 1]) is not null) i++;
                if (i < tokens.Count && WordValue(tokens[i]) is { } rest && rest < 100)
                {
                    value += rest; i++;
                    if (rest >= 20 && rest % 10 == 0 && i < tokens.Count && Units.TryGetValue(tokens[i], out var unit2) && unit2 >= 1 && unit2 <= 9) { value += unit2; i++; }
                }
            }
            if (i < tokens.Count && Thousands.Contains(tokens[i])) { value *= 1000; i++; }
            // "one and a half", "en och en halv", "ett och ett halvt".
            if (i + 2 < tokens.Count && (tokens[i] == "and" || tokens[i] == "och") && new[] { "a", "en", "ett", "one" }.Contains(tokens[i + 1]) && Halves.Contains(tokens[i + 2]))
            {
                value += 0.5; i += 3;
            }
            found.Add(value);
        }
        return found;
    }

    private static List<string> Sentences(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            current.Append(c);
            var next = i + 1 < text.Length ? text[i + 1] : ' ';
            if (c == '\n' || ((c == '.' || c == '!' || c == '?') && char.IsWhiteSpace(next)))
            {
                parts.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// What may be shown of an answer that is still arriving. `text` is everything received so
    /// far. Finished sentences are checked as in `Checked`; the unfinished one at the end is
    /// shown as it comes only while it holds no digit and no money word, and is otherwise
    /// held back whole until it is finished and checked. A figure the check would remove is
    /// therefore never shown, not even for a moment. (A sentence that gets a digit late
    /// disappears from view until it is finished.)
    public static string ShownWhileArriving(string text, IReadOnlyList<string> given, Extra? extra = null)
    {
        var parts = Sentences(text);
        var pending = "";
        // The last part is finished only when white space follows it: "2400." may go on as "2400.5".
        if (text.Length > 0 && !char.IsWhiteSpace(text[^1]) && parts.Count > 0)
        {
            pending = parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }
        var shown = string.Concat(parts.Where(p => Checked(p, given, extra: extra).Removed.Count == 0));
        if (!pending.Any(char.IsNumber) && !Has(pending, Money)) shown += pending;
        return shown;
    }

    public sealed record CheckedText(string? Text, IReadOnlyList<string> Removed);

    /// `text` without the sentences that state a number not in `given`, and those numbers.
    /// `given`: every text Glimmer or the person supplied. `steps`: how many steps the plan
    /// has, so "step 2" and "3 steps" are allowed.
    /// `pricesOnly`: for text that proposes an edit ("crop to 4:5"), whose numbers are the
    /// proposal itself; only a sentence about money with a figure goes.
    /// `extra`: what counts as given besides `given`, each kind only where it belongs (see `Extra`).
    public static CheckedText Checked(string text, IReadOnlyList<string> given, int steps = 0, bool pricesOnly = false, Extra? extra = null)
    {
        extra ??= Extra.None;
        // What was given in digits, and what was given in words ("one and a half seconds" backs "1.5").
        var backed = new HashSet<double>(given.SelectMany(Numbers));
        backed.UnionWith(given.SelectMany(NumbersInWords));
        // A share given as 0.5 may be said as 50 (percent).
        foreach (var n in backed.ToArray())
            if (n > 0 && n <= 1) backed.Add(RoundAway(n * 100));
        // 0, 1 and the numbers up to the count of the plan's steps are free: "2 steps", a numbered list of the steps.
        for (var n = 0; n <= Math.Max(steps, 1); n++) backed.Add(n);
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var sentence in Sentences(text))
        {
            var stated = Numbers(sentence);
            var allowed = new HashSet<double>(backed);
            // A sentence about money, time, a size on disk, quality or a service keeps only what the request or the editor state gave.
            if (!Has(sentence, Held))
            {
                allowed.UnionWith(extra.Sizes);
                var lower = sentence.ToLowerInvariant();
                if (new[] { "megapixel", "megapixlar", "megapixels" }.Any(w => lower.Contains(w, StringComparison.Ordinal)) || Has(sentence, new[] { "mp" }))
                    allowed.UnionWith(extra.Megapixels);
                // The document's shape counts only written as that ratio; "4:3" for a 3:2 document has neither figure given.
                if (extra.Ratio is { } ratio && Regex.IsMatch(lower, $@"(?<![\d.,]){ratio.Item1}\s*[:∶]\s*{ratio.Item2}(?![\d.,])"))
                {
                    allowed.Add(ratio.Item1);
                    allowed.Add(ratio.Item2);
                }
                // An operation's own figures count where the sentence names the operation or its subject.
                foreach (var operation in extra.Operations)
                {
                    if (operation.Names.Any(name => name.Contains(' ') || name.Contains('_') ? lower.Contains(name, StringComparison.Ordinal) : Has(sentence, new[] { name })))
                        allowed.UnionWith(operation.Figures);
                }
            }
            var unbacked = stated.Where(n => !allowed.Any(a => Math.Abs(a - n) <= Math.Max(0.005, Math.Abs(n) * 0.005))).ToList();
            // Called a price only when the sentence carries a currency or the word.
            var about = Has(sentence, Money) ? "price" : "number";
            if (unbacked.Count == 0 || (pricesOnly && about != "price"))
                kept.Add(sentence);
            else
                removed.AddRange(unbacked.Select(n => $"{about} {Display(n)}"));
        }
        var result = string.Concat(kept).Trim();
        return new CheckedText(result.Length == 0 ? null : result, removed);
    }

    /// `n == n.rounded() ? String(Int(n)) : String(n)`.
    private static string Display(double n) =>
        n == Math.Round(n, MidpointRounding.AwayFromZero) && Math.Abs(n) < 9e18
            ? ((long)n).ToString(CultureInfo.InvariantCulture)
            : n.ToString("R", CultureInfo.InvariantCulture);
}
