// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Agent.swift (the JSONValue extension is the one in Siblings.swift)
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Glimmer.Core;

namespace Glimmer.AI;

public enum JsonKind { String, Number, Bool, Object, Array, Null }

/// <summary>A JSON value as the planner emits it (params are passed through loosely).</summary>
[JsonConverter(typeof(AgentValueConverter))]
public sealed class AgentValue : IEquatable<AgentValue>
{
    public JsonKind Kind { get; }
    readonly string? _s;
    readonly double _n;
    readonly bool _b;
    readonly ImmutableDictionary<string, AgentValue>? _o;
    readonly ImmutableArray<AgentValue> _a;

    AgentValue(JsonKind kind, string? s = null, double n = 0, bool b = false, ImmutableDictionary<string, AgentValue>? o = null, ImmutableArray<AgentValue> a = default)
    { Kind = kind; _s = s; _n = n; _b = b; _o = o; _a = a; }

    public static readonly AgentValue Null = new(JsonKind.Null);
    public static AgentValue String(string s) => new(JsonKind.String, s: s);
    public static AgentValue Number(double n) => new(JsonKind.Number, n: n);
    public static AgentValue Bool(bool b) => new(JsonKind.Bool, b: b);
    public static AgentValue Object(IEnumerable<KeyValuePair<string, AgentValue>> o) => new(JsonKind.Object, o: o.ToImmutableDictionary());
    public static AgentValue Object(params (string Key, AgentValue Value)[] o) => new(JsonKind.Object, o: o.ToImmutableDictionary(p => p.Key, p => p.Value));
    public static AgentValue Array(IEnumerable<AgentValue> a) => new(JsonKind.Array, a: [.. a]);

    public static implicit operator AgentValue(string s) => String(s);
    public static implicit operator AgentValue(double n) => Number(n);
    public static implicit operator AgentValue(bool b) => Bool(b);

    /// <summary>The text of a string value, else nil.</summary>
    public string? AsString => Kind == JsonKind.String ? _s : null;

    /// <summary>A number, a numeric string or a bool (1/0) as a number; else nil.</summary>
    public double? AsNumber => Kind switch
    {
        JsonKind.Number => _n,
        JsonKind.String => SwiftDouble(_s!),
        JsonKind.Bool => _b ? 1 : 0,
        _ => null,
    };

    public bool? AsBool => Kind == JsonKind.Bool ? _b : null;
    public ImmutableDictionary<string, AgentValue>? AsObject => Kind == JsonKind.Object ? _o : null;
    public ImmutableArray<AgentValue>? AsArray => Kind == JsonKind.Array ? _a : null;

    /// <summary>Swift's <c>Double(String)</c>: the whole text must be a decimal number (no spaces, no thousands separators).</summary>
    internal static double? SwiftDouble(string s)
    {
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return null;
        if (s.Contains(',')) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>The value as text for a path or query (Siblings.swift).</summary>
    public string PlainString => Kind switch
    {
        JsonKind.String => _s!,
        JsonKind.Number => _n == Math.Round(_n) && Math.Abs(_n) < 1e15 ? ((long)_n).ToString(CultureInfo.InvariantCulture) : _n.ToString("R", CultureInfo.InvariantCulture),
        JsonKind.Bool => _b ? "true" : "false",
        _ => "",
    };

    /// <summary>Foundation-like form: string, double, bool, Dictionary, List, null.</summary>
    public object? ToObject() => Kind switch
    {
        JsonKind.String => _s,
        JsonKind.Number => _n,
        JsonKind.Bool => _b,
        JsonKind.Object => _o!.ToDictionary(p => p.Key, p => p.Value.ToObject()),
        JsonKind.Array => _a.Select(v => v.ToObject()).ToList(),
        _ => null,
    };

    public bool Equals(AgentValue? other)
    {
        if (other is null || other.Kind != Kind) return false;
        return Kind switch
        {
            JsonKind.String => _s == other._s,
            JsonKind.Number => _n.Equals(other._n),
            JsonKind.Bool => _b == other._b,
            JsonKind.Object => _o!.Count == other._o!.Count && _o.All(p => other._o.TryGetValue(p.Key, out var v) && p.Value.Equals(v)),
            JsonKind.Array => _a.Length == other._a.Length && _a.Zip(other._a).All(p => p.First.Equals(p.Second)),
            _ => true,
        };
    }
    public override bool Equals(object? obj) => Equals(obj as AgentValue);
    public override int GetHashCode() => Kind switch
    {
        JsonKind.String => HashCode.Combine(Kind, _s),
        JsonKind.Number => HashCode.Combine(Kind, _n),
        JsonKind.Bool => HashCode.Combine(Kind, _b),
        JsonKind.Object => HashCode.Combine(Kind, _o!.Count),   // order-free; equal objects have equal counts
        JsonKind.Array => HashCode.Combine(Kind, _a.Length),
        _ => 0,
    };
    public static bool operator ==(AgentValue? a, AgentValue? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(AgentValue? a, AgentValue? b) => !(a == b);


    /// <summary>The value as a JSON node (for request bodies and tool schemas).</summary>
    public JsonNode? ToNode() => Kind switch
    {
        JsonKind.String => JsonValue.Create(_s),
        JsonKind.Number => JsonValue.Create(_n),
        JsonKind.Bool => JsonValue.Create(_b),
        JsonKind.Object => new JsonObject(_o!.Select(p => KeyValuePair.Create(p.Key, p.Value.ToNode()))),
        JsonKind.Array => new JsonArray(_a.Select(v => v.ToNode()).ToArray()),
        _ => null,
    };

    /// <summary>A value from a JSON node; anything that is not JSON becomes null.</summary>
    public static AgentValue FromNode(JsonNode? node) => node switch
    {
        null => Null,
        JsonObject o => Object(o.Select(p => KeyValuePair.Create(p.Key, FromNode(p.Value)))),
        JsonArray a => Array(a.Select(FromNode)),
        JsonValue v when v.TryGetValue<bool>(out var b) => Bool(b),
        JsonValue v when v.TryGetValue<string>(out var s) => String(s),
        JsonValue v when v.TryGetValue<double>(out var d) => Number(d),
        _ => Null,
    };

    public override string ToString() => JsonSerializer.Serialize(this);
}

/// <summary>Codable of the Swift enum: null, true/false, a number, a string, an array or an object, in that order of preference.</summary>
sealed class AgentValueConverter : JsonConverter<AgentValue>
{
    public override AgentValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null: return AgentValue.Null;
            case JsonTokenType.True: return AgentValue.Bool(true);
            case JsonTokenType.False: return AgentValue.Bool(false);
            case JsonTokenType.Number: return AgentValue.Number(reader.GetDouble());
            case JsonTokenType.String: return AgentValue.String(reader.GetString()!);
            case JsonTokenType.StartArray:
                {
                    var items = new List<AgentValue>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) items.Add(Read(ref reader, typeToConvert, options));
                    return AgentValue.Array(items);
                }
            case JsonTokenType.StartObject:
                {
                    var props = new Dictionary<string, AgentValue>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        string key = reader.GetString()!;
                        reader.Read();
                        props[key] = Read(ref reader, typeToConvert, options);
                    }
                    return AgentValue.Object(props);
                }
            default: return AgentValue.Null;
        }
    }

    public override void Write(Utf8JsonWriter writer, AgentValue value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case JsonKind.String: writer.WriteStringValue(value.AsString); break;
            case JsonKind.Number: writer.WriteNumberValue(value.AsNumber!.Value); break;
            case JsonKind.Bool: writer.WriteBooleanValue(value.AsBool!.Value); break;
            case JsonKind.Object:
                writer.WriteStartObject();
                foreach (var p in value.AsObject!) { writer.WritePropertyName(p.Key); Write(writer, p.Value, options); }
                writer.WriteEndObject();
                break;
            case JsonKind.Array:
                writer.WriteStartArray();
                foreach (var v in value.AsArray!.Value) Write(writer, v, options);
                writer.WriteEndArray();
                break;
            default: writer.WriteNullValue(); break;
        }
    }
}

public sealed record AgentParam(string Name, string Type, string Description, IReadOnlyList<string>? Options = null);

/// <summary>
/// One operation the planner may use. The system prompt is generated from this
/// list, so the model and the executor can't drift apart.
/// </summary>
public sealed record AgentOp(string Name, string Description, IReadOnlyList<AgentParam> Params, bool IsAIJob = false)
{
    public AgentOp(string name, string description) : this(name, description, [], false) { }
    public AgentOp(string name, string description, IReadOnlyList<AgentParam> @params) : this(name, description, @params, false) { }

    /// <summary>Through the API, a request with this step always runs as a job (202), like every paid step.</summary>
    public bool StartsJob => IsAIJob;
}

[JsonConverter(typeof(AgentStepConverter))]
public sealed record AgentStep : IPlanStep
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Op { get; init; }
    public ImmutableDictionary<string, AgentValue> Params { get; init; }
    public string? Rationale { get; init; }

    public AgentStep(string op, IEnumerable<KeyValuePair<string, AgentValue>>? @params = null, string? rationale = null)
    {
        Op = op;
        Params = @params?.ToImmutableDictionary() ?? ImmutableDictionary<string, AgentValue>.Empty;
        Rationale = rationale;
    }

    public AgentStep(string op, params (string Key, AgentValue Value)[] @params)
        : this(op, @params.Select(p => KeyValuePair.Create(p.Key, p.Value))) { }

    public string? String(string key) => Params.GetValueOrDefault(key)?.AsString;
    string? IPlanStep.StringParam(string key) => String(key);
    public double? Number(string key) => Params.GetValueOrDefault(key)?.AsNumber;
    public ImmutableDictionary<string, AgentValue> Object(string key) => Params.GetValueOrDefault(key)?.AsObject ?? ImmutableDictionary<string, AgentValue>.Empty;

    /// <summary>The value, unless it is absent or <c>null</c>.</summary>
    public AgentValue? Get(string key) => Params.GetValueOrDefault(key);

    public bool Equals(AgentStep? other) =>
        other is not null && Id == other.Id && Op == other.Op && Rationale == other.Rationale
        && Params.Count == other.Params.Count && Params.All(p => other.Params.TryGetValue(p.Key, out var v) && p.Value.Equals(v));
    public override int GetHashCode() => HashCode.Combine(Id, Op, Rationale, Params.Count);
}

/// <summary>Lenient, like the Swift decoder: <c>op</c> defaults to "", <c>params</c> to {}, an unreadable <c>rationale</c> to nil.</summary>
sealed class AgentStepConverter : JsonConverter<AgentStep>
{
    public override AgentStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var v = JsonSerializer.Deserialize<AgentValue>(ref reader, options);
        var o = v?.AsObject;
        if (o is null) throw new JsonException("An AgentStep is a JSON object.");
        string op = o.GetValueOrDefault("op")?.AsString ?? "";
        var p = o.GetValueOrDefault("params")?.AsObject ?? ImmutableDictionary<string, AgentValue>.Empty;
        return new AgentStep(op, p, o.GetValueOrDefault("rationale")?.AsString);
    }

    public override void Write(Utf8JsonWriter writer, AgentStep value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("op", value.Op);
        writer.WritePropertyName("params");
        JsonSerializer.Serialize(writer, AgentValue.Object(value.Params), options);
        if (value.Rationale is not null) writer.WriteString("rationale", value.Rationale);
        writer.WriteEndObject();
    }
}

public sealed record AgentPlan
{
    public ImmutableArray<AgentStep> Steps { get; init; }
    public string? Message { get; init; }
    /// <summary>Ops the model invented (dropped).</summary>
    public ImmutableArray<string> DroppedOps { get; init; } = [];
    /// <summary>How many steps got a missing kind from the request's own words (<c>PlanCheck.completed</c>).</summary>
    public int FilledFromRequest { get; init; }
    /// <summary>Figures the model stated that nobody gave it; their sentences were taken out (<c>AssistantNumbers</c>).</summary>
    public ImmutableArray<string> RemovedFigures { get; init; } = [];
    /// <summary>The answer as the model wrote it, kept when the figures check took something out of it (shown in the test of a model, not to a person).</summary>
    public string? MessageAsWritten { get; init; }
    /// <summary>
    /// What is wrong with steps that carry a parameter their operation doesn't have
    /// (<c>StepParameters</c>, and a sibling's closed schema): a plan with one is not run at all.
    /// </summary>
    public ImmutableArray<string> StrayParameters { get; init; } = [];
    /// <summary>The same for a person: what was asked for a setting it doesn't have, and the setting's name (the first such step).</summary>
    public string? StraySubject { get; init; }
    public string? StrayName { get; init; }

    public AgentPlan(IEnumerable<AgentStep> steps, string? message = null, IEnumerable<string>? droppedOps = null)
    {
        Steps = [.. steps];
        Message = message;
        DroppedOps = droppedOps is null ? [] : [.. droppedOps];
    }

    public bool UsesAI => Steps.Any(s => AgentPlanner.Op(s.Op)?.IsAIJob == true);

    public bool Equals(AgentPlan? other) =>
        other is not null && Steps.SequenceEqual(other.Steps) && Message == other.Message && DroppedOps.SequenceEqual(other.DroppedOps)
        && FilledFromRequest == other.FilledFromRequest && RemovedFigures.SequenceEqual(other.RemovedFigures)
        && MessageAsWritten == other.MessageAsWritten && StrayParameters.SequenceEqual(other.StrayParameters)
        && StraySubject == other.StraySubject && StrayName == other.StrayName;
    public override int GetHashCode() => HashCode.Combine(Steps.Length, Message, DroppedOps.Length);

    // Plan checks (PlanCheck.swift reads an AgentPlan; the generic checks in PlanCheck.cs take the steps).

    /// <summary>
    /// A step that needs a kind and came without one gets it from the request when its words name exactly one kind (<c>PlanCheck.completed</c>);
    /// the step says where its type came from. Returns the plan and how many steps were filled in.
    /// </summary>
    public (AgentPlan Plan, int Filled) Completed(string request, IReadOnlyList<string> adjustments, IReadOnlyList<string> filters)
    {
        var (steps, filled) = PlanCheck.Completed(Steps, request, adjustments, filters, s => s.String("type") ?? "",
            (s, kind) => s with
            {
                Params = s.Params.SetItem("type", AgentValue.String(kind)),
                Rationale = string.Join(" ", new[] { s.Rationale, $"({PlanCheck.FilledNote})" }.Where(x => x is not null)),
            });
        return (this with { Steps = [.. steps] }, filled);
    }

    /// <summary>Whether the plan may run without being shown first (<c>PlanCheck.runsAtOnce</c>): no stray parameter anywhere, and every step that changes the document is named by the request.</summary>
    public bool RunsAtOnce(string request, Func<string, string?>? commandTitle = null) =>
        PlanCheck.RunsAtOnce(Steps, request, commandTitle, planHasStrayParameters: !StrayParameters.IsEmpty,
                             stepHasStray: s => StepParameters.StrayIn(s) is not null);

    /// <summary>The steps the request doesn't name, for telling the person why the plan waits.</summary>
    public List<AgentStep> Unnamed(string request, Func<string, string?>? commandTitle = null) => PlanCheck.Unnamed(Steps, request, commandTitle);

    /// <summary>The plan without its unasked-for self-contained extras, and those extras, when that split is safe (<c>PlanCheck.withoutExtras</c>).</summary>
    public (AgentPlan Runs, List<AgentStep> Offered)? WithoutExtras(string request, Func<string, string?>? commandTitle = null)
    {
        if (PlanCheck.WithoutExtras(Steps, request, commandTitle) is not { } split) return null;
        return (this with { Steps = [.. split.Runs] }, split.Offered);
    }
}

/// <summary>What the planner knows about the open document.</summary>
public sealed record AgentContext(int Width, int Height, int LayerCount, bool HasSelection, string ActiveLayerKind, string ActiveLayerName);

/// <summary>A plan kept for reuse: runs again without the planner (and without cost, unless it has AI steps).</summary>
public sealed record SavedAction(string Name, IReadOnlyList<AgentStep> Steps)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>
/// Where the saved actions are kept: the Mac's <c>UserDefaults</c> key <c>assistant.savedActions</c>. The editor supplies
/// an implementation (a JSON file in the app's data folder); the bytes are the JSON list of <see cref="SavedAction"/>.
/// </summary>
public interface ISavedActionStore
{
    /// <summary>The stored bytes, or null when nothing was stored.</summary>
    byte[]? Load();
    void Store(byte[] data);
}

/// <summary>Keeps the saved actions in memory (tests, and a run without a data folder).</summary>
public sealed class InMemorySavedActionStore : ISavedActionStore
{
    byte[]? _data;
    public byte[]? Load() => _data;
    public void Store(byte[] data) => _data = data;
}

public static class SavedActions
{
    static readonly JsonSerializerOptions Options = new() { Converters = { new SavedActionConverter() } };

    public static IReadOnlyList<SavedAction> Load(ISavedActionStore store)
    {
        if (store.Load() is not { } data) return [];
        try { return JsonSerializer.Deserialize<List<SavedAction>>(data, Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void Store(IReadOnlyList<SavedAction> actions, ISavedActionStore store) =>
        store.Store(JsonSerializer.SerializeToUtf8Bytes(actions, Options));
}

/// <summary>Swift's synthesized Codable: <c>{"id": "UPPER-CASE-UUID", "name": …, "steps": […]}</c>.</summary>
sealed class SavedActionConverter : JsonConverter<SavedAction>
{
    public override SavedAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var o = JsonSerializer.Deserialize<AgentValue>(ref reader, options)?.AsObject ?? throw new JsonException("A saved action is a JSON object.");
        var name = o.GetValueOrDefault("name")?.AsString ?? throw new JsonException("A saved action has a name.");
        var steps = (o.GetValueOrDefault("steps")?.AsArray ?? throw new JsonException("A saved action has steps."))
            .Select(s => JsonSerializer.Deserialize<AgentStep>(JsonSerializer.Serialize(s, options), options)!).ToList();
        var id = Guid.TryParse(o.GetValueOrDefault("id")?.AsString, out var g) ? g : throw new JsonException("A saved action has an id.");
        return new SavedAction(name, steps) { Id = id };
    }

    public override void Write(Utf8JsonWriter writer, SavedAction value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id.ToString("D").ToUpperInvariant());
        writer.WriteString("name", value.Name);
        writer.WritePropertyName("steps");
        JsonSerializer.Serialize(writer, value.Steps, options);
        writer.WriteEndObject();
    }
}

/// <summary><c>#RRGGBB</c> / <c>#RRGGBBAA</c> → sRGB colour (alpha ignored), and the reverse (the <c>hexString</c> extension in StyleTextStep.swift).</summary>
public static class HexColors
{
    public static SRGBColor? Parse(string? text)
    {
        if (text is null) return null;
        // Swift's .whitespaces: horizontal whitespace only, as for the original's trimming here.
        var s = text.Trim(' ', '\t');
        if (s.StartsWith('#')) s = s[1..];
        if (!(s.Length == 6 || s.Length == 8)) return null;
        if (!uint.TryParse(s.AsSpan(0, 6), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return null;
        return new SRGBColor(((v >> 16) & 0xff) / 255.0, ((v >> 8) & 0xff) / 255.0, (v & 0xff) / 255.0);
    }

    /// <summary><c>#RRGGBB</c>, the reverse of <see cref="Parse"/>.</summary>
    public static string HexString(this SRGBColor c)
    {
        static int Byte(double v) => (int)Math.Round(Math.Min(Math.Max(v, 0), 1) * 255, MidpointRounding.AwayFromZero);
        return $"#{Byte(c.R):X2}{Byte(c.G):X2}{Byte(c.B):X2}";
    }
}

public static partial class AgentPlanner
{
    /// <summary>
    /// Command ids the agent may run directly (no file dialogs or destructive app-level actions): the layer, selection,
    /// image and view commands that are not an adjustment, not system-provided and not one of those that open a sheet for
    /// a person (developing a RAW layer through the API is not designed yet).
    /// </summary>
    public static IReadOnlyList<CommandDescriptor> RunnableCommands => _runnable ??= BuildRunnable();
    static IReadOnlyList<CommandDescriptor>? _runnable;

    static IReadOnlyList<CommandDescriptor> BuildRunnable()
    {
        var excluded = new HashSet<CommandID>
        {
            CommandID.LayerStyle, CommandID.NewSolidFill, CommandID.NewGradientFill, CommandID.NewPatternFill,
            CommandID.FeatherSelection, CommandID.GrowSelection, CommandID.ShrinkSelection, CommandID.Crop, CommandID.ImageSize,
            CommandID.CanvasSize, CommandID.FreeTransform, CommandID.DevelopRaw,
        };
        return CommandRegistry.Standard.Descriptors
            .Where(d => (d.Category is CommandCategory.Layer or CommandCategory.Select or CommandCategory.Image or CommandCategory.View)
                        && d.Id.AdjustmentKind is null && !d.IsSystemProvided && !excluded.Contains(d.Id))
            .ToList();
    }

    /// <summary>A sibling app's tool name ("jammer_x"): <see cref="SiblingApp.Split"/>.</summary>
    static bool IsSiblingTool(string name) => SiblingApp.Split(name) is not null;

    /// <summary>C's <c>%.2g</c>: two significant digits, trailing zeros removed, exponent form outside 1e-4 ≤ |v| &lt; 100; ties round to even on the exact value.</summary>
    internal static string FormatG2(double v) => FormatG(v, 2);

    internal static string FormatG(double v, int precision)
    {
        if (v == 0) return "0";
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        bool neg = v < 0;
        // Exact decimal expansion, then round half to even at `precision` significant digits.
        string e = Math.Abs(v).ToString("E120", CultureInfo.InvariantCulture);   // d.ddd…E+xxx, exact for any double
        int ePos = e.IndexOf('E');
        int exp10 = int.Parse(e[(ePos + 1)..], CultureInfo.InvariantCulture);
        string digits = e[..ePos].Replace(".", "");
        var keep = digits[..precision].Select(c => c - '0').ToList();
        string rest = digits[precision..];
        bool up = rest[0] > '5' || (rest[0] == '5' && rest[1..].Any(c => c != '0')) || (rest[0] == '5' && !rest[1..].Any(c => c != '0') && (keep[^1] % 2 == 1));
        if (up)
        {
            int i = keep.Count - 1;
            while (i >= 0) { if (keep[i] == 9) { keep[i] = 0; i--; } else { keep[i]++; break; } }
            if (i < 0) { keep.Insert(0, 1); keep.RemoveAt(keep.Count - 1); exp10++; }
        }
        string d = string.Concat(keep);
        string body;
        if (exp10 < -4 || exp10 >= precision)
        {
            string mant = d[..1] + (d.Length > 1 ? "." + d[1..] : "");
            if (mant.Contains('.')) mant = mant.TrimEnd('0').TrimEnd('.');
            body = $"{mant}e{(exp10 < 0 ? '-' : '+')}{Math.Abs(exp10):00}";
        }
        else if (exp10 >= 0)
        {
            string ip = d[..(exp10 + 1)], fp = d[(exp10 + 1)..].TrimEnd('0');
            body = fp.Length > 0 ? ip + "." + fp : ip;
        }
        else
        {
            string fp = (new string('0', -exp10 - 1) + d).TrimEnd('0');
            body = "0." + fp;
        }
        return neg ? "-" + body : body;
    }

    static string Number(double v) => v == Math.Round(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : FormatG2(v);

    /// <summary>"brightness_contrast: brightness (-1…1, 0), contrast (-1…1, 0); …" from the schema.</summary>
    internal static string AdjustmentReference => string.Join("; ", AdjustmentKind.AllCases.Select(kind =>
    {
        var keys = kind.Sliders().Select(s => $"{s.Key} ({Number(s.Range.LowerBound)}…{Number(s.Range.UpperBound)}, {Number(s.DefaultValue)})").ToList();
        keys.AddRange(kind.Toggles().Select(t => $"{t.Key} (true/false)"));
        keys.AddRange(kind.Colors().Select(c => $"{c.Key} (#RRGGBB)"));
        return keys.Count == 0 ? null : $"{kind.RawValue()}: " + string.Join(", ", keys);
    }).Where(s => s is not null));

    internal static string FilterReference => string.Join("; ", FilterKind.AllCases.Select(kind =>
    {
        var keys = kind.Fields().Select(f => f.IsToggle ? $"{f.Key} (0/1)" : $"{f.Key} ({Number(f.Range.LowerBound)}…{Number(f.Range.UpperBound)}, {Number(f.DefaultValue)})").ToList();
        return $"{kind.RawValue()}: " + (keys.Count == 0 ? "no settings" : string.Join(", ", keys));
    }));

    static AgentParam P(string name, string type, string description, IReadOnlyList<string>? options = null) => new(name, type, description, options);
    static AgentOp Op(string name, string description, AgentParam[]? @params = null, bool aiJob = false) => new(name, description, @params ?? [], aiJob);

    /// <summary>Settings from here on are read when the register is first asked for (<see cref="Ops"/> is built once).</summary>
    public static IReadOnlyList<AgentOp> Ops => _ops ??= BuildOps();
    static IReadOnlyList<AgentOp>? _ops;

    static List<AgentOp> BuildOps()
    {
        var ops = new List<AgentOp>
        {
            Op("add_adjustment", "Add a non-destructive adjustment layer above the active layer (contrast, hue, black & white, curves …).", [
                P("type", "string", "Which adjustment.", AdjustmentKind.AllCases.Select(k => k.RawValue()).ToList()),
                P("params", "object", "Settings by key; omitted keys keep their default, {} is neutral. Keys (range, default) per type — " + AdjustmentReference + ". Moderate values look natural: exposure is in stops (±0.3–1), most −1…1 sliders are strong beyond ±0.4. For a color cast use color_balance (positive B = bluer, positive R = redder) or photo_filter."),
            ]),
            Op("apply_filter", "Apply a destructive pixel filter to the active pixel layer (inside the selection if there is one).", [
                P("type", "string", "Which filter.", FilterKind.AllCases.Select(k => k.RawValue()).ToList()),
                P("params", "object", "Settings by key; {} uses the defaults. Keys (range, default) per type — " + FilterReference + "."),
            ]),
            Op("add_layer_effect", "Add or update a layer style on the active layer. Only the keys you give change. Keys: dropShadow and innerShadow take color (#RRGGBB), opacity (0…1), angle (degrees), distance (px), size (px, the blur), spread (0…1); outerGlow takes color, opacity, size (px), spread (0…1); stroke takes color, opacity, width (px), position (outside, inside or center); colorOverlay takes color, opacity. All take blendMode (a blend mode id) and enabled (false turns the effect off). A new effect starts from: shadows black, opacity 0.75, angle 120, distance 10, size 10; glow pale yellow, opacity 0.75, size 10; stroke red, width 3, outside; overlay red, opacity 1.", [
                P("type", "string", "Which effect.", ["dropShadow", "innerShadow", "outerGlow", "stroke", "colorOverlay"]),
                P("params", "object", "Patch, e.g. {\"distance\": 8, \"opacity\": 0.5, \"size\": 12} for dropShadow, {\"width\": 4, \"color\": \"#ffffff\"} for stroke, or {}."),
            ]),
            Op("apply_preset", "Apply a finished photo look (adjustment group plus grain/vignette).", [
                P("name", "string", "Preset name.", Preset.All.Select(x => x.Name).ToList()),
            ]),
            Op("add_text", "Add a one-line text layer above the active layer (it doesn't wrap: a long text at a big size runs past the canvas, so check the bounds in the step's info and lower size if needed). Place it with position, or exactly with x and y; move it later with transform_layer (moveX, moveY). The step's info gives the new layer's id and bounds.", [
                P("text", "string", "The text."),
                P("color", "string", "#RRGGBB color (optional, default the foreground color)."),
                P("size", "number", "Font size in pixels (optional; default ~8% of the image height)."),
                P("bold", "boolean", "Optional. true for bold text."),
                P("fontName", "string", "Optional. PostScript font name, e.g. Georgia-Bold (default Helvetica Neue)."),
                P("position", "string", "Optional. Centered horizontally; top and bottom leave 8 % of the image height to the edge; center is the default.", ["top", "center", "bottom"]),
                P("x", "number", "Optional. Left edge of the text in document pixels (instead of position)."),
                P("y", "number", "Optional. Top edge of the text in document pixels (instead of position)."),
            ]),
            Op("image_edit_composite", "Edit the WHOLE visible image with a natural-language instruction (content-aware: \"make it snowy\", \"change the car to red\"). The result lands as a new layer.", [
                P("instruction", "string", "The change to make."),
            ], aiJob: true),
            Op("generate_layer", "Generate a new image from a text prompt as a new layer.", [
                P("prompt", "string", "What to generate."),
            ], aiJob: true),
            Op("auto_enhance", "Automatic tone and color correction (on-device, free, instant) as editable adjustment layers. A good first step for \"fix\", \"improve\" or \"make it look better\" before any creative changes."),
            Op("auto_color", "Neutralize a color cast (on-device, free) with a Color Balance layer. For \"too yellow/blue/green\", \"fix the white balance\"."),
            Op("select_sky", "Select the sky (on-device, free). Use before replacing or adjusting the sky."),
            Op("select_people", "Select every person in the image (on-device, free, more precise than select_subject for people)."),
            Op("remove_background", "Cut the main subject out of the active layer onto transparency (adds a mask; on-device, free). Works on a pixel layer or a smart object, such as an image just added with add_image."),
            Op("select_subject", "Select the main subject (on-device, free)."),
            Op("inpaint_selection", "Regenerate or erase the SELECTED region. Requires a selection.", [
                P("prompt", "string", "What to fill the selection with (may be empty for remove)."),
                P("mode", "string", "fill replaces with the prompt; remove erases an object.", ["fill", "remove"]),
            ], aiJob: true),
            Op("upscale", "Upscale the whole image.", [
                P("scale", "number", "2 or 4 (on-device Real-ESRGAN, free)."),
            ]),
            Op("fill", "Fill the selection (or the whole active layer) with a color.", [
                P("color", "string", "#RRGGBB or #RRGGBBAA."),
            ]),
            Op("set_foreground", "Set the foreground (paint) color.", [
                P("color", "string", "#RRGGBB."),
            ]),
            Op("select_all", "Select the whole canvas."),
            Op("deselect", "Clear the selection."),
            Op("invert_selection", "Invert the selection."),
            Op("feather_selection", "Soften the selection edge.", [P("radius", "number", "Pixels.")]),
            Op("select_layer", "Make a layer the active one, so the following steps act on it. New layers become active by themselves.", [
                P("name", "string", "The layer's name (not case-sensitive; the topmost match wins) or its id."),
            ]),
            Op("new_layer", "Add an empty pixel layer.", [P("name", "string", "Optional layer name.")]),
            Op("transform_layer", "Move, scale, rotate, skew or distort a layer or a group, like Free Transform. To only move a layer, give moveX and/or moveY. The step's info gives the layer's bounds afterwards. Text stays editable for scale, rotation and skew; corners (distort or perspective) would turn text into pixels and are refused unless allowRasterize is true. One undo step.", [
                P("target", "string", "Optional. The layer's or group's name or id; default the active layer."),
                P("scaleX", "number", "Optional. Width in percent of the current size (default 100)."),
                P("scaleY", "number", "Optional. Height in percent of the current size (default 100)."),
                P("degrees", "number", "Optional. Rotation about the center, clockwise."),
                P("moveX", "number", "Optional. Move right by this many document pixels (negative: left)."),
                P("moveY", "number", "Optional. Move down by this many document pixels (negative: up)."),
                P("skewX", "number", "Optional. Horizontal skew in degrees."),
                P("skewY", "number", "Optional. Vertical skew in degrees."),
                P("corners", "points", "Optional. Four corners in document pixels, top-left, top-right, bottom-right, bottom-left, for distort or perspective (replaces move, scale, rotation and skew). They say where the corners of the layer's bounds (as GET document reports them) end up."),
                P("allowRasterize", "boolean", "Optional. Let corners turn text layers into pixels (default false)."),
            ]),
            Op("style_text", "Give words in a text layer their own font, boldness, size or color (the rest of the text keeps its style). Find the words with match (preferred), or give from and length in characters; with neither, the whole text is styled. Stays editable text; one undo step. The step's info says which ranges changed.", [
                P("target", "string", "Optional. The text layer's name or id; default the active layer."),
                P("match", "string", "Optional. The word or phrase to style (exact text, or ignoring case if the exact text isn't there). Preferred over from/length."),
                P("occurrence", "string", "Optional. Which matches to style (default all).", ["all", "first"]),
                P("from", "number", "Optional, instead of match. Start, in characters from 0. Characters are grapheme clusters: 👍🏽 or å counts as 1."),
                P("length", "number", "Optional, with from. How many characters (grapheme clusters)."),
                P("fontName", "string", "Optional. PostScript font name, e.g. HelveticaNeue-Bold."),
                P("bold", "boolean", "Optional. true for the bold member of each word's font family, false for the regular one."),
                P("size", "number", "Optional. Font size in document pixels."),
                P("color", "string", "Optional. #RRGGBB."),
            ]),
            Op("crop", "Crop the canvas for every layer, like the Crop tool. Give exactly one of: a rectangle (x, y, width, height), toSelection, or aspect. A rectangle partly outside the canvas is clipped to it; the step's info says which box was used. One undo step.", [
                P("x", "number", "Optional. Left edge of the box in document pixels (default 0)."),
                P("y", "number", "Optional. Top edge of the box in document pixels (default 0)."),
                P("width", "number", "Optional, with a rectangle. Width of the box in pixels."),
                P("height", "number", "Optional, with a rectangle. Height of the box in pixels."),
                P("toSelection", "boolean", "Optional, instead of a rectangle. true crops to the bounds of the selection."),
                P("aspect", "string", "Optional, instead of a rectangle. W:H, like 16:9 or 1:1: the largest centered box of that shape."),
                P("deletePixels", "boolean", "Optional. true throws away what is outside the box; false (default) keeps it outside the canvas."),
            ]),
            Op("resize_image", "Resample the whole image to a new size, like Image › Image Size: every layer, mask, path and guide is scaled. Give width or height alone to keep the proportions; give both for exactly that size (the proportions may change); or give scale. Text and smart objects stay editable. One undo step.", [
                P("width", "number", "Optional. New width in pixels."),
                P("height", "number", "Optional. New height in pixels."),
                P("scale", "number", "Optional, instead of width and height. 0.5 halves both sides, 2 doubles them."),
            ]),
            Op("resize_canvas", "Give the image more room, or less, without scaling it, like Image › Canvas Size. Give width and/or height (one alone keeps the other side), or aspect. New room is transparent unless fill is given; what a smaller canvas cuts off is kept outside it. The step's info says which box was used. One undo step.", [
                P("width", "number", "Optional. New canvas width in pixels."),
                P("height", "number", "Optional. New canvas height in pixels."),
                P("aspect", "string", "Optional, instead of width and height. W:H, like 16:9: the smallest canvas of that shape that holds the whole image."),
                P("anchor", "string", "Optional. Where the image stays (default center); new room goes on the other sides.", CanvasSteps.AnchorNames),
                P("fill", "string", "Optional. #RRGGBB: adds a color fill layer at the bottom so the new room isn't transparent. Ignored when no room is added."),
            ]),
            Op("select_rect", "Select a rectangle or an ellipse. A box partly outside the canvas is clipped to it.", [
                P("x", "number", "Left edge in document pixels."),
                P("y", "number", "Top edge in document pixels."),
                P("width", "number", "Width in pixels."),
                P("height", "number", "Height in pixels."),
                P("shape", "string", "Optional. rect (default) or ellipse (inscribed in the box).", ["rect", "ellipse"]),
                P("mode", "string", "Optional. How it combines with the selection there is (default replace).", ["replace", "add", "subtract", "intersect"]),
                P("feather", "number", "Optional. Soften the edge by this many pixels."),
            ]),
            Op("set_layer", "Change a layer's opacity, blend mode, visibility or name. One undo step.", [
                P("target", "string", "Optional. The layer's id or name; default the active layer. A name two layers share is refused: use the id."),
                P("opacity", "number", "Optional. 0…1, as GET document reports it."),
                P("blendMode", "string", "Optional. The blend mode id, as GET document reports it (pass-through for groups only).", BlendMode.AllCases.Select(m => m.RawValue()).ToList()),
                P("visible", "boolean", "Optional. Show or hide the layer."),
                P("name", "string", "Optional. The new name."),
            ]),
            Op("add_waveform", "Draw the waveform of the sound open in Jämmer (the series' audio editor, a separate app) as a new shape layer, for a cover or poster. It stays a vector shape the user can recolor, move and style.", [
                P("style", "string", "Optional. bars (default) or wave (one outline).", Enum.GetValues<SiblingWaveform.Style>().Select(s => s.RawValue()).ToList()),
                P("bars", "number", "Optional. How many bars, 16–400 (default 64)."),
                P("color", "string", "Optional. #RRGGBB; default the foreground color."),
                P("height", "number", "Optional. Share of the canvas height, 0.05–1 (default 0.3)."),
                P("width", "number", "Optional. Share of the canvas width, 0.1–1 (default 0.8)."),
                P("position", "string", "Optional. center (default), top or bottom.", ["center", "top", "bottom"]),
                P("document", "string", "Optional. The Jämmer document id or name; default the one in front."),
            ]),
            Op("run_command", "Run an editor menu command.", [
                P("id", "string", "Command id.", RunnableCommands.Select(d => d.Id.RawValue).ToList()),
            ]),
        };
        ops.AddRange(OptionalOps());
        return ops;
    }

    /// <summary>Operations of features a release can be built without (Directory.Build.props: GLIMMER_RAW, GLIMMER_HEAL).</summary>
    static List<AgentOp> OptionalOps()
    {
        var list = new List<AgentOp>();
#if GLIMMER_RAW
        list.Add(Op("develop_raw", "Develop a RAW layer again from its camera file with other settings (on-device, free): brighter or darker, warmer or cooler, lifted shadows. Only for a layer that was opened from a RAW file (its `raw` in the document's layers); for any other layer use add_adjustment. Only the values given change. One undo step. Refused with the allowed range when a value is outside it, and by name for a control the file doesn't have: `GET /documents/{id}/layers/{layer}/raw/info` says what the file allows and what the settings are now.", [
            P("target", "string", "Optional. The RAW layer's id or name (default: the active layer)."),
            P("exposure", "number", "Optional. Stops added to the file's own exposure, -5…5; 1 is twice as bright."),
            P("temperature", "number", "Optional. White balance in kelvin, 2000…50000. A higher number makes the picture warmer (more yellow), a lower one cooler (more blue)."),
            P("tint", "number", "Optional. -150…150; negative is greener, positive more magenta."),
            P("shadows", "number", "Optional. 0…2; 1 leaves the shadows as they are, more lifts them."),
            P("contrast", "number", "Optional. 0…1, where the file has the control."),
            P("sharpness", "number", "Optional. 0…1, where the file has the control."),
            P("luminanceNoise", "number", "Optional. Noise reduction, 0…1, where the file has the control."),
            P("colorNoise", "number", "Optional. Color noise reduction, 0…1, where the file has the control."),
            P("highlightRecovery", "boolean", "Optional. Recover detail in blown highlights (on by default), where the file has the switch."),
            P("lensCorrection", "boolean", "Optional. Use the lens correction stored in the file, where there is one. It reshapes the whole picture a little, not only the corners."),
            P("asShot", "boolean", "Optional. true first goes back to the camera's white balance and the standard values; other values given in the same step are then applied."),
            P("replaceChanges", "boolean", "Optional. A layer that was painted on or filtered after it was developed is refused (replaces_changes) unless this is true: developing again replaces those changes."),
        ]));
#endif
#if GLIMMER_HEAL
        list.Add(Op("heal_selection", "Fill the SELECTED area from what surrounds it, to take out a blemish, a wire or a small object (on-device, free, no model). Requires a selection. Copies existing image, so it suits areas up to about 120 px across on texture or even surfaces; larger areas are refused with reason area_too_large unless allowLarge is true. The step's info gives the method used, the pixels filled, the area's width at its thickest and the layer. One undo step; the selection stays.", [
            P("method", "string", "Optional. auto (default) picks; smooth suits thin lines and even areas; texture copies patches of the surroundings.", ["auto", "smooth", "texture"]),
            P("expand", "number", "Optional. Pixels the selection is grown by first, 0–50 (default 3), so the object's edge and halo go with it."),
            P("sampleAllLayers", "boolean", "Optional. true (default): reads what is visible and puts the fill in a new layer named Heal above the active layer. false: reads and changes the active pixel layer itself."),
            P("allowLarge", "boolean", "Optional. true fills an area over about 120 px across anyway; the result may smear or repeat (info.quality is then may_repeat)."),
        ]));
#endif
        return list;
    }

    public static AgentOp? Op(string name) => Ops.FirstOrDefault(o => o.Name == name);

    internal const string Preamble = """
        You are the planning brain of Glimmer, a native Windows photo editor (a modern Photoshop). The user states a goal in natural language; you turn it into an ordered PLAN of editor operations that a deterministic executor runs.

        Only use the operations listed under AVAILABLE OPS. Each step names one op exactly and supplies concrete params. Never invent ops, params or values that are not described.

        Guidelines:
        - Order steps the way an expert retoucher would (select before inpainting; remove the background before adding a drop shadow).
        - Prefer the editor's native, non-destructive ops (add_adjustment, apply_preset, add_layer_effect, apply_filter, selection ops). Use image_edit_composite and generate_layer only for content changes the native ops cannot express: they cost money and take time.
        - inpaint_selection needs a selection. If there is none and the goal needs one, add a step that makes one (select_subject, select_all + invert_selection …) or ask a clarifying question.
        - Colors are #RRGGBB hex strings.
        - When unsure about a param, send {} (or omit it) to get sensible defaults rather than guessing exotic numbers.
        - If the goal is ambiguous, contradictory or impossible with these ops, return an empty "steps" array and put a short clarifying question in "message".
        - If the user is only chatting or asking a question about the editor, answer briefly in "message" with no steps.
        - If asked what something costs or whether it is free, don't assert it: say that prices are the service's and that the model and its price are shown in Settings › AI.
        - Plan only what the request asks for. Don't add auto_enhance, auto_color or any other improvement the user didn't ask for.
        - Never state a number you were not given. Sizes, layer counts and other facts about the document come only from the editor state in the request; never state a price, a cost or how long something takes.
        - Keep each step's "rationale" to one short sentence.

        OUTPUT FORMAT — return ONLY one JSON object, no markdown:
        {"steps": [{"op": "<op name>", "params": {…}, "rationale": "<short>"}], "message": "<short summary of the plan, or a question>"}
        """;

    public static string RenderOps() => string.Join("\n\n", Ops.Select(op =>
    {
        string @params = op.Params.Count == 0 ? "    (no params)" : string.Join("\n", op.Params.Select(p =>
        {
            string opts = p.Options is null ? "" : $" — one of: {string.Join(", ", p.Options)}";
            return $"    - {p.Name} ({p.Type}): {p.Description}{opts}";
        }));
        return $"- {op.Name}{(op.IsAIJob ? " [AI job]" : "")}: {op.Description}\n{@params}";
    }));

    public static string SystemPrompt() => Preamble + "\n\nAVAILABLE OPS:\n" + RenderOps();

    public static string UserPrompt(string goal, AgentContext context, IReadOnlyList<(string User, string Reply)> history)
    {
        var text = new StringBuilder();
        text.Append($"""
            CURRENT EDITOR CONTEXT:
            - canvas: {context.Width}×{context.Height} px
            - layers: {context.LayerCount}
            - active layer: {context.ActiveLayerName} ({context.ActiveLayerKind})
            - hasSelection: {(context.HasSelection ? "true" : "false")}

            """);
        if (history.Count > 0)
        {
            text.Append("EARLIER IN THIS CONVERSATION:\n");
            foreach (var turn in history.Skip(Math.Max(0, history.Count - 6)))
                text.Append($"- user: {turn.User}\n  you: {turn.Reply}\n");
            text.Append('\n');
        }
        text.Append($"USER GOAL:\n{goal}\n\nReturn the JSON plan now.");
        return text.ToString();
    }

    /// <summary>
    /// Parses the model's reply: tolerates code fences and prose around the JSON,
    /// drops unknown ops and malformed steps instead of failing the plan.
    /// </summary>
    public static AgentPlan ParsePlan(string text)
    {
        var json = ExtractJsonObject(text);
        if (json is null)
        {
            // Plain prose: treat as a chat answer.
            var trimmed = text.Trim();   // Swift .whitespacesAndNewlines
            if (trimmed.Length == 0) throw new AgentError("The assistant returned an empty answer.");
            return new AgentPlan([], trimmed);
        }
        var raw = JsonSerializer.Deserialize<AgentValue>(json)?.AsObject
                  ?? throw new JsonException("The plan is not a JSON object.");
        var steps = new List<AgentStep>();
        var dropped = new List<string>();
        foreach (var value in raw.GetValueOrDefault("steps")?.AsArray ?? [])
        {
            AgentStep? step;
            try { step = JsonSerializer.Deserialize<AgentStep>(JsonSerializer.Serialize(value)); } catch (JsonException) { continue; }
            if (step is null) continue;
            if (Op(step.Op) is null && !IsSiblingTool(step.Op)) dropped.Add(step.Op);
            else steps.Add(step);
        }
        return new AgentPlan(steps, raw.GetValueOrDefault("message")?.AsString, dropped);
    }

    internal static string? ExtractJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false, escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    // MARK: Suggestions

    /// <summary>An edit the assistant proposes for this particular photo.</summary>
    public sealed record Suggestion(
        /// <summary>Two or three words for a button.</summary>
        string Title,
        /// <summary>The request to send to the planner when it is chosen.</summary>
        string Goal)
    {
        public string Id => Title;
    }

    internal const string SuggestionPrompt = """
        You are a photo retoucher looking at a user's image in a photo editor. Propose exactly three different edits that would genuinely improve or creatively transform THIS photo, based on what you see (its light, colors, subject, flaws). Make them distinct: for example one corrective, one mood, one bold.
        The editor can do: tonal and color adjustments, black & white, blur/sharpen/vignette/grain filters, photo looks, text, removing the background, and AI edits of content.
        Return ONLY JSON: {"suggestions": [{"title": "<2-3 words>", "goal": "<one sentence instructing the edit, specific to this photo>"}]}
        Write title and goal in the language with code "%@".
        """;

    public static IReadOnlyList<Suggestion> ParseSuggestions(string text)
    {
        if (ExtractJsonObject(text) is not { } json) return [];
        JsonElement items;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            items = arr.Clone();
        }
        catch (JsonException) { return []; }
        var seen = new HashSet<string>();
        var result = new List<Suggestion>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Str(string k) => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var title = Str("title")?.Trim();
            var goal = Str("goal")?.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(goal) || !seen.Add(title)) continue;
            // A suggestion's numbers are its proposal ("crop to 4:5"); a price is never the model's to state.
            if (AssistantNumbers.Checked(goal, [], pricesOnly: true).Text is not { } g) continue;
            if (AssistantNumbers.Checked(title, [], pricesOnly: true).Text is not { } t) continue;
            result.Add(new Suggestion(t, g));
            if (result.Count == 3) break;
        }
        return result;
    }

    /// <summary>Looks at the image (JPEG) and proposes three edits.</summary>
    public static async Task<IReadOnlyList<Suggestion>> SuggestionsAsync(byte[] image, string language, ITextModel model, CancellationToken ct = default)
    {
        var response = await model.ReplyAsync(null, SuggestionPrompt.Replace("%@", language), [image], "image/jpeg", json: true, temperature: 0.7, ct).ConfigureAwait(false);
        return ParseSuggestions(response.Text);
    }

    // MARK: Tool calling

    internal const string ToolPreamble = """
        You are the planning brain of Glimmer, a native Windows photo editor (a modern Photoshop). The user states a goal in natural language; you carry it out by calling the editor's tools. Every tool call becomes one step of a plan that a deterministic executor runs in the order you call them.

        Guidelines:
        - Call tools in the order an expert retoucher would (select before inpainting; remove the background before adding a drop shadow).
        - Prefer the editor's native, non-destructive tools (add_adjustment, apply_preset, add_layer_effect, apply_filter, selection tools). Use image_edit_composite and generate_layer only for content changes the native tools cannot express: they cost money and take time.
        - inpaint_selection needs a selection. If there is none and the goal needs one, make one first (select_subject, select_all …) or ask a clarifying question.
        - Colors are #RRGGBB hex strings. When unsure about a setting, pass {} to get sensible defaults rather than guessing exotic numbers.
        - Give each call a one-sentence "rationale".
        - Alongside the calls, reply with one short sentence summarizing the plan, in the user's language.
        - If the goal is ambiguous, contradictory or impossible with these tools, call no tools and ask a short clarifying question.
        - If the user is only chatting or asking about the editor, answer briefly without calling tools.
        - When a picture of the current image is attached, look at it: choose settings that suit this photo (its exposure, colors and subject) rather than generic ones.
        - Tools whose names start with jammer_ or flimmer_ (when present) act in Glimmer's sibling apps Jämmer (audio) and Flimmer (video), which run separately. Use them only when the user asks for something in that app. To bring Jämmer's sound into the image, use add_waveform.
        """;

    internal const string ReviewPreamble = """
        You are the quality check of Glimmer, a photo editor. The user asked for an edit and the editor carried out the steps listed. You see the image BEFORE (first picture) and AFTER (second picture).

        Judge whether the result meets what the user asked for.
        - If it does (or is close enough), call no tools and reply with one short sentence saying what was done, in the user's language.
        - If it clearly falls short (too weak, too strong, wrong direction, something asked for is missing), call at most three tools to correct it, building on the current result, and say in one short sentence what you are adjusting. Do not repeat steps that already worked.
        Colors are #RRGGBB. Pass {} for default settings.
        """;

    /// <summary>
    /// The JSON Schema of an op's params. The assistant's tools, the API's operation
    /// list and the guide all use it, so they describe the same thing.
    /// Closed: a name that isn't in <c>properties</c> is refused by the app, and the schema says so.
    /// <paramref name="forModel"/> leaves the nested settings as a plain object (their keys are in the
    /// description): the per-type schemas are for callers that check before they call, and
    /// would double the list a model is sent.
    /// </summary>
    public static JsonObject ParamsSchema(AgentOp op, bool forModel = false)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in op.Params)
        {
            var schema = new JsonObject { ["description"] = p.Description };
            switch (p.Type)
            {
                case "number": schema["type"] = "number"; break;
                case "object": schema["type"] = "object"; break;
                case "boolean": schema["type"] = "boolean"; break;
                case "points":  // [[x, y], …]
                    schema["type"] = "array";
                    schema["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 };
                    break;
                default: schema["type"] = "string"; break;
            }
            if (p.Options is not null) schema["enum"] = new JsonArray(p.Options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
            if (p.Name == "params" && !forModel && StepParameters.NestedSchemas(op.Name) is { } byType)
            {
                // Which settings exist depends on `type`: one closed schema per type.
                schema["x-byType"] = byType;
                schema["x-byTypeKey"] = "type";   // which property chooses the sort: said, so a caller need not guess
            }
            properties[p.Name] = schema;
            if (p.Description.IndexOf("optional", StringComparison.CurrentCultureIgnoreCase) < 0) required.Add(p.Name);
        }
        properties["rationale"] = StepParameters.RationaleSchema;
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    /// <summary>
    /// One function tool per op; the schemas are generated from <see cref="Ops"/>.
    /// <paramref name="shortList"/>: only the everyday operations (the API's core tier), for small local models.
    /// <paramref name="rawLayer"/> false leaves out <c>develop_raw</c>: the assistant is offered it only when the active layer is a RAW layer.
    /// </summary>
    public static List<JsonObject> ToolSchemas(bool shortList = false, bool rawLayer = true) =>
        Ops.Where(o => (!shortList || AgentAPI.CoreOps.Contains(o.Name)) && (rawLayer || o.Name != "develop_raw")).Select(op =>
        {
            var parameters = ParamsSchema(op, forModel: true);
            var properties = (JsonObject)parameters["properties"]!;
            properties["rationale"] = new JsonObject { ["type"] = "string", ["description"] = "One short sentence: why this step." };
            string description = op.IsAIJob ? op.Description + " (AI job: costs money and takes a while.)" : op.Description;
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = op.Name, ["description"] = description, ["parameters"] = parameters },
            };
        }).ToList();

    /// <summary>The conversation: system prompt, earlier turns, then the goal with the editor's state.</summary>
    public static List<JsonObject> ToolMessages(string goal, AgentContext context, IReadOnlyList<(string User, string Reply)> history,
                                                byte[]? image = null, string? siblingContext = null)
    {
        var messages = new List<JsonObject> { new() { ["role"] = "system", ["content"] = ToolPreamble } };
        foreach (var turn in history.Skip(Math.Max(0, history.Count - 8)))
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn.User });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = turn.Reply });
        }
        var state = $"[Editor state: canvas {context.Width}×{context.Height} px, {context.LayerCount} layers, "
                  + $"active layer “{context.ActiveLayerName}” ({context.ActiveLayerKind}), "
                  + $"{(context.HasSelection ? "there is a selection" : "no selection")}.]";
        var text = state + (siblingContext is null ? "" : "\n" + siblingContext) + "\n\n" + goal;
        if (image is not null)
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }, ImagePart(image)) });
        else
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        return messages;
    }

    internal static JsonObject ImagePart(byte[] jpeg) =>
        new() { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } };

    /// <summary>The quality check after a plan ran: goal, what was done, before and after.</summary>
    public static List<JsonObject> ReviewMessages(string goal, IReadOnlyList<AgentStep> steps, byte[] before, byte[] after)
    {
        var done = string.Join("\n", steps.Select(step => $"- {step.Op} {AgentValue.Object(step.Params).ToString()}"));
        var text = $"USER REQUEST:\n{goal}\n\nSTEPS CARRIED OUT:\n{done}";
        return
        [
            new JsonObject { ["role"] = "system", ["content"] = ReviewPreamble },
            new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }, ImagePart(before), ImagePart(after)) },
        ];
    }

    /// <summary>Tools the quality check may use: the editor's own, never paid AI jobs.</summary>
    public static List<JsonObject> ReviewToolSchemas() =>
        Ops.Zip(ToolSchemas()).Where(p => !p.First.IsAIJob).Select(p => p.Second).ToList();

    /// <summary>Asks the model to judge the result; returns corrective steps (possibly none).</summary>
    public static async Task<AgentPlan> ReviewAsync(string goal, IReadOnlyList<AgentStep> steps, byte[] before, byte[] after, ITextModel model,
                                                    CancellationToken ct = default)
    {
        var response = await model.CallToolsAsync(ReviewMessages(goal, steps, before, after), ReviewToolSchemas(), 0.2, null, ct).ConfigureAwait(false);
        var plan = PlanFrom(response);
        return plan with { Steps = [.. plan.Steps.Where(s => Op(s.Op)?.IsAIJob == false).Take(3)] };
    }

    /// <summary>Turns tool calls into a plan, dropping unknown tools and calls with unreadable arguments. <paramref name="extra"/> are sibling tool names offered with this request.</summary>
    public static AgentPlan PlanFrom(TextToolReply response, ISet<string>? extra = null)
    {
        var steps = new List<AgentStep>();
        var dropped = new List<string>();
        foreach (var call in response.ToolCalls)
        {
            if (Op(call.Name) is null && extra?.Contains(call.Name) != true)
            {
                dropped.Add(call.Name);
                continue;
            }
            var @params = new Dictionary<string, AgentValue>();
            try
            {
                if (JsonSerializer.Deserialize<AgentValue>(call.Arguments)?.AsObject is { } decoded)
                    foreach (var p in decoded) @params[p.Key] = p.Value;
            }
            catch (JsonException) { }
            string? rationale = null;
            if (@params.Remove("rationale", out var r)) rationale = r.AsString;
            // Some models send nested objects as JSON strings.
            foreach (var (key, value) in @params.ToList())
            {
                if (value.AsString is { } s && s.StartsWith('{'))
                {
                    try { if (JsonSerializer.Deserialize<AgentValue>(s)?.AsObject is { } nested) @params[key] = AgentValue.Object(nested); }
                    catch (JsonException) { }
                }
            }
            steps.Add(new AgentStep(call.Name, @params, rationale));
        }
        var text = response.Text?.Trim();
        if (steps.Count == 0 && dropped.Count == 0 && text is not null && text.Contains("\"steps\"")) return ParsePlan(text);   // a model that ignored the tools and wrote a JSON plan anyway
        if (steps.Count == 0 && dropped.Count == 0 && string.IsNullOrEmpty(text)) throw AgentError.EmptyResponse;
        return new AgentPlan(steps, string.IsNullOrEmpty(text) ? null : text, dropped);
    }

    static string Joined(params string?[] parts) => string.Join(" ", parts.Where(p => p is not null));

    /// <summary>
    /// Asks the text model for a plan through tool calling. <paramref name="siblings"/> are the running sibling apps' operations, offered as
    /// extra tools, and <paramref name="siblingContext"/> names their open documents.
    /// </summary>
    public static async Task<(AgentPlan Plan, double? Cost)> PlanAsync(string goal, AgentContext context, IReadOnlyList<(string User, string Reply)> history,
                                                                       ITextModel model, byte[]? image = null, IReadOnlyList<SiblingOperation>? siblings = null,
                                                                       string? siblingContext = null, Action<string>? onText = null, CancellationToken ct = default)
    {
        siblings ??= [];
        var messages = ToolMessages(goal, context, history, image, siblingContext);
        var tools = ToolSchemas(model.PrefersShortToolList, context.ActiveLayerKind == "raw");
        if (!model.PrefersShortToolList) tools.AddRange(siblings.Select(s => s.ToolSchema));
        // The text is checked while it arrives too: the caller never gets a figure nobody gave the model.
        var given = GivenFigures(goal, context, history, siblingContext);
        Action<string>? checkedText = null;
        if (onText is not null)
        {
            var also = AlsoGivenFigures(goal, context);
            checkedText = text => onText(AssistantNumbers.ShownWhileArriving(text, given, also));
        }
        var response = await model.CallToolsAsync(messages, tools, 0.2, checkedText, ct).ConfigureAwait(false);
        var plan = PlanFrom(response, siblings.Select(s => s.ToolName).ToHashSet());
        // A kind the model left out is taken from the request when its words name exactly one (PlanCheck).
        var completed = plan.Completed(goal, AdjustmentKind.AllCases.Select(k => k.RawValue()).ToList(), FilterKind.AllCases.Select(k => k.RawValue()).ToList());
        plan = completed.Plan with { FilledFromRequest = completed.Filled };
        // A parameter the operation doesn't have: said in the step, as a kind taken from the request is, and the plan waits.
        for (int i = 0; i < plan.Steps.Length; i++)
        {
            if (StepParameters.StrayIn(plan.Steps[i]) is not { } stray) continue;
            plan = plan with { StrayParameters = plan.StrayParameters.Add(stray.Message) };
            if (plan.StraySubject is null) plan = plan with { StraySubject = stray.Subject, StrayName = stray.Name };
            plan = plan with { Steps = plan.Steps.SetItem(i, plan.Steps[i] with { Rationale = Joined(plan.Steps[i].Rationale, $"({StepParameters.StrayNote}: {stray.Name})") }) };
        }
        // The same for a step in a sibling app, against the list that app gave when the plan was
        // made: found now, before any step runs, not at step three with another app's document already changed.
        for (int i = 0; i < plan.Steps.Length; i++)
        {
            var op = siblings.FirstOrDefault(s => s.ToolName == plan.Steps[i].Op);
            if (op is null) continue;
            var @params = plan.Steps[i].Params.Remove("rationale").Remove("document");
            if (op.Stray(@params) is not { } stray)
            {
                // Settings per sort that the other app publishes without naming what chooses the sort: not checked, and said.
                if (op.UncheckedTable(@params) is { } place)
                    plan = plan with { Steps = plan.Steps.SetItem(i, plan.Steps[i] with { Rationale = Joined(plan.Steps[i].Rationale, $"({StepParameters.UncheckedNote} {place})") }) };
                continue;
            }
            plan = plan with { StrayParameters = plan.StrayParameters.Add(SiblingError.StrayParameter(op.ToolName, stray.Name, stray.Allowed).Message) };
            if (plan.StraySubject is null) plan = plan with { StraySubject = op.App.DisplayName(), StrayName = stray.Name };
            plan = plan with { Steps = plan.Steps.SetItem(i, plan.Steps[i] with { Rationale = Joined(plan.Steps[i].Rationale, $"({StepParameters.StrayNote}: {stray.Name})") }) };
        }
        plan = CheckFigures(plan, goal, context, history, siblingContext);
        return (plan, response.Cost);
    }

    /// <summary>What Glimmer itself says about the document, in the Assistant's place when the model's own answer had to be taken out.</summary>
    public static string StateSentence(AgentContext context) =>
        $"The document is {context.Width} × {context.Height} pixels and has {context.LayerCount} {(context.LayerCount == 1 ? "layer" : "layers")}.";

    /// <summary>Every text the model was given for this request: the figures in them may be stated.</summary>
    internal static List<string> GivenFigures(string goal, AgentContext context, IReadOnlyList<(string User, string Reply)> history, string? siblingContext)
    {
        var given = new List<string> { goal, $"{context.Width} {context.Height} {context.LayerCount}", context.ActiveLayerName, siblingContext ?? "" };
        given.AddRange(history.Skip(Math.Max(0, history.Count - 8)).Select(h => h.User));
        return given;
    }

    /// <summary>
    /// Each operation's own figures (its description and its parameters' are sent with every request), with
    /// the words that name it: its tool name, the stems by which a request names it in English and Swedish
    /// (<c>PlanCheck.Words</c>), its parameters' names, and for adjustments and filters the kinds' own stems.
    /// </summary>
    internal static IReadOnlyList<(IReadOnlyList<string> Names, IReadOnlyList<double> Figures)> OperationFigures => _figures ??= BuildFigures();
    static IReadOnlyList<(IReadOnlyList<string> Names, IReadOnlyList<double> Figures)>? _figures;

    static IReadOnlyList<(IReadOnlyList<string> Names, IReadOnlyList<double> Figures)> BuildFigures() =>
        Ops.Select(op =>
        {
            var names = new List<string> { op.Name, op.Name.Replace("_", " ") };
            if (PlanCheck.Words.TryGetValue(op.Name, out var words)) names.AddRange(words);
            names.AddRange(op.Params.Select(p => p.Name).Where(n => n.Length >= 5).Select(n => n.ToLowerInvariant()));
            if (op.Name is "add_adjustment" or "apply_filter")
            {
                names.AddRange(PlanCheck.Kinds.Values.SelectMany(v => v));
                names.AddRange(["adjustment", "justering", "filter"]);
            }
            if (op.Name == "upscale") names.AddRange(["upscal", "förstor", "skala upp", "enlarge"]);
            var figures = AssistantNumbers.Numbers(op.Description).Concat(op.Params.SelectMany(p => AssistantNumbers.Numbers(p.Description))).ToList();
            return ((IReadOnlyList<string>)names.Select(n => n.ToLowerInvariant()).ToList(), (IReadOnlyList<double>)figures);
        }).ToList();

    /// <summary>What counts as given besides the request and the editor state: what the app works out from the document's size, and each operation's own figures where it is named.</summary>
    internal static AssistantNumbers.Extra AlsoGivenFigures(string goal, AgentContext context)
    {
        var extra = AssistantNumbers.Worked(context.Width, context.Height, goal);
        extra.Operations = OperationFigures.ToList();
        return extra;
    }

    /// <summary>
    /// Takes out of the plan's text every sentence with a figure the model was not given (<c>AssistantNumbers</c>).
    /// When nothing is left of an answer without steps, Glimmer states what it knows itself.
    /// </summary>
    internal static AgentPlan CheckFigures(AgentPlan plan, string goal, AgentContext context, IReadOnlyList<(string User, string Reply)> history, string? siblingContext)
    {
        var given = GivenFigures(goal, context, history, siblingContext);
        foreach (var step in plan.Steps) given.Add(AgentValue.Object(step.Params).ToString());
        var also = AlsoGivenFigures(goal, context);
        var removed = new List<string>();
        if (plan.Message is { } message)
        {
            var result = AssistantNumbers.Checked(message, given, steps: plan.Steps.Length, extra: also);
            if (result.Removed.Count > 0) plan = plan with { MessageAsWritten = message };
            plan = plan with { Message = result.Text };
            removed.AddRange(result.Removed);
        }
        for (int i = 0; i < plan.Steps.Length; i++)
        {
            if (plan.Steps[i].Rationale is not { } rationale) continue;
            var result = AssistantNumbers.Checked(rationale, given, steps: plan.Steps.Length, extra: also);
            plan = plan with { Steps = plan.Steps.SetItem(i, plan.Steps[i] with { Rationale = result.Text }) };
            removed.AddRange(result.Removed);
        }
        plan = plan with { RemovedFigures = [.. removed] };
        if (plan.Steps.IsEmpty && plan.Message is null && removed.Count > 0) plan = plan with { Message = StateSentence(context) };
        return plan;
    }

}

public sealed class AgentError(string message) : Exception(message)
{
    public static AgentError EmptyResponse => new("The assistant returned an empty answer.");
}

/// <summary>
/// The Assistant's operation register as the agent API and the guide want it (<see cref="IAgentOperationCatalog"/>): what
/// <c>AgentAPI</c> read from <c>AgentPlanner</c>, <c>StepParameters</c> and <c>CanvasSteps</c> in the original.
/// </summary>
public sealed class AgentOperationCatalog : IAgentOperationCatalog
{
    public static AgentOperationCatalog Instance { get; } = new();

    public IReadOnlyList<AgentOperation> Operations => _operations ??= AgentPlanner.Ops.Select(op =>
        new AgentOperation(op.Name, op.Description, AgentPlanner.ParamsSchema(op),
                           op.Params.Select(p => new AgentOperationParam(p.Name, p.Type, p.Description, p.Options)).ToList(),
                           op.IsAIJob, op.StartsJob)).ToList();
    IReadOnlyList<AgentOperation>? _operations;

    public IReadOnlyList<RunnableCommand> RunnableCommands => AgentPlanner.RunnableCommands.Select(d => new RunnableCommand(d.Id.RawValue, d.Title)).ToList();

    public JsonObject RationaleSchema => StepParameters.RationaleSchema;

    public IReadOnlyList<string> FitValues { get; } = Enum.GetValues<CanvasSteps.Fit>().Select(f => f.RawValue()).ToList();

    public string StrayExample { get; } = StepParameters.StrayIn(new AgentStep("crop", ("aspect", "16:9"), ("widht", 800)))?.Message ?? "";

    public string NestedStrayExample { get; } =
        StepParameters.StrayIn(new AgentStep("add_adjustment", ("type", "exposure"), ("params", AgentValue.Object(("brightness", 0.2)))))?.Message ?? "";
}

