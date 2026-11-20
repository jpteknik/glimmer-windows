// Part of the port of Packages/GlimmerKit/Sources/GlimmerCore/TextAndPaths.swift and TextStyles.swift: where the Mac
// asks Core Text for a font by PostScript name (CTFontCreateWithName), this finds the installed font to use.

using System.Collections.Concurrent;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Glimmer.Core;

/// <summary>How a stored PostScript font name was matched to an installed font.</summary>
/// <param name="RequestedName">The name as stored in the document (never changed by resolving).</param>
/// <param name="PostScriptName">The installed font's own PostScript name, when it has one.</param>
/// <param name="Family">The installed font's family.</param>
/// <param name="Weight">100 (thin) … 900 (black).</param>
/// <param name="Width">1 (ultra-condensed) … 9 (ultra-expanded); 5 is normal.</param>
/// <param name="Italic">Italic or oblique.</param>
/// <param name="IsExact">True when a font with exactly the requested PostScript name is installed.</param>
public sealed record ResolvedFont(string RequestedName, string? PostScriptName, string Family, int Weight, int Width, bool Italic, bool IsExact);

/// <summary>
/// Maps the PostScript font names documents store ("HelveticaNeue-Bold", as the Mac writes them) to installed fonts.
/// </summary>
/// <remarks>
/// <para>Order: a font with exactly that PostScript name; else the family and style the name spells
/// ("HelveticaNeue-BoldItalic" is Helvetica Neue, weight 700, italic), where a Mac family that Windows doesn't have
/// is tried as its usual stand-in (Helvetica → Arial, San Francisco → Segoe UI, Menlo → Consolas, …); else Segoe UI,
/// Arial and the other common sans-serif families; else the system default.</para>
/// <para>The stored name is never rewritten: a document opened on a computer without the font keeps asking for it,
/// and shows it again where it is installed. Results depend only on the installed fonts and are cached per name.</para>
/// </remarks>
public static class FontResolver
{
    private static readonly ConcurrentDictionary<string, FontFace> Faces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Family, int Weight, int Width, int Slant, int CodePoint), FontFace?> Fallbacks = new();
    private static readonly Lazy<Dictionary<string, InstalledFace>> Installed = new(BuildIndex);

    /// <summary>One installed face, found by enumerating the font manager.</summary>
    private sealed record InstalledFace(string PostScriptName, string Family, int StyleIndex, int Weight, int Width, SKFontStyleSlant Slant);

    /// <summary>Whether a font with exactly this PostScript name is installed.</summary>
    public static bool Exists(string postScriptName) => Installed.Value.ContainsKey(postScriptName);

    /// <summary>The PostScript names of every installed font, sorted.</summary>
    public static IReadOnlyList<string> InstalledPostScriptNames => [.. Installed.Value.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The installed font a stored PostScript name is drawn with.</summary>
    public static ResolvedFont Resolve(string postScriptName)
    {
        var face = Face(postScriptName);
        var tf = face.Typeface;
        return new ResolvedFont(postScriptName, face.PostScriptName, tf.FamilyName, tf.FontWeight, tf.FontWidth,
                                tf.FontSlant != SKFontStyleSlant.Upright, face.IsExact);
    }

    /// <summary>Whether the font a name resolves to is bold (weight 600 or more, as Core Text's bold trait).</summary>
    public static bool IsBold(string postScriptName) => Face(postScriptName).Typeface.FontWeight >= 600;

    /// <summary>
    /// The PostScript name of the member of the resolved font's family with (or without) bold, keeping its width and
    /// italic; null when the family has no such member.
    /// </summary>
    public static string? WithBold(string postScriptName, bool bold)
    {
        var tf = Face(postScriptName).Typeface;
        bool italic = tf.FontSlant != SKFontStyleSlant.Upright;
        using var style = new SKFontStyle(bold ? 700 : 400, tf.FontWidth, tf.FontSlant);
        using var other = SKFontManager.Default.MatchFamily(tf.FamilyName, style);
        if (other is null || !SameFamily(other.FamilyName, tf.FamilyName)) return null;
        if ((other.FontWeight >= 600) != bold || (other.FontSlant != SKFontStyleSlant.Upright) != italic) return null;
        return PostScriptNameOf(other);
    }

    /// <summary>The face for a stored PostScript name (cached).</summary>
    internal static FontFace Face(string postScriptName) => Faces.GetOrAdd(postScriptName, Load);

    /// <summary>A face that has a glyph for <paramref name="codePoint"/> when <paramref name="primary"/> hasn't (Core Text's font fallback).</summary>
    internal static FontFace? Fallback(FontFace primary, int codePoint)
    {
        var tf = primary.Typeface;
        var key = (tf.FamilyName, tf.FontWeight, tf.FontWidth, (int)tf.FontSlant, codePoint);
        return Fallbacks.GetOrAdd(key, static k =>
        {
            var match = SKFontManager.Default.MatchCharacter(k.Family, k.Weight, k.Width, (SKFontStyleSlant)k.Slant, null, k.CodePoint);
            if (match is null) return null;
            // One face per fallback font, so a run of characters from it shapes as one piece.
            var faceKey = $"{match.FamilyName}|{match.FontWeight}|{match.FontWidth}|{(int)match.FontSlant}";
            return FallbackFaces.GetOrAdd(faceKey, _ => new FontFace(match, PostScriptNameOf(match), isExact: false));
        });
    }

    private static readonly ConcurrentDictionary<string, FontFace> FallbackFaces = new(StringComparer.Ordinal);

    private static FontFace Load(string name)
    {
        var mgr = SKFontManager.Default;
        if (Installed.Value.TryGetValue(name, out var exact))
        {
            using var set = mgr.GetFontStyles(exact.Family);
            if (set is not null && exact.StyleIndex < set.Count && set.CreateTypeface(exact.StyleIndex) is { } tf)
                return new FontFace(tf, exact.PostScriptName, isExact: true);
        }
        var (families, weight, width, slant) = Parse(name);
        using var style = new SKFontStyle(weight, width, slant);
        foreach (var family in families.Concat(DefaultFamilies))
        {
            var tf = mgr.MatchFamily(family, style);
            if (tf is null) continue;
            if (SameFamily(tf.FamilyName, family)) return new FontFace(tf, PostScriptNameOf(tf), isExact: false);
            tf.Dispose();
        }
        var fallback = mgr.MatchCharacter('A') ?? SKTypeface.Default;
        return new FontFace(fallback, PostScriptNameOf(fallback), isExact: false);
    }

    private static bool SameFamily(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // MARK: Parsing PostScript names

    /// <summary>Stand-ins tried after the family a name spells, by the family part without spaces, lower case.</summary>
    private static readonly Dictionary<string, string[]> MacFamilies = BuildMacFamilies();

    private static Dictionary<string, string[]> BuildMacFamilies()
    {
        string[] helvetica = ["Helvetica Neue", "Helvetica", "Arial", "Liberation Sans", "Nimbus Sans", "DejaVu Sans"];
        string[] system = ["SF Pro", "Segoe UI", "Arial", "Liberation Sans", "Nimbus Sans", "DejaVu Sans"];
        string[] mono = ["Menlo", "SF Mono", "Cascadia Mono", "Consolas", "Courier New", "Liberation Mono", "DejaVu Sans Mono"];
        string[] times = ["Times New Roman", "Times", "Liberation Serif", "Nimbus Roman", "DejaVu Serif"];
        string[] courier = ["Courier New", "Courier", "Liberation Mono", "Nimbus Mono PS", "DejaVu Sans Mono"];
        string[] serif = ["Georgia", "Times New Roman", "Liberation Serif", "Nimbus Roman", "DejaVu Serif"];
        var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string[] keys, string[] families, string? first = null)
        {
            foreach (var k in keys) d[k] = first is null ? families : [first, .. families];
        }
        Add(["helveticaneue"], helvetica);
        Add(["helvetica"], helvetica.Skip(1).ToArray(), "Helvetica");
        Add(["sfns", "sfnstext", "sfnsdisplay", "sfnsrounded", "sfpro", "sfprotext", "sfprodisplay", "sfprorounded", "sfui", "sfuitext",
             "sfuidisplay", "applesystemuifont", "systemfont"], system);
        Add(["sfmono", "sfnsmono"], mono, "SF Mono");
        Add(["menlo"], mono);
        Add(["monaco"], mono, "Monaco");
        Add(["times", "timesnewroman", "timesroman"], times);
        Add(["courier", "couriernew"], courier);
        Add(["lucidagrande"], ["Lucida Grande", "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans"]);
        Add(["geneva"], ["Geneva", "Verdana", "DejaVu Sans"]);
        Add(["avenir"], ["Avenir", "Avenir Next", "Segoe UI", "Arial", "DejaVu Sans"]);
        Add(["avenirnext", "avenirnextcondensed"], ["Avenir Next", "Avenir", "Segoe UI", "Arial", "DejaVu Sans"]);
        Add(["futura"], ["Futura", "Century Gothic", "URW Gothic", "Segoe UI", "DejaVu Sans"]);
        Add(["palatino"], ["Palatino", "Palatino Linotype", "P052", .. serif]);
        Add(["optima"], ["Optima", "Candara", "Segoe UI", "DejaVu Sans"]);
        Add(["gillsans"], ["Gill Sans", "Gill Sans MT", "Segoe UI", "DejaVu Sans"]);
        Add(["baskerville"], ["Baskerville", "Baskerville Old Face", .. serif]);
        Add(["didot"], ["Didot", "Bodoni MT", .. serif]);
        Add(["bodoni72", "bodonisvtwentyfouritc"], ["Bodoni 72", "Bodoni MT", .. serif]);
        Add(["cochin"], ["Cochin", .. serif]);
        Add(["hoeflertext"], ["Hoefler Text", .. serif]);
        Add(["charter"], ["Charter", .. serif]);
        Add(["newyork", "newyorkmedium", "newyorksmall"], ["New York", .. serif]);
        Add(["georgia"], serif);
        Add(["americantypewriter"], ["American Typewriter", .. courier]);
        Add(["markerfelt"], ["Marker Felt", "Comic Sans MS", "DejaVu Sans"]);
        Add(["chalkboard", "chalkboardse"], ["Chalkboard SE", "Chalkboard", "Comic Sans MS", "DejaVu Sans"]);
        Add(["noteworthy"], ["Noteworthy", "Segoe Print", "Comic Sans MS", "DejaVu Sans"]);
        Add(["arial"], ["Arial", "Liberation Sans", "Nimbus Sans", "DejaVu Sans"]);
        Add(["verdana"], ["Verdana", "DejaVu Sans"]);
        Add(["tahoma"], ["Tahoma", "Verdana", "DejaVu Sans"]);
        Add(["trebuchetms"], ["Trebuchet MS", "DejaVu Sans"]);
        return d;
    }

    /// <summary>Tried, in order, when nothing the name spells is installed.</summary>
    private static readonly string[] DefaultFamilies =
        ["Segoe UI", "Arial", "Helvetica", "Liberation Sans", "Nimbus Sans", "DejaVu Sans", "Noto Sans", "Ubuntu"];

    private static readonly (string Word, int Weight)[] WeightWords =
    [
        ("extralight", 200), ("ultralight", 200), ("hairline", 100), ("thin", 100), ("semilight", 350), ("demilight", 350),
        ("light", 300), ("extrablack", 950), ("ultrablack", 950), ("black", 900), ("extrabold", 800), ("ultrabold", 800),
        ("heavy", 800), ("semibold", 600), ("demibold", 600), ("demi", 600), ("bold", 700), ("medium", 500),
        ("regular", 400), ("roman", 400), ("book", 400), ("normal", 400), ("plain", 400),
    ];

    /// <summary>Style words that can end a hyphen-less PostScript name; longer ones first.</summary>
    private static readonly string[] TrailingStyleWords =
        ["semibold", "demibold", "extrabold", "ultrabold", "extralight", "ultralight", "condensed", "italic", "oblique",
         "medium", "light", "black", "heavy", "bold", "thin"];

    private static readonly (string Word, int Width)[] WidthWords =
    [
        ("ultracondensed", 1), ("extracondensed", 2), ("semicondensed", 4), ("condensed", 3), ("compressed", 3), ("narrow", 3),
        ("semiexpanded", 6), ("extraexpanded", 8), ("ultraexpanded", 9), ("expanded", 7), ("extended", 7), ("wide", 7),
    ];

    /// <summary>
    /// The families to try for a PostScript name, and the style it spells: "TimesNewRomanPS-BoldItalicMT" gives
    /// Times New Roman, weight 700, italic.
    /// </summary>
    internal static (IReadOnlyList<string> Families, int Weight, int Width, SKFontStyleSlant Slant) Parse(string name)
    {
        var trimmed = name.TrimStart('.');
        int dash = trimmed.IndexOf('-');
        string familyPart = dash >= 0 ? trimmed[..dash] : trimmed;
        string stylePart = dash >= 0 ? trimmed[(dash + 1)..] : "";
        familyPart = StripVendorSuffix(familyPart);
        stylePart = StripVendorSuffix(stylePart);

        // Without a hyphen the family part can carry the style ("ArialBoldItalic"): take style words off its end, unless
        // the whole part is a known family. Words that also end family names ("TimesNewRoman") stay.
        var lowerFamily = familyPart.ToLowerInvariant();
        var lowerStyle = stylePart.ToLowerInvariant();
        if (dash < 0 && !MacFamilies.ContainsKey(lowerFamily))
        {
            bool stripped = true;
            while (stripped)
            {
                stripped = false;
                foreach (var word in TrailingStyleWords)
                {
                    if (lowerFamily.Length <= word.Length || !lowerFamily.EndsWith(word, StringComparison.Ordinal)) continue;
                    familyPart = familyPart[..^word.Length];
                    lowerFamily = lowerFamily[..^word.Length];
                    lowerStyle = word + lowerStyle;
                    stripped = true;
                    break;
                }
            }
        }

        int weight = 400, width = 5;
        var slant = SKFontStyleSlant.Upright;
        var rest = lowerStyle;
        foreach (var (word, w) in WidthWords)
        {
            if (!rest.Contains(word, StringComparison.Ordinal)) continue;
            width = w;
            rest = rest.Replace(word, "", StringComparison.Ordinal);
            break;
        }
        // A family can be a width of another ("AvenirNextCondensed", "ArialNarrow"): it keeps its name and gives its width.
        if (width == 5)
        {
            foreach (var (word, w) in WidthWords)
            {
                if (!lowerFamily.EndsWith(word, StringComparison.Ordinal) || lowerFamily.Length == word.Length) continue;
                width = w;
                break;
            }
        }
        if (rest.Contains("italic", StringComparison.Ordinal) || rest.Contains("oblique", StringComparison.Ordinal)
            || rest.Contains("slanted", StringComparison.Ordinal) || rest.EndsWith("it", StringComparison.Ordinal))
        {
            slant = rest.Contains("oblique", StringComparison.Ordinal) ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Italic;
            rest = rest.Replace("italic", "", StringComparison.Ordinal).Replace("oblique", "", StringComparison.Ordinal);
        }
        foreach (var (word, w) in WeightWords)
        {
            if (!rest.Contains(word, StringComparison.Ordinal)) continue;
            weight = Math.Min(w, 900);
            break;
        }

        var families = new List<string>();
        var key = lowerFamily.Replace(" ", "", StringComparison.Ordinal);
        if (MacFamilies.TryGetValue(key, out var mapped)) families.AddRange(mapped);
        var spaced = CamelSplit(familyPart);
        if (!families.Contains(spaced, StringComparer.OrdinalIgnoreCase)) families.Insert(0, spaced);
        if (!families.Contains(familyPart, StringComparer.OrdinalIgnoreCase)) families.Add(familyPart);
        return (families, weight, width, slant);
    }

    private static string StripVendorSuffix(string part)
    {
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
        {
            if (part.Length > suffix.Length && part.EndsWith(suffix, StringComparison.Ordinal)) return part[..^suffix.Length];
        }
        return part;
    }

    /// <summary>"HelveticaNeue" → "Helvetica Neue", "SFProDisplay" → "SF Pro Display", "TimesNewRoman" → "Times New Roman".</summary>
    internal static string CamelSplit(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c))
            {
                char prev = s[i - 1];
                bool nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextLower)) sb.Append(' ');
            }
            else if (i > 0 && char.IsDigit(c) && char.IsLetter(s[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // MARK: The installed fonts

    private static Dictionary<string, InstalledFace> BuildIndex()
    {
        var index = new Dictionary<string, InstalledFace>(StringComparer.Ordinal);
        var mgr = SKFontManager.Default;
        foreach (var family in mgr.GetFontFamilies().Order(StringComparer.Ordinal))
        {
            using var set = mgr.GetFontStyles(family);
            if (set is null) continue;
            for (int i = 0; i < set.Count; i++)
            {
                using var tf = set.CreateTypeface(i);
                if (tf is null || PostScriptNameOf(tf) is not { } ps) continue;
                index.TryAdd(ps, new InstalledFace(ps, family, i, tf.FontWeight, tf.FontWidth, tf.FontSlant));
            }
        }
        return index;
    }

    private const uint NameTableTag = 0x6E616D65;   // 'name'

    /// <summary>The PostScript name (name ID 6) in a font's <c>name</c> table.</summary>
    internal static string? PostScriptNameOf(SKTypeface typeface)
    {
        if (!typeface.TryGetTableData(NameTableTag, out var table) || table is null || table.Length < 6) return null;
        int count = ReadUInt16(table, 2), stringOffset = ReadUInt16(table, 4);
        string? best = null;
        int bestRank = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > table.Length) break;
            int platform = ReadUInt16(table, r), encoding = ReadUInt16(table, r + 2), language = ReadUInt16(table, r + 4);
            int nameId = ReadUInt16(table, r + 6), length = ReadUInt16(table, r + 8), offset = ReadUInt16(table, r + 10);
            if (nameId != 6) continue;
            int start = stringOffset + offset;
            if (start + length > table.Length) continue;
            int rank;
            string value;
            if (platform == 3 && (encoding == 1 || encoding == 10))
            {
                rank = language == 0x409 ? 0 : 1;
                value = Encoding.BigEndianUnicode.GetString(table, start, length);
            }
            else if (platform == 0)
            {
                rank = 2;
                value = Encoding.BigEndianUnicode.GetString(table, start, length);
            }
            else if (platform == 1 && encoding == 0)
            {
                rank = 3;
                value = Encoding.Latin1.GetString(table, start, length);
            }
            else continue;
            if (rank < bestRank && value.Length > 0)
            {
                best = value;
                bestRank = rank;
            }
        }
        return best;
    }

    private static int ReadUInt16(byte[] b, int at) => (b[at] << 8) | b[at + 1];
}

/// <summary>An installed font face and what shaping text with it needs.</summary>
internal sealed class FontFace
{
    /// <summary>HarfBuzz positions are in units of 1/<see cref="ShapingScale"/> em.</summary>
    internal const int ShapingScale = 1 << 16;

    private readonly Lazy<HarfBuzzSharp.Font> shaper;

    public FontFace(SKTypeface typeface, string? postScriptName, bool isExact)
    {
        Typeface = typeface;
        PostScriptName = postScriptName;
        IsExact = isExact;
        shaper = new Lazy<HarfBuzzSharp.Font>(() =>
        {
            // The blob takes the stream over and releases it when HarfBuzz is done with it: not disposed here.
            var stream = Typeface.OpenStream(out int index);
            using var blob = stream.ToHarfBuzzBlob();
            using var face = new Face(blob, index) { Index = index, UnitsPerEm = Typeface.UnitsPerEm };
            var font = new HarfBuzzSharp.Font(face);
            font.SetScale(ShapingScale, ShapingScale);
            font.SetFunctionsOpenType();
            return font;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public SKTypeface Typeface { get; }
    public string? PostScriptName { get; }
    public bool IsExact { get; }

    /// <summary>The HarfBuzz font. Shape with <see cref="ShapingLock"/> held: one shaping call per font at a time.</summary>
    public HarfBuzzSharp.Font Shaper => shaper.Value;

    public Lock ShapingLock { get; } = new();

    public bool HasGlyph(int codePoint) => Typeface.ContainsGlyph(codePoint);

    /// <summary>A Skia font at <paramref name="size"/>, set up as the Mac draws text: antialiased, not hinted, positioned to fractions of a pixel.</summary>
    public SKFont CreateFont(float size) => new(Typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        Subpixel = true,
        LinearMetrics = true,
    };
}
