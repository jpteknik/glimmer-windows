// The one lookup every text a person sees goes through. English is the key, word for word as in the Swift source;
// the Swedish comes from the table built from the original's App/Localizable.xcstrings (tools/xcstrings_to_table.py)
// plus the few strings only the Windows app has (WindowsStrings.sv.json). Swedish is used when Windows' display
// language is Swedish (CultureInfo.CurrentUICulture), as the Mac follows the system language.
//
// Shown text also gets Windows' words where the Mac's would be wrong: ⌘/⌥/⇧ become Ctrl/Alt/Shift in Photoshop's
// order for Windows ("⇧⌘L" -> "Ctrl+Shift+L"), and "this Mac" becomes "this PC". The lookup key never changes.
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Markup;
using Glimmer.Editor;

namespace Glimmer.App.Localization;

public static class Loc
{
    private static Dictionary<string, string?> strings = new(StringComparer.Ordinal);
    private static Dictionary<string, (string En, string? Sv)> keyed = new(StringComparer.Ordinal);
    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>> plurals = new(StringComparer.Ordinal);
    private static List<(Regex Pattern, string Replacement)> platformEn = [], platformSv = [];
    private static readonly HashSet<string> missing = new(StringComparer.Ordinal);

    /// <summary>True when texts are shown in Swedish.</summary>
    public static bool IsSwedish { get; private set; }

    /// <summary>Keys asked for that are in neither table (a typo against the Swift source, or a new string).</summary>
    public static IReadOnlyCollection<string> MissingKeys => missing;

    /// <summary>Loads the tables. language: "sv", "en", or null for Windows' display language.</summary>
    public static void Initialize(string? language = null)
    {
        var lang = language ?? Environment.GetEnvironmentVariable("GLIMMER_LANGUAGE") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        IsSwedish = string.Equals(lang, "sv", StringComparison.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        // A missing table is logged and the app carries on in plain English instead of not starting at all.
        using (var doc = Read(assembly, "Glimmer.Strings.sv.json"))
        if (doc is not null)
        {
            var root = doc.RootElement;
            foreach (var p in root.GetProperty("strings").EnumerateObject())
                strings[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
            foreach (var p in root.GetProperty("keyed").EnumerateObject())
                keyed[p.Name] = (p.Value.GetProperty("en").GetString()!, p.Value.GetProperty("sv").GetString());
            foreach (var p in root.GetProperty("plurals").EnumerateObject())
                plurals[p.Name] = p.Value.EnumerateObject().ToDictionary(
                    l => l.Name, l => l.Value.EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetString()!));
        }
        using (var doc = Read(assembly, "Glimmer.WindowsStrings.sv.json"))
        if (doc is not null)
        {
            var root = doc.RootElement;
            foreach (var p in root.GetProperty("strings").EnumerateObject())
                strings[p.Name] = p.Value.GetString();
            platformEn = Rules(root.GetProperty("platform").GetProperty("en"));
            platformSv = Rules(root.GetProperty("platform").GetProperty("sv"));
        }
    }

    private static JsonDocument? Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            global::Glimmer.App.Platform.CrashLog.Note($"Missing resource {name}: texts stay in English.");
            return null;
        }
        return JsonDocument.Parse(stream);
    }

    private static List<(Regex, string)> Rules(JsonElement list) =>
        list.EnumerateArray().Select(r => (new Regex(r[0].GetString()!, RegexOptions.CultureInvariant), r[1].GetString()!)).ToList();

    /// <summary>The text for an English key (no placeholders).</summary>
    public static string T(string english)
    {
        if (!strings.TryGetValue(english, out var swedish))
        {
            lock (missing) missing.Add(english);
            System.Diagnostics.Debug.WriteLine($"Loc: no entry for \"{english}\"");
        }
        return Platform(IsSwedish && swedish is not null ? swedish : english);
    }

    /// <summary>A template with .NET placeholders ("Exported {0} ({1}).") looked up, then formatted.</summary>
    public static string F(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(template), args.Select(FormatArg).ToArray());

    /// <summary>The one-or-many forms of a template, by the "other" form's English ("{0} points").</summary>
    public static string Plural(string otherTemplate, long count)
    {
        var form = count == 1 ? "one" : "other";
        string template = otherTemplate;
        if (plurals.TryGetValue(otherTemplate, out var langs))
        {
            var lang = IsSwedish && langs.ContainsKey("sv") ? "sv" : "en";
            if (langs.TryGetValue(lang, out var forms) && forms.TryGetValue(form, out var t)) template = t;
        }
        else
        {
            lock (missing) missing.Add(otherTemplate);
        }
        return Platform(string.Format(CultureInfo.CurrentCulture, template, count));
    }

    /// <summary>An entry whose key is not its English (String(localized: "layer.name.group", defaultValue: "Group")).</summary>
    public static string Keyed(string key, string englishDefault) =>
        keyed.TryGetValue(key, out var e) ? Platform(IsSwedish && e.Sv is not null ? e.Sv : e.En) : Platform(englishDefault);

    /// <summary>A text the editor composed.</summary>
    public static string T(UiText text) =>
        text.IsVerbatim ? Convert.ToString(text.Args[0], CultureInfo.CurrentCulture) ?? "" : F(text.Template, text.Args.ToArray());

    /// <summary>The text exactly as given, with only the platform words changed (a step name from a plan, an error's
    /// own words). Use for strings that are not catalogue keys.</summary>
    public static string Verbatim(string text) => Platform(text);

    /// <summary>A catalogue key when it is one, otherwise the text as given (button titles that may be composed).</summary>
    public static string Maybe(string text) => strings.ContainsKey(text) ? T(text) : Platform(text);

    private static object? FormatArg(object? arg) => arg switch
    {
        UiText t => T(t),
        _ => arg,
    };

    // ------------------------------------------------------------------------------------------- platform words

    private static readonly Regex KeyGlyphs = new("(?<mods>[⌃⌥⇧⌘]+)(?<rest>-(?=\\p{L})|[A-Za-z0-9↩⌫⌦⎋←→↑↓\\[\\]\\\\';=+/]?)",
                                                  RegexOptions.CultureInvariant);

    /// <summary>Puts Windows' key names and words in place of the Mac's.</summary>
    public static string Platform(string text)
    {
        if (text.Length == 0) return text;
        if (text.IndexOfAny(['⌃', '⌥', '⇧', '⌘', '↩', '⌫']) >= 0)
        {
            text = KeyGlyphs.Replace(text, m =>
            {
                var mods = Modifiers(m.Groups["mods"].Value);
                var rest = m.Groups["rest"].Value;
                if (rest.Length == 0) return mods;          // "⇧ add" -> "Shift add"
                if (rest == "-") return mods + "+";         // "⌥-click" -> "Alt+click"
                return mods + "+" + KeyName(rest[0]);       // "⇧⌘L" -> "Ctrl+Shift+L"
            });
            text = text.Replace("↩", "Enter").Replace("⌫", "Backspace");
        }
        foreach (var (pattern, replacement) in IsSwedish ? platformSv : platformEn)
            text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>Windows order, as Photoshop writes it: Ctrl, Alt, Shift. ⌃ and ⌘ are both Ctrl.</summary>
    private static string Modifiers(string glyphs)
    {
        var parts = new List<string>(3);
        if (glyphs.Contains('⌘') || glyphs.Contains('⌃')) parts.Add("Ctrl");
        if (glyphs.Contains('⌥')) parts.Add("Alt");
        if (glyphs.Contains('⇧')) parts.Add("Shift");
        return string.Join("+", parts);
    }

    public static string KeyName(char key) => key switch
    {
        '↩' => "Enter", '⌫' => "Backspace", '⌦' => "Delete", '⎋' => "Esc", '←' => "Left", '→' => "Right", '↑' => "Up", '↓' => "Down",
        ' ' => "Space", _ => char.ToUpperInvariant(key).ToString(),
    };
}

/// <summary>{l:Loc 'English text'} in XAML.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}

/// <summary>Turns a bound English key (a tool title, a command title) into the shown text.</summary>
public sealed class LocConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => "",
            UiText t => Loc.T(t),
            string s => Loc.T(s),
            _ => Loc.T(value.ToString() ?? ""),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a looked-up template (the ConverterParameter) with the bound values: "{0} × {1} px".</summary>
public sealed class LocFormatConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.F(parameter as string ?? "", values.Select(v => v == System.Windows.DependencyProperty.UnsetValue ? null : v).ToArray());

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
