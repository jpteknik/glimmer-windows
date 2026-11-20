// Ported from Packages/GlimmerKit/Sources/GlimmerAI/StepParameters.swift
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// A step may only carry parameters its operation has. A parameter under a name the
/// operation doesn't know used to be passed over in silence, so that an agent believed it
/// had done something it had not (found with <c>develop_raw</c>, 2026-11-16: "brightness" beside
/// a valid value ran with the valid value alone). The series' rule since: refused by name,
/// before anything is applied.
///
/// Everything here is read from the register that describes each operation (<see cref="AgentPlanner.Ops"/>,
/// and for the nested settings the adjustment's, the filter's and the effect's own lists), so
/// an operation added later is covered without a line here.
/// </summary>
public static class StepParameters
{
    public sealed record Stray(string Name, string? Inside, IReadOnlyList<string> Allowed, string Op, string Subject = "")
    {
        /// <summary>"crop has no parameter named widht. It has: x, y, width, height, toSelection, aspect."</summary>
        public string Message
        {
            get
            {
                string place = Inside is null ? "" : $" in {Inside}";
                string list = Allowed.Count == 0 ? "It takes none." : $"It has: {string.Join(", ", Allowed)}.";
                return $"{Op} has no parameter named {Name}{place}. {list}";
            }
        }

        public bool Equals(Stray? other) =>
            other is not null && Name == other.Name && Inside == other.Inside && Op == other.Op && Subject == other.Subject && Allowed.SequenceEqual(other.Allowed);
        public override int GetHashCode() => HashCode.Combine(Name, Inside, Op);
    }

    /// <summary>Said in a plan's step that carries a parameter its operation doesn't have.</summary>
    public const string StrayNote = "asks for a setting that doesn't exist";

    /// <summary>Said in a plan's step in another app whose settings Glimmer could not check beforehand.</summary>
    public const string UncheckedNote = "Glimmer could not check these settings before running: the other app doesn't say which value chooses them, in";

    /// <summary>Swift's <c>.capitalized</c>: each word's first letter upper case, the rest lower case.</summary>
    static string Capitalized(string s) =>
        string.Join(" ", s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    /// <summary>The first parameter of the step that its operation doesn't have, or null. Unknown operations are someone else's to refuse.</summary>
    public static Stray? StrayIn(AgentStep step)
    {
        if (AgentPlanner.Op(step.Op) is not { } op) return null;
        var allowed = op.Params.Select(p => p.Name).ToList();
        var name = step.Params.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !allowed.Contains(k) && k != "rationale");
        if (name is not null)
            return new Stray(name, null, allowed, step.Op, Capitalized(step.Op.Replace("_", " ")));
        // The nested settings of the three operations that take them by type.
        if (step.Get("params")?.AsObject is not { } nested || NestedKeys(step.Op, step.String("type")) is not { } keys) return null;
        var bad = nested.Keys.Order(StringComparer.Ordinal).FirstOrDefault(k => !keys.Contains(k));
        if (bad is not null)
        {
            string type = step.String("type") ?? "";
            string subject = AdjustmentKind.FromRawValue(type)?.Title() ?? FilterKind.FromRawValue(type)?.Title() ?? type;
            return new Stray(bad, "params", keys, step.Op, subject);
        }
        return null;
    }

    /// <summary>
    /// The setting names an operation's <c>params</c> object takes for this <c>type</c>, or null when the
    /// operation has no such object or the type is not one it knows (refused elsewhere).
    /// </summary>
    public static IReadOnlyList<string>? NestedKeys(string op, string? type)
    {
        if (type is null) return null;
        switch (op)
        {
            case "add_adjustment":
                {
                    if (AdjustmentKind.FromRawValue(type) is not { } kind) return null;
                    return kind.Sliders().Select(s => s.Key).Concat(kind.Toggles().Select(t => t.Key)).Concat(kind.Colors().Select(c => c.Key)).ToList();
                }
            case "apply_filter":
                return FilterKind.FromRawValue(type)?.Fields().Select(f => f.Key).ToList();
            case "add_layer_effect":
                return type switch
                {
                    "dropShadow" or "innerShadow" => EffectKeys(new ShadowEffect()),
                    "outerGlow" => EffectKeys(new GlowEffect()),
                    "stroke" => EffectKeys(new StrokeEffect()),
                    "colorOverlay" => EffectKeys(new ColorOverlayEffect()),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>The keys the effect is stored with; the patch says "enabled" for the stored "isEnabled".</summary>
    static List<string> EffectKeys<T>(T value)
    {
        using var doc = JsonDocument.Parse(SwiftJson.Serialize(value));
        return doc.RootElement.EnumerateObject().Select(p => p.Name == "isEnabled" ? "enabled" : p.Name).Order(StringComparer.Ordinal).ToList();
    }

    // MARK: Published schemas

    static JsonObject Closed(JsonObject properties) =>
        new() { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };

    /// <summary>
    /// The settings an operation's <c>params</c> object takes, per <c>type</c>, as JSON Schema: what the
    /// check above enforces, published so a caller can stop a mistake before it calls
    /// (SIBLINGS.md §3). Null for an operation without such an object.
    /// </summary>
    public static JsonObject? NestedSchemas(string op)
    {
        switch (op)
        {
            case "add_adjustment":
                {
                    var o = new JsonObject();
                    foreach (var kind in AdjustmentKind.AllCases)
                    {
                        var p = new JsonObject();
                        foreach (var s in kind.Sliders()) p[s.Key] = new JsonObject { ["type"] = "number", ["minimum"] = s.Range.LowerBound, ["maximum"] = s.Range.UpperBound, ["default"] = s.DefaultValue };
                        foreach (var t in kind.Toggles()) p[t.Key] = new JsonObject { ["type"] = "boolean", ["default"] = t.DefaultValue };
                        foreach (var c in kind.Colors()) p[c.Key] = new JsonObject { ["type"] = "string", ["description"] = "#RRGGBB" };
                        o[kind.RawValue()] = Closed(p);
                    }
                    return o;
                }
            case "apply_filter":
                {
                    var o = new JsonObject();
                    foreach (var kind in FilterKind.AllCases)
                    {
                        var p = new JsonObject();
                        foreach (var f in kind.Fields())
                            p[f.Key] = f.IsToggle
                                ? new JsonObject { ["type"] = "number", ["enum"] = new JsonArray(0, 1), ["default"] = f.DefaultValue }
                                : new JsonObject { ["type"] = "number", ["minimum"] = f.Range.LowerBound, ["maximum"] = f.Range.UpperBound, ["default"] = f.DefaultValue };
                        o[kind.RawValue()] = Closed(p);
                    }
                    return o;
                }
            case "add_layer_effect":
                {
                    JsonObject Schema<T>(T value)
                    {
                        var p = new JsonObject();
                        using var doc = JsonDocument.Parse(SwiftJson.Serialize(value));
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            string name = prop.Name == "isEnabled" ? "enabled" : prop.Name;
                            p[name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Object => new JsonObject { ["type"] = "string", ["description"] = "#RRGGBB" },
                                JsonValueKind.True or JsonValueKind.False => new JsonObject { ["type"] = "boolean" },
                                JsonValueKind.Number => new JsonObject { ["type"] = "number" },
                                _ => new JsonObject { ["type"] = "string" },
                            };
                        }
                        return Closed(p);
                    }
                    return new JsonObject
                    {
                        ["dropShadow"] = Schema(new ShadowEffect()), ["innerShadow"] = Schema(new ShadowEffect()), ["outerGlow"] = Schema(new GlowEffect()),
                        ["stroke"] = Schema(new StrokeEffect()), ["colorOverlay"] = Schema(new ColorOverlayEffect()),
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>The name every operation offered as a tool accepts and ignores for effect: a model adds it by habit.</summary>
    public static JsonObject RationaleSchema =>
        new() { ["type"] = "string", ["description"] = "Optional. One short sentence: why this step. Accepted everywhere and has no effect." };

    // MARK: Endpoints

    /// <summary>The query parameters each API path takes (by its segments after /api/v1, with <c>*</c> for an id), and nothing else.</summary>
    public static IReadOnlyList<string> QueryParameters(IReadOnlyList<string> path, string method)
    {
        string? first = path.Count > 0 ? path[0] : null, last = path.Count > 0 ? path[^1] : null;
        return (path.Count, first, last, method) switch
        {
            (1, "documents", _, "POST") => ["name"],
            (3, "documents", "image", _) => ["format", "maxSide"],
            (3, "documents", "layers", "POST") => ["fit", "x", "y", "smartObject", "name"],
            (2, "debug", "raw", _) => ["name"],
            _ => [],
        };
    }

    /// <summary>What a JSON body may hold, by what it is for.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BodyParameters = new Dictionary<string, IReadOnlyList<string>>
    {
        ["steps"] = ["steps", "allowPaid", "allowUnknownPrice", "async"],
        ["step"] = ["op", "params", "rationale", "id"],
        ["documents"] = ["name", "image", "width", "height", "background"],
        ["export"] = ["preset", "format", "depth", "flatten", "quality", "maxSide", "maxBytes", "size"],
        ["layers"] = ["fromDocument"],
        ["undo"] = [],
        ["settings.open"] = ["pane"],
    };

    /// <summary>"export has no parameter named qualty. It has: …", or null when every name is known.</summary>
    public static string? StrayMessage(IEnumerable<string> given, IReadOnlyList<string> allowed, string what)
    {
        var name = given.Order(StringComparer.Ordinal).FirstOrDefault(g => !allowed.Contains(g) && g != "rationale");
        if (name is null) return null;
        return $"{what} has no parameter named {name}. " + (allowed.Count == 0 ? "It takes none." : $"It has: {string.Join(", ", allowed)}.");
    }
}
