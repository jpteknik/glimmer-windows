// The JSON conventions of the original's Codable types (manifest.json, record.json, the agent API), reproduced with
// System.Text.Json. No Swift file of its own: every Codable type in GlimmerCore is written and read through these
// helpers so that what the Mac writes reads here and the other way round.

using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimmer.Core;

/// <summary>
/// Options and helpers that reproduce Swift's <c>JSONEncoder</c>/<c>JSONDecoder</c> as the Mac version uses them.
/// </summary>
/// <remarks>
/// <para>The rules every converter in Glimmer.Core follows:</para>
/// <list type="bullet">
/// <item>Keys are written sorted the way Swift's <c>.sortedKeys</c> sorts them (case-insensitive, digit runs compared
/// as numbers, ties broken by code point). Swift sorts only where <c>.sortedKeys</c> is set; sorted output is valid
/// everywhere, so it is always used here.</item>
/// <item>A <c>nil</c> optional is left out. A key whose value is <c>null</c> reads as <c>nil</c>.</item>
/// <item>A missing non-optional key is an error unless the Swift type has a custom, lenient <c>init(from:)</c>
/// (property default values do not make Swift's synthesized decoding lenient).</item>
/// <item>Enums with raw values are their raw value; an unknown raw value is an error.</item>
/// <item>A <c>UUID</c> is an upper-case string, <c>Data</c> is base64, <c>SIMD2</c> is <c>[x, y]</c>.</item>
/// <item>A dictionary whose key is neither <c>String</c> nor <c>Int</c> (and doesn't adopt
/// <c>CodingKeyRepresentable</c>, which nothing in the original does) is a flat array <c>[key, value, …]</c>.</item>
/// </list>
/// </remarks>
public static class SwiftJson
{
    /// <summary>Indented output, as <c>[.prettyPrinted, .sortedKeys]</c> (the manifest). Non-ASCII stays readable.</summary>
    public static JsonSerializerOptions Pretty { get; } = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Compact output, as <c>[.sortedKeys]</c> (the link journal's record).</summary>
    public static JsonSerializerOptions Compact { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The order Swift's <c>.sortedKeys</c> puts keys in.</summary>
    public static IComparer<string> KeyOrder { get; } = new SwiftKeyComparer();

    public static string Serialize<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Pretty : Compact);

    public static byte[] SerializeToUtf8Bytes<T>(T value, bool pretty = false) =>
        JsonSerializer.SerializeToUtf8Bytes(value, pretty ? Pretty : Compact);

    /// <exception cref="JsonException">The JSON doesn't decode as <typeparamref name="T"/> would on the Mac.</exception>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Compact) ?? throw new JsonException($"Expected {typeof(T).Name} but found null.");

    /// <exception cref="JsonException">The JSON doesn't decode as <typeparamref name="T"/> would on the Mac.</exception>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Compact) ?? throw new JsonException($"Expected {typeof(T).Name} but found null.");

    // MARK: Writing

    /// <summary>Writes a finite double. Swift's encoder refuses NaN and infinities, and so does this.</summary>
    public static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value))
            throw new JsonException($"Unable to encode Double.{(double.IsNaN(value) ? "nan" : "infinity")} directly in JSON.");
        writer.WriteNumberValue(value);
    }

    public static void WriteFloat(Utf8JsonWriter writer, float value)
    {
        if (!float.IsFinite(value))
            throw new JsonException($"Unable to encode Float.{(float.IsNaN(value) ? "nan" : "infinity")} directly in JSON.");
        writer.WriteNumberValue(value);
    }

    /// <summary>A UUID as Swift writes it: upper case, with hyphens.</summary>
    public static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    internal static void Write<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);

    /// <summary>A <c>[String: V]</c> dictionary: a JSON object with sorted keys.</summary>
    internal static void WriteStringDictionary<TValue>(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, TValue>> dict,
                                                        Action<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in dict.OrderBy(p => p.Key, KeyOrder))
        {
            writer.WritePropertyName(key);
            writeValue(writer, value);
        }
        writer.WriteEndObject();
    }

    // MARK: Reading

    internal static JsonElement ReadElement(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    internal static JsonElement ExpectObject(JsonElement e, string typeName)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Expected to decode {typeName} as a dictionary but found {Describe(e)} instead.");
        return e;
    }

    internal static string Describe(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "a dictionary",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a bool",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    /// <summary>The value for <paramref name="key"/> when it is present and not <c>null</c> (Swift's <c>decodeIfPresent</c>).</summary>
    internal static bool TryGet(this JsonElement o, string key, out JsonElement value)
    {
        if (o.TryGetProperty(key, out value) && value.ValueKind != JsonValueKind.Null) return true;
        value = default;
        return false;
    }

    /// <summary>The value for <paramref name="key"/>, which must be there (Swift's <c>decode</c>).</summary>
    internal static JsonElement Req(this JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var value))
            throw new JsonException($"No value associated with key \"{key}\".");
        if (value.ValueKind == JsonValueKind.Null)
            throw new JsonException($"Expected a value for key \"{key}\" but found null instead.");
        return value;
    }

    internal static double AsDouble(this JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d))
            throw new JsonException($"Expected to decode Double for key \"{key}\" but found {Describe(e)} instead.");
        return d;
    }

    internal static float AsFloat(this JsonElement e, string key) => (float)e.AsDouble(key);

    internal static long AsInt(this JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            if (e.TryGetInt64(out var i)) return i;
            if (e.TryGetDouble(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9.2e18) return (long)d;
        }
        throw new JsonException($"Expected to decode Int for key \"{key}\" but found {Describe(e)} instead.");
    }

    internal static bool AsBool(this JsonElement e, string key) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new JsonException($"Expected to decode Bool for key \"{key}\" but found {Describe(e)} instead."),
    };

    internal static string AsString(this JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.String
            ? e.GetString()!
            : throw new JsonException($"Expected to decode String for key \"{key}\" but found {Describe(e)} instead.");

    internal static Guid AsUuid(this JsonElement e, string key)
    {
        var s = e.AsString(key);
        // Swift's UUID(uuidString:) takes the 8-4-4-4-12 form only, in either case.
        if (s.Length == 36 && Guid.TryParseExact(s, "D", out var id)) return id;
        throw new JsonException($"Attempted to decode UUID from invalid UUID string \"{s}\" for key \"{key}\".");
    }

    internal static byte[] AsData(this JsonElement e, string key)
    {
        var s = e.AsString(key);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new JsonException($"Encountered Data is not valid Base64 for key \"{key}\"."); }
    }

    internal static T As<T>(this JsonElement e, JsonSerializerOptions options, string key)
    {
        try
        {
            return e.Deserialize<T>(options) ?? throw new JsonException($"Expected {typeof(T).Name} for key \"{key}\" but found null.");
        }
        catch (JsonException ex) when (!ex.Message.Contains("key \"", StringComparison.Ordinal))
        {
            throw new JsonException($"{ex.Message} (key \"{key}\")", ex);
        }
    }

    internal static double ReqDouble(this JsonElement o, string key) => o.Req(key).AsDouble(key);
    internal static float ReqFloat(this JsonElement o, string key) => o.Req(key).AsFloat(key);
    internal static long ReqInt(this JsonElement o, string key) => o.Req(key).AsInt(key);
    internal static bool ReqBool(this JsonElement o, string key) => o.Req(key).AsBool(key);
    internal static string ReqString(this JsonElement o, string key) => o.Req(key).AsString(key);
    internal static Guid ReqUuid(this JsonElement o, string key) => o.Req(key).AsUuid(key);
    internal static T Req<T>(this JsonElement o, string key, JsonSerializerOptions options) => o.Req(key).As<T>(options, key);

    internal static double? OptDouble(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsDouble(key) : null;
    internal static long? OptInt(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsInt(key) : null;
    internal static bool? OptBool(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsBool(key) : null;
    internal static string? OptString(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsString(key) : null;
    internal static Guid? OptUuid(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsUuid(key) : null;
    internal static byte[]? OptData(this JsonElement o, string key) => o.TryGet(key, out var v) ? v.AsData(key) : null;

    internal static T? Opt<T>(this JsonElement o, string key, JsonSerializerOptions options) where T : class =>
        o.TryGet(key, out var v) ? v.As<T>(options, key) : null;

    internal static T? OptValue<T>(this JsonElement o, string key, JsonSerializerOptions options) where T : struct =>
        o.TryGet(key, out var v) ? v.As<T>(options, key) : null;

    internal static System.Collections.Immutable.ImmutableArray<T> ReqArray<T>(this JsonElement o, string key, JsonSerializerOptions options) =>
        ArrayOf<T>(o.Req(key), options, key);

    internal static System.Collections.Immutable.ImmutableArray<T>? OptArray<T>(this JsonElement o, string key, JsonSerializerOptions options) =>
        o.TryGet(key, out var v) ? ArrayOf<T>(v, options, key) : null;

    internal static System.Collections.Immutable.ImmutableArray<T> ArrayOf<T>(JsonElement e, JsonSerializerOptions options, string key)
    {
        if (e.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Expected to decode Array for key \"{key}\" but found {Describe(e)} instead.");
        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray()) builder.Add(item.As<T>(options, key));
        return builder.MoveToImmutable();
    }

    /// <summary>A <c>[String: V]</c> dictionary (a JSON object).</summary>
    internal static System.Collections.Immutable.ImmutableDictionary<string, TValue> StringDictionary<TValue>(
        JsonElement e, string key, Func<JsonElement, TValue> readValue)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Expected to decode Dictionary for key \"{key}\" but found {Describe(e)} instead.");
        var b = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, TValue>();
        foreach (var p in e.EnumerateObject()) b[p.Name] = readValue(p.Value);
        return b.ToImmutable();
    }

    // MARK: Key order

    /// <summary>
    /// <c>String.compare(_:options: [.numeric, .caseInsensitive, .forcedOrdering])</c>, which the Mac's JSON writer
    /// sorts keys with: letters compared without case, runs of digits by value, and a final code-point comparison so
    /// that different keys never compare equal.
    /// </summary>
    private sealed class SwiftKeyComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                char a = x[i], b = y[j];
                if (char.IsAsciiDigit(a) && char.IsAsciiDigit(b))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var da = x.AsSpan(si, i - si).TrimStart('0');
                    var db = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (da.Length != db.Length) return da.Length < db.Length ? -1 : 1;
                    int c = da.SequenceCompareTo(db);
                    if (c != 0) return Math.Sign(c);
                    continue;
                }
                int fold = char.ToUpperInvariant(a).CompareTo(char.ToUpperInvariant(b));
                if (fold != 0) return Math.Sign(fold);
                i++;
                j++;
            }
            int rest = (x.Length - i).CompareTo(y.Length - j);
            if (rest != 0) return Math.Sign(rest);
            return Math.Sign(string.CompareOrdinal(x, y));
        }
    }
}

/// <summary>
/// The raw value of an enum case, as its Swift counterpart's <c>rawValue</c> (for example <c>"pass-through"</c>).
/// Every enum that is <c>Codable</c> in the original has one on each member.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class RawValueAttribute(string value) : Attribute
{
    public string Value { get; } = value;
}

/// <summary>Raw values of an enum whose members carry <see cref="RawValueAttribute"/>.</summary>
public static class RawValues<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> ToRaw = new();
    private static readonly Dictionary<string, T> FromRaw = new(StringComparer.Ordinal);

    static RawValues()
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (T)field.GetValue(null)!;
            var raw = field.GetCustomAttribute<RawValueAttribute>()?.Value
                      ?? throw new InvalidOperationException($"{typeof(T).Name}.{field.Name} has no raw value.");
            ToRaw[value] = raw;
            FromRaw[raw] = value;
        }
    }

    /// <summary>The cases in declaration order (Swift's <c>allCases</c>).</summary>
    public static IReadOnlyList<T> AllCases { get; } = Enum.GetValues<T>();

    public static string Of(T value) =>
        ToRaw.TryGetValue(value, out var raw) ? raw : throw new ArgumentOutOfRangeException(nameof(value), value, null);

    /// <summary>Swift's <c>init?(rawValue:)</c>: the case with this raw value, or null.</summary>
    public static T? Parse(string? raw) => raw is not null && FromRaw.TryGetValue(raw, out var v) ? v : null;
}

/// <summary>Reads and writes an enum as its raw value; an unknown raw value is an error, as in Swift.</summary>
public sealed class RawValueEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected to decode {typeof(T).Name} as a string but found {reader.TokenType}.");
        var raw = reader.GetString();
        return RawValues<T>.Parse(raw)
               ?? throw new JsonException($"Cannot initialize {typeof(T).Name} from invalid String value {raw}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(RawValues<T>.Of(value));
}

/// <summary>Collects an object's fields and writes them in Swift's sorted-key order.</summary>
internal sealed class JsonFields
{
    private readonly List<(string Key, Action<Utf8JsonWriter> Write)> fields = [];
    private readonly JsonSerializerOptions options;

    public JsonFields(JsonSerializerOptions options) => this.options = options;

    public JsonFields Double(string key, double value)
    {
        fields.Add((key, w => SwiftJson.WriteDouble(w, value)));
        return this;
    }

    public JsonFields Double(string key, double? value) => value is { } v ? Double(key, v) : this;

    public JsonFields Float(string key, float value)
    {
        fields.Add((key, w => SwiftJson.WriteFloat(w, value)));
        return this;
    }

    public JsonFields Int(string key, long value)
    {
        fields.Add((key, w => w.WriteNumberValue(value)));
        return this;
    }

    public JsonFields Int(string key, long? value) => value is { } v ? Int(key, v) : this;

    public JsonFields Bool(string key, bool value)
    {
        fields.Add((key, w => w.WriteBooleanValue(value)));
        return this;
    }

    public JsonFields Bool(string key, bool? value) => value is { } v ? Bool(key, v) : this;

    public JsonFields String(string key, string? value)
    {
        if (value is not null) fields.Add((key, w => w.WriteStringValue(value)));
        return this;
    }

    public JsonFields Uuid(string key, Guid? value)
    {
        if (value is { } id) fields.Add((key, w => w.WriteStringValue(SwiftJson.UuidString(id))));
        return this;
    }

    public JsonFields Data(string key, ReadOnlyMemory<byte>? value)
    {
        if (value is { } bytes) fields.Add((key, w => w.WriteBase64StringValue(bytes.Span)));
        return this;
    }

    /// <summary>A nested value through its own converter; left out when null.</summary>
    public JsonFields Value<T>(string key, T? value)
    {
        if (value is not null)
        {
            var opts = options;
            fields.Add((key, w => JsonSerializer.Serialize(w, value, opts)));
        }
        return this;
    }

    /// <summary>An array that is always there (a default array is written as empty).</summary>
    public JsonFields Array<T>(string key, System.Collections.Immutable.ImmutableArray<T> values) =>
        Array(key, values.IsDefault ? [] : (IEnumerable<T>)values);

    /// <summary>An optional array: left out when null.</summary>
    public JsonFields Array<T>(string key, System.Collections.Immutable.ImmutableArray<T>? values) =>
        values is { } v ? Array(key, v) : this;

    public JsonFields Array<T>(string key, IEnumerable<T>? values)
    {
        if (values is not null)
        {
            var opts = options;
            var list = values.ToList();
            fields.Add((key, w =>
            {
                w.WriteStartArray();
                foreach (var v in list) JsonSerializer.Serialize(w, v, opts);
                w.WriteEndArray();
            }));
        }
        return this;
    }

    public JsonFields Custom(string key, Action<Utf8JsonWriter> write)
    {
        fields.Add((key, write));
        return this;
    }

    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var (key, write) in fields.OrderBy(f => f.Key, SwiftJson.KeyOrder))
        {
            writer.WritePropertyName(key);
            write(writer);
        }
        writer.WriteEndObject();
    }
}

/// <summary>Equality and hashing for the collections inside records (records compare collections by reference).</summary>
internal static class Seq
{
    public static bool Equal<T>(System.Collections.Immutable.ImmutableArray<T> a, System.Collections.Immutable.ImmutableArray<T> b)
    {
        if (a.IsDefault || b.IsDefault) return a.IsDefault == b.IsDefault || (a.IsDefaultOrEmpty && b.IsDefaultOrEmpty);
        return a.AsSpan().SequenceEqual(b.AsSpan(), EqualityComparer<T>.Default);
    }

    public static bool Equal<T>(System.Collections.Immutable.ImmutableArray<T>? a, System.Collections.Immutable.ImmutableArray<T>? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || Equal(a.Value, b!.Value));

    public static bool Equal<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? a, IReadOnlyDictionary<TKey, TValue>? b,
                                           Func<TValue, TValue, bool>? valueEquals = null)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        valueEquals ??= EqualityComparer<TValue>.Default.Equals;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !valueEquals(value, other)) return false;
        }
        return true;
    }

    public static int Hash<T>(System.Collections.Immutable.ImmutableArray<T> a)
    {
        if (a.IsDefaultOrEmpty) return 0;
        var h = new HashCode();
        foreach (var x in a) h.Add(x);
        return h.ToHashCode();
    }

    public static int Hash<T>(System.Collections.Immutable.ImmutableArray<T>? a) => a.HasValue ? Hash(a.Value) : -1;

    public static int Hash<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? d)
    {
        if (d is null) return -1;
        int h = d.Count;
        foreach (var (key, value) in d) h ^= HashCode.Combine(key, value);   // order-independent
        return h;
    }

    public static bool BytesEqual(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || a.Value.Span.SequenceEqual(b!.Value.Span));

    public static int BytesHash(ReadOnlyMemory<byte>? a)
    {
        if (!a.HasValue) return -1;
        var h = new HashCode();
        h.AddBytes(a.Value.Span.Length > 256 ? a.Value.Span[..256] : a.Value.Span);
        h.Add(a.Value.Length);
        return h.ToHashCode();
    }
}

