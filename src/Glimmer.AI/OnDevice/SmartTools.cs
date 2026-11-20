// Ported from Packages/GlimmerKit/Sources/GlimmerAI/SmartTools.swift
//
// What replaces Vision here:
// - Person segmentation (VNGeneratePersonSegmentationRequest): RT-DETRv2 (Lv et al. 2024, Apache-2.0) finds the people
//   and MODNet (Ke et al., "MODNet: Real-Time Trimap-Free Portrait Matting", AAAI 2022, Apache-2.0) draws their
//   outlines, hair included. MODNet alone mattes whatever looks like a portrait's subject (a cat, a mug: measured), so
//   only what lies in a person's box counts.
// - Text recognition (VNRecognizeTextRequest): the app's IImageTextRecognizer (Windows.Media.Ocr); the Mac's confidence
//   filter and reading order are applied here.
// - Image classification (VNClassifyImageRequest): MobileNetV4 (Qin et al. 2024) trained on ImageNet-1k, through timm,
//   whose 1,000 labels are not Vision's ~1,300; see ImageLabel.
// The colour cast is the original's arithmetic.
using System.Collections.Immutable;
using System.Text.Json;
using Glimmer.Core;

namespace Glimmer.AI;

// MARK: People

public static partial class SubjectSegmentation
{
    /// <summary>A person's score from the detector (sigmoid of its logit) that counts; RT-DETR's usual threshold.</summary>
    internal const float PersonThreshold = 0.5f;

    /// <summary>
    /// A soft mask (0...255) of every person in the image. (Vision's accurate person segmentation on the Mac: hair and
    /// edges are better than the subject mask.)
    /// </summary>
    public static async Task<GrayImage> PeopleMaskAsync(RGBAImage image, ModelStore? store = null, CancellationToken ct = default)
    {
        store ??= ModelStore.Shared;
        // Both models or neither: the question names everything that is missing at once.
        var missing = store.Missing([OnDeviceModel.PersonDetector, OnDeviceModel.PersonMatting]);
        if (missing.Count > 0) throw new ModelMissingError(missing);
        var detector = await OnnxModels.SessionAsync(store, OnDeviceModel.PersonDetector, "onnx", "model_int8.onnx", ct).ConfigureAwait(false);
        var matting = await OnnxModels.SessionAsync(store, OnDeviceModel.PersonMatting, "onnx", "model_fp16.onnx", ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var people = People(detector, image);
            // Nobody found: as Vision's empty mask.
            if (people.Count == 0) throw SegmentationError.NoSubject();
            ct.ThrowIfCancellationRequested();
            int w = image.Width, h = image.Height;
            var alpha = Matte(matting, image);
            // People small in the picture get a matte of their own, from a crop around them at the model's size.
            foreach (var box in people.Where(b => b.Height < h * 0.35).OrderByDescending(b => b.Width * b.Height).Take(8))
            {
                ct.ThrowIfCancellationRequested();
                var crop = Grow(box, 0.25, w, h);
                var part = Matte(matting, image.Cropped(crop));
                for (int y = 0; y < crop.Height; y++)
                    for (int x = 0; x < crop.Width; x++)
                    {
                        int i = (crop.Y + y) * w + crop.X + x;
                        alpha[i] = Math.Max(alpha[i], part[y * crop.Width + x]);
                    }
            }
            // Only what lies in a person's box (widened a little) counts.
            var inside = new bool[w * h];
            foreach (var box in people)
            {
                var r = Grow(box, 0.1, w, h);
                for (int y = r.Y; y < r.Y + r.Height; y++)
                    for (int x = r.X; x < r.X + r.Width; x++) inside[y * w + x] = true;
            }
            var values = new float[w * h];
            for (int i = 0; i < values.Length; i++) values[i] = inside[i] ? Math.Clamp(alpha[i], 0, 1) * 255 : 0;
            // An empty mask means nobody was found.
            if (!values.Any(v => v > 128)) throw SegmentationError.NoSubject();
            return new GrayImage(w, h, values);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The people RT-DETRv2 finds, as boxes in the image's pixels (y down). COCO class 0 is "person".</summary>
    internal static List<CGRectLike> People(Microsoft.ML.OnnxRuntime.InferenceSession detector, RGBAImage image)
    {
        const int side = 640;
        // RT-DETR's processor only rescales to 0…1.
        var input = PixelSampling.Tensor(image, side, side, [0f, 0f, 0f], [1f, 1f, 1f]);
        var outputs = OnnxModels.Run(detector, [OnnxModels.Input.Floats("pixel_values", input, 1, 3, side, side)], "logits", "pred_boxes");
        var (logits, shape) = outputs[0];
        var boxes = outputs[1].Data;
        int queries = (int)shape[1], classes = (int)shape[2];
        var found = new List<CGRectLike>();
        for (int q = 0; q < queries; q++)
        {
            float score = 1 / (1 + MathF.Exp(-logits[q * classes]));
            if (score < PersonThreshold) continue;
            double cx = boxes[q * 4] * image.Width, cy = boxes[q * 4 + 1] * image.Height;
            double bw = boxes[q * 4 + 2] * image.Width, bh = boxes[q * 4 + 3] * image.Height;
            found.Add(new CGRectLike(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2));
        }
        return found;
    }

    /// <summary>MODNet's matte (0…1) of a picture at the picture's size: the shorter side at 512, both multiples of 32.</summary>
    internal static float[] Matte(Microsoft.ML.OnnxRuntime.InferenceSession matting, RGBAImage image)
    {
        double k = 512.0 / Math.Min(image.Width, image.Height);
        int mw = Math.Max(32, (int)Math.Round(image.Width * k / 32) * 32), mh = Math.Max(32, (int)Math.Round(image.Height * k / 32) * 32);
        var input = PixelSampling.Tensor(image, mw, mh, [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f]);
        var (matte, _) = OnnxModels.Run(matting, [OnnxModels.Input.Floats("input", input, 1, 3, mh, mw)], "output")[0];
        return PixelSampling.ResizePlane(matte, mw, mh, image.Width, image.Height);
    }

    /// <summary>A box widened by <paramref name="by"/> of its size on every side, in whole pixels inside the image.</summary>
    private static PixelRect Grow(CGRectLike box, double by, int w, int h)
    {
        double mx = box.Width * by, my = box.Height * by;
        int x0 = Math.Clamp((int)Math.Floor(box.MinX - mx), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(box.MinY - my), 0, h - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(box.MaxX + mx), x0 + 1, w), y1 = Math.Clamp((int)Math.Ceiling(box.MaxY + my), y0 + 1, h);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}

// MARK: Text in the image

public static class ImageText
{
    public sealed record Line(string String, CGRectLike Box, double Confidence)
    {
        // String: the text. Box: pixel rectangle, y down.
    }

    /// <summary>
    /// The text the recognizer can read, line by line, top to bottom. (Vision on the Mac, with its accurate level, its
    /// language correction and automatic language; on Windows, Windows.Media.Ocr through <see cref="IImageTextRecognizer"/>.)
    /// No recognizer: <see cref="TextRecognitionUnavailableError"/>.
    /// </summary>
    public static async Task<IReadOnlyList<Line>> LinesAsync(RGBAImage image, IImageTextRecognizer? recognizer = null, CancellationToken ct = default)
    {
        recognizer ??= OnDeviceServices.TextRecognizer ?? throw new TextRecognitionUnavailableError();
        var found = await recognizer.RecognizeAsync(image, ct).ConfigureAwait(false);
        var lines = found.Where(l => l.Confidence > 0.3).ToList();
        return Sorted(lines);
    }

    /// <summary>Top to bottom; lines whose middles are closer than half a line apart count as one row, read left to right.</summary>
    internal static IReadOnlyList<Line> Sorted(IEnumerable<Line> lines) =>
        lines.Order(Comparer<Line>.Create((a, b) =>
        {
            if (Math.Abs(a.Box.MidY() - b.Box.MidY()) > Math.Min(a.Box.Height, b.Box.Height) / 2) return a.Box.MidY().CompareTo(b.Box.MidY());
            return a.Box.MinX.CompareTo(b.Box.MinX);
        })).ToList();

    /// <summary>The lines as plain text, one per line.</summary>
    public static string PlainText(IReadOnlyList<Line> lines) => string.Join("\n", lines.Select(l => l.String));

    /// <summary>
    /// An editable text layer that covers <paramref name="line"/>: its width matches the original's (the height follows),
    /// its colour is sampled from the image.
    /// </summary>
    public static Layer TextLayer(Line line, RGBAImage image, string fontName = "HelveticaNeue-Medium")
    {
        var box = line.Box;
        var text = new Glimmer.Core.TextLayer(line.String, Vec2.Zero, fontName, Math.Max(box.Height * 0.8, 4), TextColor(image, box));
        double width = text.LayoutSize.Width;
        if (width > 4) text = text with { Size = text.Size * Math.Min(Math.Max(box.Width / width, 0.5), 2) };
        var layout = text.LayoutSize;
        text = text with { Origin = new Vec2(box.MinX - 1, box.MidY() - layout.Height / 2) };
        var name = new System.Globalization.StringInfo(line.String);
        return new Layer(name.LengthInTextElements > 32 ? name.SubstringByTextElements(0, 32) : line.String, new TextContent(text));
    }

    /// <summary>
    /// The text colour inside <paramref name="box"/>: the pixels that differ most from the box's typical (background)
    /// colour. Works for dark-on-light and light-on-dark.
    /// </summary>
    public static SRGBColor TextColor(RGBAImage image, CGRectLike box)
    {
        if (box.Integral().Intersection(new CGRectLike(0, 0, image.Width, image.Height)) is not { } rect
            || rect.Width < 2 || rect.Height < 2)
            return SRGBColor.Black;
        var crop = image.Cropped(new PixelRect((int)rect.MinX, (int)rect.MinY, (int)rect.Width, (int)rect.Height));
        int w = Math.Min(crop.Width, 160), h = Math.Max(1, (int)((double)crop.Height * w / crop.Width));
        var px = PixelSampling.Draw8(crop, w, h);
        var samples = new List<(double L, double R, double G, double B)>();
        for (int i = 0; i < px.Length; i += 4)
        {
            double r = px[i] / 255.0, g = px[i + 1] / 255.0, b = px[i + 2] / 255.0;
            samples.Add((0.2126 * r + 0.7152 * g + 0.0722 * b, r, g, b));
        }
        var sorted = samples.Select(s => s.L).Order().ToList();
        double background = sorted[sorted.Count / 2];
        var far = samples.OrderByDescending(s => Math.Abs(s.L - background)).Take(Math.Max(1, samples.Count / 10)).ToList();
        return new SRGBColor(far.Average(s => s.R), far.Average(s => s.G), far.Average(s => s.B));
    }
}

// MARK: Colour cast

public static partial class SmartAnalysis
{
    /// <summary>
    /// The colour cast of the image's near-neutral pixels (sRGB, per channel, relative to their grey), or null when
    /// there are too few neutrals to judge. Saturated pixels are ignored, so a red car or a blue sky doesn't count as a
    /// cast.
    /// </summary>
    public static Vec3? ColorCast(RGBAImage image)
    {
        int w = Math.Min(image.Width, 256), h = Math.Max(1, (int)((double)image.Height * w / Math.Max(image.Width, 1)));
        var px = PixelSampling.Draw8(image, w, h);
        double sr = 0, sg = 0, sb = 0;
        int n = 0, total = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] <= 8) continue;
            total++;
            double r = px[i] / 255.0, g = px[i + 1] / 255.0, b = px[i + 2] / 255.0;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            double l = (r + g + b) / 3;
            if (!(l > 0.15 && l < 0.95 && mx - mn < 0.25)) continue;
            sr += r - l;
            sg += g - l;
            sb += b - l;
            n++;
        }
        if (!(total > 0 && (double)n / total > 0.05)) return null;
        return new Vec3(sr / n, sg / n, sb / n);
    }

    /// <summary>A cast worth correcting: larger than <paramref name="threshold"/> in any channel.</summary>
    public static bool IsNoticeableCast(Vec3? cast, double threshold = 0.02)
    {
        if (cast is not { } c) return false;
        return Math.Max(Math.Abs(c.X), Math.Max(Math.Abs(c.Y), Math.Abs(c.Z))) > threshold;
    }

    /// <summary>
    /// A Color Balance adjustment that removes <paramref name="cast"/> (most of it: a little warmth or coolness is
    /// often intended).
    /// </summary>
    public static Adjustment CastCorrection(Vec3 cast, double strength = 0.8)
    {
        // The shader adds value × 0.5 × tone weight, and the three tone weights sum to 1.
        var shift = cast * (-2 * strength);
        var numbers = ImmutableDictionary.CreateBuilder<string, double>();
        foreach (var tone in new[] { "shadows", "midtones", "highlights" })
        {
            numbers[$"{tone}R"] = Math.Min(Math.Max(shift.X, -1), 1);
            numbers[$"{tone}G"] = Math.Min(Math.Max(shift.Y, -1), 1);
            numbers[$"{tone}B"] = Math.Min(Math.Max(shift.Z, -1), 1);
        }
        return new Adjustment(AdjustmentKind.ColorBalance)
        {
            Numbers = numbers.ToImmutable(),
            Flags = ImmutableDictionary<string, bool>.Empty.Add("preserveLuminosity", true),
        };
    }
}

// MARK: What's in the picture

public static class ImageLabel
{
    /// <summary>
    /// Vision's labels form a hierarchy and the broad parents ("structure", "outdoor") always score highest; they make
    /// poor names. (ImageNet's labels, which this port's classifier has, are all leaves; the list is kept from the Mac.)
    /// </summary>
    internal static readonly IReadOnlySet<string> TooBroad = new HashSet<string>
    {
        "structure", "outdoor", "indoor", "land", "tableware", "utensil", "liquid", "drink", "material", "textile", "wood_processed",
        "conveyance", "machine", "container", "art", "people", "adult", "sky", "blue_sky", "plant", "vegetation", "water", "water_body",
        "consumer_electronics", "furniture", "clothing", "office_supplies",
    };

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];
    private static readonly object LabelsGate = new();
    private static readonly Dictionary<string, string[]> LabelsByFile = new();

    /// <summary>
    /// A short name for what the image shows ("Coffee mug", "Alp &amp; valley"), or null when the classifier isn't
    /// confident: the best specific label, plus a second one when it is nearly as likely. Null too when the classifier
    /// isn't downloaded: a name is a nicety, so it never asks for the download itself.
    /// The Mac's threshold is 0.2 on Vision's confidences; ImageNet's thousand-way probabilities spread thinner over
    /// near-identical labels, and 0.3 keeps names such as "Wig" for a portrait out (measured on sample pictures).
    /// </summary>
    public static async Task<string?> NameAsync(RGBAImage image, ModelStore? store = null, double minimumConfidence = 0.3, CancellationToken ct = default)
    {
        store ??= ModelStore.Shared;
        if (!store.IsInstalled(OnDeviceModel.ImageLabels)) return null;
        var session = await OnnxModels.SessionAsync(store, OnDeviceModel.ImageLabels, "onnx", "model_fp16.onnx", ct).ConfigureAwait(false);
        // config.json is a package of its own (a single file): Locate gives the file.
        var configPath = await store.LocateAsync("config.json", OnDeviceModel.ImageLabels, ct).ConfigureAwait(false);
        var labels = Labels(configPath);
        var probabilities = await Task.Run(() => Classify(session, image), ct).ConfigureAwait(false);
        var ranked = Enumerable.Range(0, Math.Min(labels.Length, probabilities.Length))
            .Select(i => (Label: labels[i], Confidence: (double)probabilities[i]))
            .Where(c => c.Confidence >= minimumConfidence && !TooBroad.Contains(c.Label))
            .OrderByDescending(c => c.Confidence).ToList();
        return Name(ranked);
    }

    /// <summary>The words of a name from the ranked labels: the best, and the second when it is within 0.1 of it.</summary>
    internal static string? Name(IReadOnlyList<(string Label, double Confidence)> specific)
    {
        if (specific.Count == 0) return null;
        var words = new List<string> { Pretty(specific[0].Label) };
        if (specific.Count > 1 && specific[0].Confidence - specific[1].Confidence < 0.1) words.Add(Pretty(specific[1].Label));
        var pretty = string.Join(" & ", words.Select(w => w.Replace('_', ' ')));
        return pretty.Length == 0 ? null : char.ToUpperInvariant(pretty[0]) + pretty[1..];
    }

    /// <summary>
    /// One ImageNet label as a name: its first synonym, or the second when that one only adds to it ("tabby, tabby cat"
    /// is "tabby cat", "pickup, pickup truck" is "pickup truck"; "valley, vale" is "valley").
    /// </summary>
    internal static string Pretty(string label)
    {
        var synonyms = label.Split(", ");
        if (synonyms.Length > 1 && synonyms[1].Length > synonyms[0].Length
            && synonyms[1].Split(' ').Contains(synonyms[0], StringComparer.OrdinalIgnoreCase))
            return synonyms[1];
        return synonyms[0];
    }

    /// <summary>ImageNet's 1,000 labels, in the model's order, from the model's config.json (<c>id2label</c>).</summary>
    private static string[] Labels(string configPath)
    {
        lock (LabelsGate)
        {
            if (LabelsByFile.TryGetValue(configPath, out var cached)) return cached;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(configPath));
            var map = doc.RootElement.GetProperty("id2label");
            var labels = new string[map.EnumerateObject().Count()];
            foreach (var p in map.EnumerateObject()) labels[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString() ?? "";
            LabelsByFile[configPath] = labels;
            return labels;
        }
    }

    /// <summary>Softmax probabilities: the picture's shorter side at 256, the middle 224 × 224 (timm's evaluation crop).</summary>
    private static float[] Classify(Microsoft.ML.OnnxRuntime.InferenceSession session, RGBAImage image)
    {
        double k = 256.0 / Math.Min(image.Width, image.Height);
        int rw = Math.Max(224, (int)Math.Round(image.Width * k)), rh = Math.Max(224, (int)Math.Round(image.Height * k));
        var resized = PixelSampling.Resized(image, rw, rh).Cropped(new PixelRect((rw - 224) / 2, (rh - 224) / 2, 224, 224));
        var input = PixelSampling.Tensor(resized, 224, 224, Mean, Std);
        var (logits, _) = OnnxModels.Run(session, [OnnxModels.Input.Floats("pixel_values", input, 1, 3, 224, 224)], "logits")[0];
        float max = logits.Max();
        double sum = 0;
        var p = new float[logits.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = MathF.Exp(logits[i] - max);
            sum += p[i];
        }
        for (int i = 0; i < p.Length; i++) p[i] = (float)(p[i] / sum);
        return p;
    }

    /// <summary>
    /// Names a camera or the system gave the file ("IMG_1234", "Screenshot …", "Pasted Image"): worth replacing with
    /// what the image shows.
    /// </summary>
    public static bool IsGeneric(string name)
    {
        var n = name.Trim(' ', '\t').ToLowerInvariant();
        if (n.Length == 0) return true;
        string[] prefixes = ["img", "dsc", "dscf", "dscn", "pxl", "mvimg", "photo", "image", "pasted", "screenshot",
                             "skärmavbild", "skarmavbild", "untitled", "namnlös", "layer", "lager", "inklistrat", "clipboard", "scan"];
        if (prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))) return true;
        int digits = n.Count(char.IsNumber);
        return (double)digits / n.Length > 0.5;
    }
}
