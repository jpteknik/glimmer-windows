// Ported from Packages/GlimmerKit/Sources/GlimmerCore/TextStyles.swift

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// A range of UTF-16 code units (Swift's <c>NSRange</c>): exactly the indices of a C# string.
/// </summary>
public readonly record struct TextRange(int Location, int Length)
{
    public int End => Location + Length;

    /// <summary><c>NSIntersectionRange</c>: the overlap, or (0, 0) when there is none.</summary>
    public static TextRange Intersection(TextRange a, TextRange b)
    {
        int lo = Math.Max(a.Location, b.Location), hi = Math.Min(a.End, b.End);
        return hi > lo ? new TextRange(lo, hi - lo) : new TextRange(0, 0);
    }
}

/// <summary>
/// A stretch of a text layer styled differently from the rest of it: a word in bold, another colour or size.
/// <see cref="Location"/> and <see cref="Length"/> count UTF-16 code units of <see cref="TextLayer.String"/> (the
/// offsets of NSString, and of a C# string). A null field keeps the layer's own value.
/// </summary>
[JsonConverter(typeof(TextStyleRunJsonConverter))]
public sealed record TextStyleRun(int Location, int Length, string? FontName = null, double? Size = null, SRGBColor? Color = null)
{
    public TextRange Range => new(Location, Length);
    internal bool IsPlain => FontName is null && Size is null && Color is null;
}

/// <summary>The font, size and colour a character of a text layer is drawn with.</summary>
public sealed record TextStyle(string FontName, double Size, SRGBColor Color);

public sealed partial record TextLayer
{
    /// <summary>The layer's own style, used wherever no run says otherwise.</summary>
    public TextStyle BaseStyle => new(FontName, Size, Color);

    /// <summary>Whether some characters are styled differently from the layer.</summary>
    public bool HasStyledRuns => Styles is { IsEmpty: false };

    private int UnitCount => String.Length;

    private readonly record struct Override(string? FontName = null, double? Size = null, SRGBColor? Color = null);

    private Override[] OverridesPerUnit()
    {
        var units = new Override[UnitCount];
        foreach (var run in Styles ?? [])
        {
            int lo = Math.Max(0, run.Location), hi = Math.Min(units.Length, run.Location + run.Length);
            for (int i = lo; i < hi; i++)
            {
                if (run.FontName is { } f) units[i] = units[i] with { FontName = f };
                if (run.Size is { } s) units[i] = units[i] with { Size = s };
                if (run.Color is { } c) units[i] = units[i] with { Color = c };
            }
        }
        return units;
    }

    /// <summary>Collapses per-unit overrides into runs, leaving out what equals the layer's own style.</summary>
    private TextLayer WithOverrides(Override[] units)
    {
        Override Plain(Override o) => new(o.FontName == FontName ? null : o.FontName, o.Size == Size ? null : o.Size,
                                          o.Color == Color ? null : o.Color);
        var runs = new List<TextStyleRun>();
        int i = 0;
        while (i < units.Length)
        {
            var o = Plain(units[i]);
            int j = i + 1;
            while (j < units.Length && Plain(units[j]) == o) j++;
            var run = new TextStyleRun(i, j - i, o.FontName, o.Size, o.Color);
            if (!run.IsPlain) runs.Add(run);
            i = j;
        }
        return this with { Styles = runs.Count == 0 ? null : [.. runs] };
    }

    /// <summary>The style of the character at a UTF-16 offset (the last character at the end of the text).</summary>
    public TextStyle StyleAt(int offset)
    {
        var s = BaseStyle;
        int n = UnitCount;
        if (n == 0) return s;
        int i = Math.Min(Math.Max(offset, 0), n - 1);
        foreach (var run in Styles ?? [])
        {
            if (i < run.Location || i >= run.Location + run.Length) continue;
            if (run.FontName is { } f) s = s with { FontName = f };
            if (run.Size is { } v) s = s with { Size = v };
            if (run.Color is { } c) s = s with { Color = c };
        }
        return s;
    }

    /// <summary>The whole text cut into stretches of one style each, in order.</summary>
    public IReadOnlyList<(TextRange Range, TextStyle Style)> ResolvedRuns()
    {
        var units = OverridesPerUnit();
        var output = new List<(TextRange, TextStyle)>();
        int i = 0;
        while (i < units.Length)
        {
            int j = i + 1;
            while (j < units.Length && units[j] == units[i]) j++;
            var o = units[i];
            output.Add((new TextRange(i, j - i), new TextStyle(o.FontName ?? FontName, o.Size ?? Size, o.Color ?? Color)));
            i = j;
        }
        return output;
    }

    /// <summary>Restyles part of the text; null arguments leave that property as it is.</summary>
    public TextLayer SetStyle(TextRange range, string? fontName = null, double? size = null, SRGBColor? color = null)
    {
        var units = OverridesPerUnit();
        int lo = Math.Max(0, range.Location), hi = Math.Min(units.Length, range.Location + range.Length);
        if (lo >= hi) return this;
        for (int i = lo; i < hi; i++)
        {
            if (fontName is not null) units[i] = units[i] with { FontName = fontName };
            if (size is { } s) units[i] = units[i] with { Size = Math.Max(s, 1) };
            if (color is { } c) units[i] = units[i] with { Color = c };
        }
        return WithOverrides(units);
    }

    /// <summary>
    /// Sets the text and the style of every stretch at once (what the text editor holds when it closes). Only what
    /// differs from the layer's own style is kept.
    /// </summary>
    public TextLayer SetStyledText(string newString, IEnumerable<(TextRange Range, TextStyle Style)> runs)
    {
        var layer = this with { String = newString };
        var units = new Override[layer.UnitCount];
        foreach (var (range, style) in runs)
        {
            int lo = Math.Max(0, range.Location), hi = Math.Min(units.Length, range.Location + range.Length);
            for (int i = lo; i < hi; i++) units[i] = new Override(style.FontName, style.Size, style.Color);
        }
        return layer.WithOverrides(units);
    }

    /// <summary>
    /// After a change to the whole layer: drops the per-word values of the properties that changed, so the change
    /// shows everywhere (as in Photoshop with the layer selected).
    /// </summary>
    public TextLayer DropStyles(TextLayer changedFrom)
    {
        if (Styles is not { } runs) return this;
        var old = changedFrom;
        var dropped = runs.Select(r => r with
        {
            FontName = FontName != old.FontName ? null : r.FontName,
            Size = Size != old.Size ? null : r.Size,
            Color = Color != old.Color ? null : r.Color,
        }).ToImmutableArray();
        var layer = this with { Styles = dropped };
        return layer.WithOverrides(layer.OverridesPerUnit());
    }

    /// <summary>
    /// Keeps the runs over the same characters after the text changed from <paramref name="old"/> to
    /// <see cref="String"/> without the editor (an agent, a script): characters that stayed keep their style, and
    /// inserted text takes the style of the character before it.
    /// </summary>
    public TextLayer CarryStyles(string old)
    {
        if (!HasStyledRuns) return this;
        string a = old, b = String;
        var oldUnits = (this with { String = old }).OverridesPerUnit();
        var (removed, inserted) = MyersDiff.Changes(a, b);
        var units = new List<Override>(b.Length);
        int i = 0;
        for (int j = 0; j < b.Length; j++)
        {
            while (i < a.Length && removed.Contains(i)) i++;
            if (inserted.Contains(j))
            {
                units.Add(units.Count > 0 ? units[^1] : (i < oldUnits.Length ? oldUnits[i] : default));
            }
            else
            {
                units.Add(i < oldUnits.Length ? oldUnits[i] : default);
                i++;
            }
        }
        return WithOverrides([.. units]);
    }

    // MARK: Character ranges (for agents)

    /// <summary>
    /// The UTF-16 range of <paramref name="length"/> characters from character <paramref name="from"/>. Characters
    /// are user-perceived characters (grapheme clusters: 👍🏽 or a decomposed å counts as one). Null when outside the text.
    /// </summary>
    public TextRange? Utf16Range(int from, int length)
    {
        var starts = StringInfo.ParseCombiningCharacters(String);
        int count = starts.Length;
        if (from < 0 || length <= 0 || from + length > count) return null;
        int lo = starts[from];
        int hi = from + length < count ? starts[from + length] : String.Length;
        return new TextRange(lo, hi - lo);
    }

    /// <summary>A UTF-16 range as characters from the start (grapheme clusters).</summary>
    public (int From, int Length) CharacterRange(TextRange range)
    {
        if (range.Location < 0 || range.Length < 0 || range.End > String.Length) return (0, 0);
        var starts = StringInfo.ParseCombiningCharacters(String);
        // Swift's Range(_:in:) rounds an index inside a character down to the character's start.
        int CharacterIndex(int utf16)
        {
            if (utf16 >= String.Length) return starts.Length;
            int k = Array.BinarySearch(starts, utf16);
            return k >= 0 ? k : ~k - 1;
        }
        int from = CharacterIndex(range.Location);
        return (from, CharacterIndex(range.End) - from);
    }

    /// <summary>
    /// Where <paramref name="match"/> occurs, as UTF-16 ranges, whole characters only (so "a" doesn't match inside
    /// "å"). Exact text first; if that finds nothing, ignoring case.
    /// </summary>
    public IReadOnlyList<TextRange> Ranges(string match, bool all)
    {
        var hay = Characters(String);
        var needle = Characters(match);
        if (needle.Length == 0 || needle.Length > hay.Length) return [];

        List<(int From, int Count)> Search(Func<string, string, bool> same)
        {
            var found = new List<(int, int)>();
            int i = 0;
            while (i <= hay.Length - needle.Length)
            {
                bool hit = true;
                for (int k = 0; k < needle.Length && hit; k++) hit = same(hay[i + k], needle[k]);
                if (hit)
                {
                    found.Add((i, needle.Length));
                    if (!all) break;
                    i += needle.Length;
                }
                else
                {
                    i++;
                }
            }
            return found;
        }

        var hits = Search(SameCharacter);
        if (hits.Count == 0) hits = Search((x, y) => SameCharacter(x.ToLowerInvariant(), y.ToLowerInvariant()));
        var result = new List<TextRange>();
        foreach (var (from, count) in hits)
            if (Utf16Range(from, count) is { } r) result.Add(r);
        return result;
    }

    /// <summary>Swift's <c>Character ==</c>: canonical equivalence (a precomposed å equals a + ring).</summary>
    private static bool SameCharacter(string a, string b) =>
        string.Equals(a.Normalize(NormalizationForm.FormC), b.Normalize(NormalizationForm.FormC), StringComparison.Ordinal);

    private static string[] Characters(string s)
    {
        var starts = StringInfo.ParseCombiningCharacters(s);
        var result = new string[starts.Length];
        for (int i = 0; i < starts.Length; i++)
        {
            int end = i + 1 < starts.Length ? starts[i + 1] : s.Length;
            result[i] = s[starts[i]..end];
        }
        return result;
    }

    /// <summary>
    /// Whether a font with this PostScript name is installed (exactly that name, as Core Text's
    /// <c>CTFontCopyPostScriptName(CTFontCreateWithName(name)) == name</c> asks on the Mac).
    /// </summary>
    public static bool FontExists(string name) => FontResolver.Exists(name);

    /// <summary>
    /// Makes the letters in <paramref name="range"/> bold (or not bold) within their own font family. Returns the
    /// layer unchanged and false when a family has no such member.
    /// </summary>
    public (TextLayer Text, bool Changed) SetBold(bool bold, TextRange range)
    {
        var changes = new List<(TextRange Part, string Name)>();
        foreach (var (runRange, style) in ResolvedRuns())
        {
            var part = TextRange.Intersection(runRange, range);
            if (part.Length <= 0) continue;
            if (FontResolver.IsBold(style.FontName) == bold) continue;
            if (FontResolver.WithBold(style.FontName, bold) is not { } other) return (this, false);
            changes.Add((part, other));
        }
        var result = this;
        foreach (var (part, name) in changes) result = result.SetStyle(part, fontName: name);
        return (result, true);
    }

    /// <summary>The largest font size in the text.</summary>
    public double LargestSize => (Styles ?? []).Where(s => s.Size is not null).Select(s => s.Size!.Value).Aggregate(Size, Math.Max);
}

/// <summary>
/// The difference between two UTF-16 strings as Swift's <c>BidirectionalCollection.difference(from:)</c> computes it
/// (Myers' algorithm, ported from the standard library so that the same characters count as kept).
/// </summary>
internal static class MyersDiff
{
    /// <summary>The offsets removed from <paramref name="a"/> and inserted into <paramref name="b"/>.</summary>
    public static (HashSet<int> Removed, HashSet<int> Inserted) Changes(string a, string b)
    {
        var trace = Descent(a, b);
        var removed = new HashSet<int>();
        var inserted = new HashSet<int>();
        int x = a.Length, y = b.Length;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            int k = x - y;
            int prevK = (k == -d || (k != d && v[k - 1] < v[k + 1])) ? k + 1 : k - 1;
            int prevX = v[prevK];
            int prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                x--;
                y--;
            }
            if (y != prevY) inserted.Add(prevY);
            else removed.Add(prevX);
            x = prevX;
            y = prevY;
        }
        return (removed, inserted);
    }

    /// <summary>V is indexed from -d...d; negative indexes are interleaved into the slots between the positive ones.</summary>
    private sealed class V(int maxIndex)
    {
        private readonly int[] a = new int[maxIndex + 1];

        private static int Transform(int index) => index <= 0 ? -index : index - 1;

        public int this[int index]
        {
            get => a[Transform(index)];
            set => a[Transform(index)] = value;
        }
    }

    private static List<V> Descent(string a, string b)
    {
        int n = a.Length, m = b.Length, max = n + m;
        var result = new List<V>();
        var v = new V(1);
        v[1] = 0;
        int x = 0, y = 0;
        for (int d = 0; d <= max; d++)
        {
            var prev = v;
            result.Add(v);
            v = new V(d);
            bool done = false;
            for (int k = -d; k <= d; k += 2)
            {
                if (k == -d)
                {
                    x = prev[k + 1];
                }
                else
                {
                    int km = prev[k - 1];
                    if (k != d)
                    {
                        int kp = prev[k + 1];
                        x = km < kp ? kp : km + 1;
                    }
                    else
                    {
                        x = km + 1;
                    }
                }
                y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[k] = x;
                if (x >= n && y >= m)
                {
                    done = true;
                    break;
                }
            }
            if (done) break;
        }
        return result;
    }
}

public sealed class TextStyleRunJsonConverter : JsonConverter<TextStyleRun>
{
    public override TextStyleRun Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = SwiftJson.ExpectObject(SwiftJson.ReadElement(ref reader), nameof(TextStyleRun));
        return new TextStyleRun((int)o.ReqInt("location"), (int)o.ReqInt("length"), o.OptString("fontName"), o.OptDouble("size"),
                                o.OptValue<SRGBColor>("color", options));
    }

    public override void Write(Utf8JsonWriter writer, TextStyleRun value, JsonSerializerOptions options) =>
        new JsonFields(options).Int("location", value.Location).Int("length", value.Length).String("fontName", value.FontName)
            .Double("size", value.Size).Value("color", value.Color).WriteTo(writer);
}
