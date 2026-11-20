// Ported from Packages/GlimmerKit/Sources/GlimmerAI/CanvasSteps.swift
using System.Globalization;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// The parts of <c>crop</c>, <c>resize_image</c>, <c>select_rect</c>, <c>set_layer</c> and <c>add_image</c> that don't
/// need the editor: what the request means, and why it is refused.
/// </summary>
public static class CanvasSteps
{
    public sealed class Refusal(string reason, string message) : Exception(message), IEquatable<Refusal>
    {
        public string Reason { get; } = reason;

        public bool Equals(Refusal? other) => other is not null && other.Reason == Reason && other.Message == Message;
        public override bool Equals(object? obj) => Equals(obj as Refusal);
        public override int GetHashCode() => HashCode.Combine(Reason, Message);
    }

    /// <summary>The largest side of a document, as for a new document.</summary>
    public const int MaxSide = 30_000;

    /// <summary>A request that contradicts itself (answered 400, before any step runs), or null.</summary>
    public static string? Malformed(AgentStep step)
    {
        bool Has(string key) => step.Get(key) is { } v && v != AgentValue.Null;
        switch (step.Op)
        {
            case "crop":
                {
                    bool rect = new[] { "x", "y", "width", "height" }.Any(Has);
                    int forms = new[] { rect, step.Get("toSelection")?.AsBool == true, Has("aspect") }.Count(f => f);
                    if (forms > 1) return "crop takes one of: x/y/width/height, toSelection, or aspect.";
                    if (forms == 0) return "crop needs x/y/width/height, toSelection: true, or aspect.";
                    if (rect && !(Has("width") && Has("height"))) return "crop needs both width and height.";
                    break;
                }
            case "resize_image":
                {
                    bool size = Has("width") || Has("height");
                    if (size && Has("scale")) return "resize_image takes width/height or scale, not both.";
                    if (!size && !Has("scale")) return "resize_image needs width, height or scale.";
                    break;
                }
            case "resize_canvas":
                {
                    bool size = Has("width") || Has("height");
                    if (size && Has("aspect")) return "resize_canvas takes width/height or aspect, not both.";
                    if (!size && !Has("aspect")) return "resize_canvas needs width, height or aspect.";
                    break;
                }
            case "select_rect":
                if (!(Has("width") && Has("height"))) return "select_rect needs x, y, width and height.";
                break;
        }
        return null;
    }

    /// <summary>"16:9" → 16 / 9. Both numbers must be positive.</summary>
    public static double? Aspect(string text)
    {
        var parts = text.Split(':').Select(p => AgentValue.SwiftDouble(p.Trim(' ', '\t'))).ToArray();
        if (parts.Length != 2 || parts[0] is not { } w || parts[1] is not { } h) return null;
        if (!(w > 0 && h > 0 && double.IsFinite(w) && double.IsFinite(h))) return null;
        return w / h;
    }

    static double RoundAway(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>The step's rectangle clipped to the canvas, or <c>bad_rect</c>.</summary>
    public static PixelRect Rect(AgentStep step, PixelRect canvas)
    {
        double x = RoundAway(step.Number("x") ?? 0), y = RoundAway(step.Number("y") ?? 0);
        double w = RoundAway(step.Number("width") ?? 0), h = RoundAway(step.Number("height") ?? 0);
        if (!new[] { x, y, w, h }.All(v => double.IsFinite(v) && Math.Abs(v) < 1e9) || !(w >= 1) || !(h >= 1))
            throw new Refusal("bad_rect", "width and height must be at least 1 pixel.");
        var r = new PixelRect((int)x, (int)y, (int)w, (int)h).Intersection(canvas);
        if (r.IsEmpty)
            throw new Refusal("bad_rect", $"The rectangle is outside the canvas ({canvas.Width} × {canvas.Height}).");
        return r;
    }

    /// <summary>The box <c>crop</c> cuts to. <paramref name="selection"/> is the bounds of the selection, if there is one.</summary>
    public static PixelRect CropBox(AgentStep step, PixelRect canvas, PixelRect? selection)
    {
        if (step.Get("toSelection")?.AsBool == true)
        {
            if (selection?.Intersection(canvas) is not { } s || s.IsEmpty)
                throw new Refusal("no_selection", "There's no selection to crop to.");
            return s;
        }
        if (step.String("aspect") is { } text)
        {
            if (Aspect(text) is not { } a)
                throw new Refusal("bad_aspect", "aspect must be W:H with positive numbers, like 16:9.");
            // The largest centred box of that shape.
            int w = canvas.Width, h = (int)RoundAway(canvas.Width / a);
            if (h > canvas.Height)
            {
                h = canvas.Height;
                w = (int)RoundAway(canvas.Height * a);
            }
            w = Math.Min(Math.Max(w, 1), canvas.Width);
            h = Math.Min(Math.Max(h, 1), canvas.Height);
            return new PixelRect(canvas.X + (canvas.Width - w) / 2, canvas.Y + (canvas.Height - h) / 2, w, h);
        }
        return Rect(step, canvas);
    }

    /// <summary>
    /// The size <c>resize_image</c> gives a <paramref name="width"/> × <paramref name="height"/> image. One of width and height keeps
    /// the proportions; both give exactly that size.
    /// </summary>
    public static (int Width, int Height) NewSize(AgentStep step, int width, int height)
    {
        double w = width, h = height;
        if (step.Number("scale") is { } scale)
        {
            w *= scale;
            h *= scale;
        }
        else
        {
            switch (step.Number("width"), step.Number("height"))
            {
                case ({ } nw, { } nh): (w, h) = (nw, nh); break;
                case ({ } nw, null): (w, h) = (nw, h * nw / w); break;
                case (null, { } nh): (w, h) = (w * nh / h, nh); break;
            }
        }
        if (!(double.IsFinite(w) && double.IsFinite(h) && RoundAway(w) >= 1 && RoundAway(h) >= 1 && RoundAway(w) <= MaxSide && RoundAway(h) <= MaxSide))
            throw new Refusal("bad_size", $"The new size must be 1…{MaxSide} pixels on each side.");
        if (DocumentLimits.Refusal((int)RoundAway(w), (int)RoundAway(h)) is { } tooLarge)
            throw new Refusal("too_large", tooLarge);
        return ((int)RoundAway(w), (int)RoundAway(h));
    }

    /// <summary>What <c>set_layer</c> changes.</summary>
    public readonly record struct LayerChange(double? Opacity = null, BlendMode? BlendMode = null, bool? Visible = null, string? Name = null);

    public static LayerChange LayerChangeOf(AgentStep step, bool isGroup)
    {
        var c = new LayerChange();
        if (step.Number("opacity") is { } o)
        {
            if (!(o >= 0 && o <= 1)) throw new Refusal("bad_opacity", "opacity must be 0…1.");
            c = c with { Opacity = o };
        }
        if (step.String("blendMode") is { } id)
        {
            if (BlendModeExtensions.Parse(id) is not { } mode || (mode == BlendMode.PassThrough && !isGroup))
            {
                var ids = string.Join(", ", BlendMode.AllCases.Where(m => m != BlendMode.PassThrough || isGroup).Select(m => m.RawValue()));
                throw new Refusal("unknown_blend_mode", $"blendMode must be one of: {ids}.");
            }
            c = c with { BlendMode = mode };
        }
        c = c with { Visible = step.Get("visible")?.AsBool };
        // Swift .whitespacesAndNewlines
        if (step.String("name")?.Trim() is { Length: > 0 } name)
        {
            c = c with { Name = StringPrefix(name, 200) };
        }
        if (c == new LayerChange())
            throw new Refusal("nothing_to_set", "Give opacity, blendMode, visible or name.");
        return c;
    }

    /// <summary>Swift's <c>String.prefix(n)</c>: the first n characters (grapheme clusters).</summary>
    static string StringPrefix(string s, int n)
    {
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(s);
        return starts.Length <= n ? s : s[..starts[n]];
    }

    static string UuidString(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>The layer a <paramref name="key"/> means: its id, or its name when exactly one layer has it. <paramref name="layers"/> is every layer, in any order.</summary>
    public static Guid Target(string key, IReadOnlyList<(Guid Id, string Name)> layers)
    {
        key = key.Trim(' ', '\t');
        foreach (var l in layers)
            if (string.Equals(UuidString(l.Id), key, StringComparison.OrdinalIgnoreCase)) return l.Id;
        var named = layers.Where(l => string.Equals(l.Name, key, StringComparison.InvariantCultureIgnoreCase)).ToList();
        if (named.Count > 1)
            throw new Refusal("ambiguous_target",
                $"{named.Count} layers are named “{key}”. Use an id: {string.Join(", ", named.Select(l => UuidString(l.Id)))}.");
        if (named.Count == 0) throw new Refusal("unknown_layer", $"No layer named “{key}”.");
        return named[0].Id;
    }

    /// <summary>
    /// The box Canvas Size cuts or extends the canvas to, in the old canvas's pixels. <paramref name="anchor"/>
    /// says which part of the image stays put: 0, 1 or 2 for left/centre/right and top/middle/bottom.
    /// </summary>
    public static PixelRect CanvasBox((int Width, int Height) old, (int Width, int Height) @new, (int X, int Y) anchor)
    {
        static int Origin(int old, int @new, int a) => a switch
        {
            0 => 0,
            2 => old - @new,
            _ => (int)Math.Floor((old - @new) / 2.0),
        };
        return new PixelRect(Origin(old.Width, @new.Width, anchor.X), Origin(old.Height, @new.Height, anchor.Y), @new.Width, @new.Height);
    }

    static readonly Dictionary<string, (int X, int Y)> Anchors = new()
    {
        ["top-left"] = (0, 0), ["top"] = (1, 0), ["top-right"] = (2, 0), ["left"] = (0, 1), ["center"] = (1, 1), ["right"] = (2, 1),
        ["bottom-left"] = (0, 2), ["bottom"] = (1, 2), ["bottom-right"] = (2, 2),
    };

    public static readonly IReadOnlyList<string> AnchorNames =
        ["top-left", "top", "top-right", "left", "center", "right", "bottom-left", "bottom", "bottom-right"];

    /// <summary>
    /// The box <c>resize_canvas</c> gives the canvas, in the old canvas's pixels. With <c>aspect</c>: the
    /// smallest canvas of that shape that holds the current one.
    /// </summary>
    public static PixelRect CanvasBox(AgentStep step, int width, int height)
    {
        if (!Anchors.TryGetValue(step.String("anchor") ?? "center", out var anchor))
            throw new Refusal("bad_anchor", $"anchor must be one of: {string.Join(", ", AnchorNames)}.");
        double w = width, h = height;
        if (step.String("aspect") is { } text)
        {
            if (Aspect(text) is not { } a)
                throw new Refusal("bad_aspect", "aspect must be W:H with positive numbers, like 16:9.");
            if (a > w / h) w = Math.Ceiling(h * a); else h = Math.Ceiling(w / a);
        }
        else
        {
            w = step.Number("width") ?? w;
            h = step.Number("height") ?? h;
        }
        if (!(double.IsFinite(w) && double.IsFinite(h) && RoundAway(w) >= 1 && RoundAway(h) >= 1 && RoundAway(w) <= MaxSide && RoundAway(h) <= MaxSide))
            throw new Refusal("bad_size", $"The canvas must be 1…{MaxSide} pixels on each side.");
        if (DocumentLimits.Refusal((int)RoundAway(w), (int)RoundAway(h)) is { } tooLarge)
            throw new Refusal("too_large", tooLarge);
        return CanvasBox((width, height), ((int)RoundAway(w), (int)RoundAway(h)), anchor);
    }

    /// <summary>How <c>add_image</c> fits an image into the canvas.</summary>
    public enum Fit { None, Contain, Cover }

    public static string RawValue(this Fit fit) => fit switch { Fit.None => "none", Fit.Contain => "contain", _ => "cover" };
    public static Fit? FitFromRaw(string raw) => raw switch { "none" => Fit.None, "contain" => Fit.Contain, "cover" => Fit.Cover, _ => null };

    /// <summary>
    /// Where an added image of <paramref name="image"/> size lands: its size after fitting, and its top-left.
    /// <c>contain</c> only scales down; <c>cover</c> scales either way until the canvas is covered.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Placement((int Width, int Height) image, (int Width, int Height) canvas,
                                                                              Fit fit, double? x, double? y)
    {
        double iw = image.Width, ih = image.Height, cw = canvas.Width, ch = canvas.Height;
        double scale = fit switch
        {
            Fit.None => 1,
            Fit.Contain => Math.Min(1, Math.Min(cw / iw, ch / ih)),
            _ => Math.Max(cw / iw, ch / ih),
        };
        double w = iw * scale, h = ih * scale;
        return (x ?? RoundAway((cw - w) / 2), y ?? RoundAway((ch - h) / 2), w, h);
    }
}
