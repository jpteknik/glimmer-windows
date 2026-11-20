// Ported from Packages/GlimmerKit/Sources/GlimmerAI/MorePipelines.swift
// (FindDistractions, which asks a text model, comes in phase 1 with the service layer.)
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Glimmer.Core;

namespace Glimmer.AI;

public static partial class AIPipelines
{
    // MARK: Relight (workers/aips/tasks/relight.py)

    public enum LightDirection { Left, Right, Top, Bottom, Front, Behind }

    static int Byte255(double v) => (int)Math.Round(v * 255, MidpointRounding.AwayFromZero);

    public static string RelightInstruction(LightDirection direction, SRGBColor color, double intensity, string environment)
    {
        int r = Byte255(color.R), g = Byte255(color.G), b = Byte255(color.B);
        string temp = r - b > 30 ? "warm (golden/amber)" : (b - r > 30 ? "cool (blue/daylight)" : "neutral white");
        string hex = $"#{r:x2}{g:x2}{b:x2}";
        string strength = intensity >= 0.75
            ? "Make the relighting DRAMATIC and high-contrast, with strong highlights and deep, well-defined shadows"
            : intensity >= 0.4
                ? "Make the relighting clearly visible, with balanced highlights and natural, soft-edged shadows"
                : "Make the relighting SUBTLE — a gentle directional shaping with soft, low-contrast shadows";
        var parts = new List<string>
        {
            $"Relight this image. Add a dominant key light coming {direction.Phrase()}, {direction.Shadows()}.",
            $"The light is {temp} light, color hex {hex}; let it set the overall color temperature of highlights and the ambient fill.",
            $"{strength}.",
        };
        var env = environment.Trim();
        if (env.Length > 0)
            parts.Add($"Relight the scene as if the subject were in this environment: \"{env}\". Adapt the background lighting, ambient color and reflections to that environment.");
        parts.Add("CRITICAL: change ONLY the lighting — its direction, color temperature, shadows and highlights. Keep the subject's identity, shapes, pose, materials and the overall composition EXACTLY the same. Do not move, add or remove objects, do not change the framing, and do not crop, rotate or resize the image.");
        return string.Join(" ", parts);
    }

    public static Task<AIImageResult> RelightAsync(RGBAImage image, LightDirection direction, SRGBColor color, double intensity,
                                                   IImageModel model, string environment = "", CancellationToken cancellationToken = default)
    {
        var instruction = RelightInstruction(direction, color, intensity, environment);
        return EditAsync(image, instruction, model, cancellationToken: cancellationToken);
    }

    // MARK: Remove reflections (pipelines/remove_reflections.py)

    public static string ReflectionsInstruction(double strength, bool confined)
    {
        var region = confined ? "Work ONLY on the reflective surface in this crop" : "Work across the whole image";
        var degree = strength >= 0.75
            ? "Remove reflections and glare COMPLETELY and fully reveal whatever is behind the glass/surface, reconstructing the hidden detail plausibly"
            : strength >= 0.4
                ? "Clearly reduce the reflections and glare, revealing most of what is behind the glass/surface while keeping it looking natural"
                : "Gently knock back only the strongest reflection hotspots and glare, keeping the edit subtle";
        return string.Join(" ", new[]
        {
            $"{region}. Remove unwanted reflections, glare and specular hotspots from reflective surfaces — glass, windows, water, screens/monitors, and eyeglass lenses — so the subject behind the glass becomes clearly visible.",
            $"{degree}.",
            "Preserve the surface itself (the frame, glass edges, screen bezel, lens rims) and everything that is genuinely behind or around it; do not invent new objects, text or logos.",
            "CRITICAL: change ONLY the reflections/glare. Keep the framing, perspective, geometry, colors and exposure of everything else EXACTLY the same. Do not recompose, crop, rotate or resize the image.",
        });
    }

    /// <summary>Removes reflections in <paramref name="roi"/> (padded) or the whole image. Returns a layer image and origin.</summary>
    public static async Task<InpaintResult> RemoveReflectionsAsync(RGBAImage image, PixelRect? roi, double strength, IImageModel model,
                                                                   CancellationToken cancellationToken = default)
    {
        var full = new PixelRect(0, 0, image.Width, image.Height);
        var region = roi is { } r ? Imaging.ClampROI(r, image.Width, image.Height) : full;
        bool confined = region != full;
        var crop = image.Cropped(region);
        var result = await model.GenerateAsync(ReflectionsInstruction(strength, confined), [crop], null, cancellationToken).ConfigureAwait(false);
        var aligned = result.Image.Resized(region.Width, region.Height);
        var matched = Imaging.ColorMatch(aligned, crop, new GrayImage(region.Width, region.Height, 255f));
        if (confined)
        {
            var feather = Imaging.FeatherMask(new GrayImage(region.Width, region.Height, 255f), erode: 2, blur: 3);
            matched = matched.WithAlpha(feather);
        }
        return new InpaintResult(matched, (region.X, region.Y), result.Cost);
    }

    // MARK: Harmonize (pipelines/harmonize.py)

    public static string HarmonizeInstruction(double strength)
    {
        var degree = strength >= 0.75 ? "Strongly" : (strength >= 0.4 ? "Noticeably" : "Subtly");
        return $"You are compositing. This image shows a subject that was inserted into a scene and currently looks pasted-in. {degree} relight and color-grade the inserted subject so it matches the scene: align its lighting direction, color temperature, contrast and exposure to the surrounding environment, and add a plausible soft contact shadow where the subject meets the ground/surface. Keep the subject's identity, shape and pose exactly. Do NOT change the background, its framing, perspective or content, and do not crop, rotate or resize the image.";
    }

    /// <summary>Re-grades <paramref name="foreground"/> (RGBA, same size as <paramref name="background"/>) to sit in the scene. Returns the subject with its original alpha.</summary>
    public static async Task<AIImageResult> HarmonizeAsync(RGBAImage foreground, RGBAImage background, double strength, IImageModel model,
                                                           CancellationToken cancellationToken = default)
    {
        var composite = background.Clone();
        for (int i = 0; i < background.Width * background.Height; i++)
        {
            float a = foreground.Pixels[i * 4 + 3] / 255;
            for (int c = 0; c < 3; c++)
                composite.Pixels[i * 4 + c] = foreground.Pixels[i * 4 + c] * a + background.Pixels[i * 4 + c] * (1 - a);
            composite.Pixels[i * 4 + 3] = 255;
        }
        var result = await model.GenerateAsync(HarmonizeInstruction(strength), [composite], null, cancellationToken).ConfigureAwait(false);
        var aligned = result.Image.Resized(background.Width, background.Height);
        float k = (float)Math.Min(Math.Max(strength, 0), 1);
        var o = foreground.Clone();
        for (int i = 0; i < foreground.Width * foreground.Height; i++)
            for (int c = 0; c < 3; c++)
                o.Pixels[i * 4 + c] = foreground.Pixels[i * 4 + c] * (1 - k) + aligned.Pixels[i * 4 + c] * k;
        return result with { Image = o };  // alpha stays the subject's own
    }

    // MARK: Upscale enhance (tasks/upscale.py)

    public static string EnhanceInstruction(double creativity)
    {
        var detail = creativity >= 0.66
            ? "add rich, realistic fine detail and texture, reconstructing plausible micro-structure where the upscale is soft"
            : creativity >= 0.33
                ? "add realistic fine detail and texture, and noticeably sharpen edges"
                : "subtly sharpen textures and edges and remove residual blur";
        return $"Enhance this image: {detail}. Keep the composition, framing, subject shapes and colors IDENTICAL — do not move, add or remove objects, do not recolor, do not crop, rotate or resize. This is a detail/sharpness pass only.";
    }

    /// <summary>Tile layout for the enhance pass (1024 px tiles, 96 px overlap).</summary>
    public static IReadOnlyList<PixelRect> EnhanceTiles(int width, int height, int tile = 1024, int overlap = 96)
    {
        static List<int> Starts(int length, int tile, int overlap)
        {
            if (length <= tile) return [0];
            var s = new List<int>();
            int p = 0;
            while (p + tile < length) { s.Add(p); p += tile - overlap; }
            s.Add(length - tile);
            return s;
        }
        var o = new List<PixelRect>();
        foreach (int y in Starts(height, tile, overlap))
            foreach (int x in Starts(width, tile, overlap))
                o.Add(new PixelRect(x, y, Math.Min(tile, width), Math.Min(tile, height)));
        return o;
    }

    /// <summary>
    /// AI detail pass over an already upscaled image: tiled, colour-matched to the
    /// base, and blended with feathered seams (the web app pasted hard edges).
    /// </summary>
    public static async Task<AIImageResult> EnhanceAsync(RGBAImage @base, double creativity, IImageModel model,
                                                         Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var tiles = EnhanceTiles(@base.Width, @base.Height);
        var o = @base.Clone();
        double cost = 0;
        var instruction = EnhanceInstruction(creativity);
        for (int n = 0; n < tiles.Count; n++)
        {
            var rect = tiles[n];
            cancellationToken.ThrowIfCancellationRequested();
            var crop = @base.Cropped(rect);
            var result = await model.GenerateAsync(instruction, [crop], null, cancellationToken).ConfigureAwait(false);
            cost += result.Cost ?? 0;
            var aligned = result.Image.Resized(rect.Width, rect.Height);
            var matched = Imaging.ColorMatch(aligned, crop, new GrayImage(rect.Width, rect.Height, 255f));
            // Feather 48 px on edges shared with other tiles.
            var alpha = new GrayImage(rect.Width, rect.Height, 255f);
            const int ramp = 48;
            for (int y = 0; y < rect.Height; y++)
            {
                for (int x = 0; x < rect.Width; x++)
                {
                    float a = 1;
                    if (rect.X > 0) a = Math.Min(a, (float)x / ramp);
                    if (rect.Y > 0) a = Math.Min(a, (float)y / ramp);
                    if (rect.MaxX < @base.Width) a = Math.Min(a, (float)(rect.Width - 1 - x) / ramp);
                    if (rect.MaxY < @base.Height) a = Math.Min(a, (float)(rect.Height - 1 - y) / ramp);
                    alpha.Values[y * rect.Width + x] = Math.Min(Math.Max(a, 0f), 1f) * 255;
                }
            }
            var region = o.Cropped(rect);
            o.Paste(Imaging.BlendMasked(region, matched, alpha), rect.X, rect.Y);
            progress?.Invoke((double)(n + 1) / tiles.Count);
        }
        return new AIImageResult(o, cost);
    }

    // MARK: Distractions (api/src/distractions)

    /// <summary>Normalized 0...1, top-left origin.</summary>
    public readonly record struct DistractionBox(double X, double Y, double Width, double Height);

    public sealed record Distraction(string Id, string Label, string? Rationale, string Severity, DistractionBox Box) : IEquatable<Distraction>
    {
        // Equatable by id only, as in Swift.
        public bool Equals(Distraction? other) => other is not null && other.Id == Id;
        public override int GetHashCode() => Id.GetHashCode();

        public PixelRect PixelRect(int width, int height) =>
            new((int)(Box.X * width), (int)(Box.Y * height),
                Math.Max(1, (int)Math.Round(Box.Width * width, MidpointRounding.AwayFromZero)),
                Math.Max(1, (int)Math.Round(Box.Height * height, MidpointRounding.AwayFromZero)));
    }

    public const string DistractionSystemPrompt = """
        You are a photo-retouching assistant for an AI image editor. Given a single photograph, identify DISTRACTING elements that a professional retoucher would typically remove to improve the image.
        Look specifically for things like:
        - photobombers and random bystanders who are not the subject
        - litter, trash, clutter and other unwanted small objects
        - stray signs, billboards, posters, logos or watermarks
        - power lines, cables, poles and similar background clutter
        - lens dust, sensor spots, smudges and blemishes on the image itself
        - harsh blown-out highlights / specular hotspots that draw the eye
        - distracting reflections or glare on glass/screens (only if clearly unwanted)
        Do NOT flag the main subject, intentional compositional elements, or normal scene content. Only flag things whose removal would plausibly improve the photo.
        For each element return a bounding box in NORMALIZED image coordinates: x, y, width and height are fractions between 0 and 1 of the image's width/height, with the origin at the TOP-LEFT corner. Be generous enough that the box fully contains the element; the user will fine-tune the selection before anything is removed, so approximate boxes are fine, but they must stay within [0,1].
        OUTPUT FORMAT — return ONLY a single JSON object, no markdown, no prose around it:
        {
          "distractions": [
            {
              "id": "d1",
              "label": "<short label, e.g. 'photobomber', 'power line', 'litter'>",
              "rationale": "<optional one short sentence on why it's distracting>",
              "severity": "low" | "medium" | "high",
              "box": { "x": 0.0, "y": 0.0, "width": 0.0, "height": 0.0 }
            }
          ],
          "message": "<optional note; if nothing distracting is found, return an empty distractions array and a short message>"
        }
        If the image has no distracting elements, return {"distractions": [], "message": "No obvious distractions found."}.
        """;

    /// <summary>Extracts the JSON object from a model reply (tolerates fences and prose).</summary>
    internal static JsonElement? JsonObjectIn(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.ValueKind == JsonValueKind.Object) return d.RootElement.Clone();
        }
        catch (JsonException) { }
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end < 0 || start >= end) return null;
        try
        {
            using var d = JsonDocument.Parse(text[start..(end + 1)]);
            return d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<(IReadOnlyList<Distraction> Items, string? Message, double? Cost)> FindDistractionsAsync(RGBAImage image, ITextModel model,
                                                                                                                  CancellationToken cancellationToken = default)
    {
        var png = image.Limited(1536).PngData();
        var reply = await model.ReplyAsync(DistractionSystemPrompt, "Analyze this image and return the JSON object of distracting elements now.",
                                           [png], "image/png", json: true, temperature: 0.2, cancellationToken).ConfigureAwait(false);
        var parsed = ParseDistractions(reply.Text);
        return (parsed.Items, parsed.Message, reply.Cost);
    }

    /// <summary>Parses and sanitizes the analyzer reply (clamps boxes to [0,1], drops empty ones).</summary>
    public static (IReadOnlyList<Distraction> Items, string? Message) ParseDistractions(string text)
    {
        if (JsonObjectIn(text) is not { } json) return ([], "The model's reply couldn't be read.");
        // `as? [[String: Any]]`: all or nothing, one element that is not an object makes the whole list empty.
        var raw = json.TryGetProperty("distractions", out var arr) && arr.ValueKind == JsonValueKind.Array
                  && arr.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object)
            ? arr.EnumerateArray().ToList() : [];
        var items = new List<Distraction>();
        bool leftOut = false;
        for (int i = 0; i < raw.Count; i++)
        {
            var d = raw[i];
            if (!(d.TryGetProperty("box", out var b) && b.ValueKind == JsonValueKind.Object)) continue;
            double N(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            double x = Math.Min(Math.Max(N("x"), 0), 1), y = Math.Min(Math.Max(N("y"), 0), 1);
            double w = Math.Min(Math.Max(N("width"), 0), 1 - x), h = Math.Min(Math.Max(N("height"), 0), 1 - y);
            if (!(w > 0.001 && h > 0.001)) continue;
            // The reason is the model's own text: a sentence with a figure nobody gave it goes (`AssistantNumbers`).
            string? rationale = null;
            if (d.TryGetProperty("rationale", out var rv) && rv.ValueKind == JsonValueKind.String)
            {
                var reason = AssistantNumbers.Checked(rv.GetString()!, given: []);
                rationale = reason.Text;
                if (reason.Removed.Count > 0) leftOut = true;
            }
            string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            items.Add(new Distraction(S("id") ?? $"d{i + 1}", S("label") ?? "distraction", rationale, S("severity") ?? "medium", new DistractionBox(x, y, w, h)));
        }
        // How many were found is Glimmer's own count, so the summary may say it.
        AssistantNumbers.CheckedText? said = null;
        if (json.TryGetProperty("message", out var mv) && mv.ValueKind == JsonValueKind.String)
            said = AssistantNumbers.Checked(mv.GetString()!, given: [$"{items.Count} {raw.Count}"]);
        if (said is { Removed.Count: > 0 }) leftOut = true;
        return (items, AssistantNumbers.Noted(said?.Text, removed: leftOut));
    }
}

public static class LightDirections
{
    public static IReadOnlyList<AIPipelines.LightDirection> AllCases { get; } = Enum.GetValues<AIPipelines.LightDirection>();

    /// <summary>The raw value: left, right, top, bottom, front, behind.</summary>
    public static string RawValue(this AIPipelines.LightDirection d) => d.ToString().ToLowerInvariant();

    /// <summary>Swift's <c>rawValue.capitalized</c>.</summary>
    public static string Title(this AIPipelines.LightDirection d) => d.ToString();

    internal static string Phrase(this AIPipelines.LightDirection d) => d switch
    {
        AIPipelines.LightDirection.Left => "from the LEFT side of the frame (a side/key light entering from the left)",
        AIPipelines.LightDirection.Right => "from the RIGHT side of the frame (a side/key light entering from the right)",
        AIPipelines.LightDirection.Top => "from directly ABOVE (overhead/top light)",
        AIPipelines.LightDirection.Bottom => "from BELOW (an uplight / footlight pointing upward)",
        AIPipelines.LightDirection.Front => "from the FRONT, on the camera axis (flat frontal light)",
        _ => "from BEHIND the subject (backlight / rim light that haloes the edges)",
    };

    internal static string Shadows(this AIPipelines.LightDirection d) => d switch
    {
        AIPipelines.LightDirection.Left => "casting soft shadows toward the right",
        AIPipelines.LightDirection.Right => "casting soft shadows toward the left",
        AIPipelines.LightDirection.Top => "casting soft shadows downward, under forms",
        AIPipelines.LightDirection.Bottom => "throwing shadows upward in an eerie, dramatic way",
        AIPipelines.LightDirection.Front => "minimizing cast shadows, filling the form evenly",
        _ => "leaving the camera-facing side in relative shadow with a bright rim",
    };
}
