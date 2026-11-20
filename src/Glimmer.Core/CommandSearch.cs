// Ported from Packages/GlimmerKit/Sources/GlimmerCore/CommandSearch.swift

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>Fuzzy matching for the Ctrl+K palette and the omnibar.</summary>
public static class FuzzyMatch
{
    /// <summary>
    /// Score of <paramref name="query"/> against <paramref name="text"/> (higher is better), null when the query's
    /// characters don't appear in order. Rewards prefixes, word starts and runs.
    /// </summary>
    public static double? Score(string query, string text)
    {
        var q = Characters(query.ToLowerInvariant()).Where(c => !IsWhitespace(c)).ToArray();
        var t = Characters(text.ToLowerInvariant());
        if (q.Length == 0) return 0;
        if (q.Length > t.Length) return null;
        double score = 0;
        int ti = 0, previousMatch = -2, firstMatch = -1;
        foreach (var qc in q)
        {
            bool found = false;
            while (ti < t.Length)
            {
                if (t[ti] == qc)
                {
                    found = true;
                    if (firstMatch < 0) firstMatch = ti;
                    bool atWordStart = ti == 0 || !(IsLetter(t[ti - 1]) || IsNumber(t[ti - 1]));
                    score += 1;
                    if (atWordStart) score += 2;
                    if (ti == previousMatch + 1) score += 1.5;
                    previousMatch = ti;
                    ti++;
                    break;
                }
                ti++;
            }
            if (!found) return null;
        }
        if (firstMatch == 0) score += 3;
        // Prefer shorter candidates for the same matches.
        score -= t.Length * 0.02;
        if (t.Take(q.Length).SequenceEqual(q)) score += 4;
        // A contiguous run ("blur" in "Gaussian Blur") beats scattered word initials.
        string lowered = string.Concat(t), needle = string.Concat(q);
        int r = lowered.IndexOf(needle, StringComparison.Ordinal);
        if (r >= 0)
        {
            score += 4 + q.Length;
            if (r == 0 || !IsLetter(Characters(lowered[..r])[^1])) score += 3;
        }
        return score;
    }

    /// <summary>The string as Swift Characters (grapheme clusters).</summary>
    internal static string[] Characters(string s)
    {
        var result = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) result.Add((string)e.Current);
        return [.. result];
    }

    /// <summary>Swift's <c>Character.isWhitespace</c> and friends look at the character's first scalar.</summary>
    internal static bool IsWhitespace(string c) => Rune.IsWhiteSpace(Rune.GetRuneAt(c, 0));
    internal static bool IsLetter(string c) => Rune.IsLetter(Rune.GetRuneAt(c, 0));
    internal static bool IsNumber(string c) => Rune.IsNumber(Rune.GetRuneAt(c, 0));
}

/// <summary>What a palette item runs (Swift's <c>PaletteItem.Action</c>).</summary>
public abstract record PaletteAction
{
    private protected PaletteAction() { }

    public sealed record Command(CommandID Id) : PaletteAction;
    public sealed record Tool(string Name) : PaletteAction;
    public sealed record Preset(string Id) : PaletteAction;
    /// <summary>A saved assistant plan, by id.</summary>
    public sealed record SavedAction(string Id) : PaletteAction;
}

/// <summary>Something the palette can run.</summary>
public sealed record PaletteItem
{
    public PaletteItem(string id, string title, string subtitle, PaletteAction action, IEnumerable<string>? keywords = null)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Keywords = keywords is null ? [] : [.. keywords];
        Action = action;
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public ImmutableArray<string> Keywords { get; }
    public PaletteAction Action { get; }

    public bool Equals(PaletteItem? other) =>
        other is not null && Id == other.Id && Title == other.Title && Subtitle == other.Subtitle && Action == other.Action
        && Seq.Equal(Keywords, other.Keywords);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Subtitle, Action, Seq.Hash(Keywords));
}

/// <summary>How often and how recently a palette item was used.</summary>
[JsonConverter(typeof(CommandUsageJsonConverter))]
public readonly record struct CommandUsage(int Count, DateTimeOffset Last)
{
    /// <summary>Never used: Swift's <c>CommandUsage()</c> (count 0, last use <c>Date.distantPast</c>).</summary>
    public CommandUsage() : this(0, DateTimeOffset.MinValue) { }

    /// <summary>
    /// Ranking bonus: grows with use (logarithmically) and halves every two weeks without use, so it breaks ties
    /// without burying better matches.
    /// </summary>
    public double Boost(DateTimeOffset now)
    {
        if (Count <= 0) return 0;
        double days = Math.Max(0, (now - Last).TotalSeconds / 86_400);
        return Math.Min(3, 1.2 * Math.Log2(1 + Count)) * Math.Pow(0.5, days / 14);
    }

    /// <summary>Records one use in a usage table, dropping the stalest entries past <paramref name="limit"/>.</summary>
    public static void Record(string id, IDictionary<string, CommandUsage> table, DateTimeOffset? now = null, int limit = 200)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var u = table.TryGetValue(id, out var existing) ? existing : new CommandUsage();
        table[id] = new CommandUsage(u.Count + 1, at);
        if (table.Count <= limit) return;
        var stalest = table.OrderBy(p => p.Value.Boost(at)).ThenBy(p => p.Value.Last).Take(table.Count - limit).Select(p => p.Key).ToList();
        foreach (var key in stalest) table.Remove(key);
    }
}

/// <summary>
/// A <see cref="CommandUsage"/> as Swift's default <c>JSONEncoder</c> writes it: <c>{"count":3,"last":752112000.5}</c>, the
/// date in seconds since 1 January 2001.
/// </summary>
public sealed class CommandUsageJsonConverter : JsonConverter<CommandUsage>
{
    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override CommandUsage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(CommandUsage));
        double seconds = o.ReqDouble("last");
        var last = seconds <= (DateTimeOffset.MinValue - ReferenceDate).TotalSeconds ? DateTimeOffset.MinValue : ReferenceDate.AddSeconds(seconds);
        return new CommandUsage((int)o.ReqInt("count"), last);
    }

    public override void Write(Utf8JsonWriter writer, CommandUsage value, JsonSerializerOptions options) =>
        new JsonFields(options).Int("count", value.Count).Double("last", (value.Last - ReferenceDate).TotalSeconds).WriteTo(writer);
}

public static class CommandSearch
{
    /// <summary>
    /// Ranked matches: the title counts most, then the group, then keywords. <paramref name="usage"/> lifts the things
    /// you use often (see <see cref="CommandUsage.Boost"/>).
    /// </summary>
    public static IReadOnlyList<(PaletteItem Item, double Score)> Search(string query, IReadOnlyList<PaletteItem> items, int limit = 12,
                                                                         IReadOnlyDictionary<string, CommandUsage>? usage = null,
                                                                         DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var trimmed = query.Trim(' ', '\t');
        if (trimmed.Length == 0) return [.. items.Take(limit).Select(i => (i, 0.0))];
        var results = new List<(PaletteItem, double)>();
        foreach (var item in items)
        {
            double? best = FuzzyMatch.Score(trimmed, item.Title);
            if (FuzzyMatch.Score(trimmed, $"{item.Subtitle} {item.Title}") is { } s) best = Math.Max(best ?? double.NegativeInfinity, s * 0.8);
            foreach (var k in item.Keywords)
                if (FuzzyMatch.Score(trimmed, k) is { } ks) best = Math.Max(best ?? double.NegativeInfinity, ks * 0.7);
            if (best is { } b)
            {
                double boost = usage is not null && usage.TryGetValue(item.Id, out var u) ? u.Boost(at) : 0;
                results.Add((item, b + boost));
            }
        }
        return [.. results.OrderByDescending(r => r.Item2).Take(limit)];
    }

    /// <summary>
    /// Options worth showing a language model for a free-text request: matches for the whole text and for each word in
    /// it, best first.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Candidates(string request, IReadOnlyList<PaletteItem> items, int limit = 15)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in FuzzyMatch.Characters(request.ToLowerInvariant()))
        {
            if (FuzzyMatch.IsLetter(c) || FuzzyMatch.IsNumber(c))
            {
                current.Append(c);
                continue;
            }
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
        if (current.Length > 0) words.Add(current.ToString());
        words = words.Where(w => FuzzyMatch.Characters(w).Length >= 3).ToList();

        var best = new Dictionary<string, (PaletteItem Item, double Score)>();
        foreach (var query in words.Prepend(request))
        {
            foreach (var (item, score) in Search(query, items, limit: 8))
            {
                // Normalise by query length so short words don't dominate.
                double s = score / Math.Max(FuzzyMatch.Characters(query).Length, 1);
                if (s > (best.TryGetValue(item.Id, out var b) ? b.Score : double.NegativeInfinity)) best[item.Id] = (item, s);
            }
        }
        return [.. best.Values.OrderByDescending(v => v.Score).Take(limit).Select(v => v.Item)];
    }

    /// <summary>
    /// Whether the text reads as a request for the assistant rather than a command lookup: several words, or no
    /// convincing command match.
    /// </summary>
    public static bool LooksLikeAssistantRequest(string query, double? bestScore)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 4) return true;
        if (bestScore is not { } score) return words.Length >= 2;
        int nonSpace = FuzzyMatch.Characters(query).Count(c => !FuzzyMatch.IsWhitespace(c));
        double perChar = score / Math.Max(nonSpace, 1);
        return words.Length >= 2 && perChar < 2.2;
    }
}
