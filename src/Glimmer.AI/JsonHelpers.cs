// New in the port: no counterpart in the original. Swift's `[String: Any]` + JSONSerialization
// become System.Text.Json.Nodes (JsonObject / JsonArray); this is the small layer that keeps the
// ported code reading like the original (`as? String`, `as? Int`, `.sortedKeys`).
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

internal static class Json
{
    internal static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    internal static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// `JSONSerialization.data(withJSONObject:)`.
    public static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Compact));

    public static string Text(JsonNode node) => node.ToJsonString(Compact);

    /// `try? JSONSerialization.jsonObject(with: data)`: null when it isn't JSON.
    public static JsonNode? Parse(ReadOnlySpan<byte> data)
    {
        try { return JsonNode.Parse(data); } catch (JsonException) { return null; }
    }

    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    /// `… as? [String: Any]`.
    public static JsonObject? ParseObject(ReadOnlySpan<byte> data) => Parse(data) as JsonObject;

    public static JsonObject? ParseObject(string text) => Parse(text) as JsonObject;

    /// Swift's `.sortedKeys`: keys in order, at every level.
    public static string SerializeSorted(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Compact.Encoder }))
            WriteSorted(writer, node);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// Sorted keys, indented: `[.prettyPrinted, .sortedKeys]`.
    public static string SerializeSortedIndented(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Compact.Encoder, Indented = true }))
            WriteSorted(writer, node);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteSorted(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// A JSON array of independent copies: a node can have only one parent, and the same message
    /// list is sent more than once (retries, the next turn).
    public static JsonArray Array(IEnumerable<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item.DeepClone());
        return array;
    }

    // MARK: `as?` casts

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        // A value built in code (not parsed) keeps its own type.
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        return null;
    }

    /// `as? Int`: a whole number.
    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue) return (int)d;
        return null;
    }

    public static JsonObject? Obj(JsonNode? node) => node as JsonObject;

    public static JsonArray? Arr(JsonNode? node) => node as JsonArray;

    /// `… as? [[String: Any]]`: the objects of an array (an array holding anything else is not one).
    public static List<JsonObject>? Objects(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item is not JsonObject o) return null;
            list.Add(o);
        }
        return list;
    }

    /// `… as? [String]`.
    public static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var list = new List<string>();
        foreach (var item in array)
        {
            var s = Str(item);
            if (s is null) return null;
            list.Add(s);
        }
        return list;
    }

    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
