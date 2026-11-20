// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Pipelines.swift
// (OpenRouterImageModel, which needs OpenRouterClient, comes in phase 1.)
using System.Security.Cryptography;
using System.Text;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>An image-generating model: text plus input images in, one image out.</summary>
public interface IImageModel
{
    Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default);
}

public sealed record AIImageResult(RGBAImage Image, double? Cost = null);

/// <summary>
/// OpenRouter-backed image model with an on-disk result cache, so retrying or
/// re-running an identical request doesn't pay twice.
/// </summary>
public sealed class OpenRouterImageModel : IImageModel
{
    /// <summary>
    /// How the picture is asked for. <c>Chat</c> is the route Glimmer has always used (chat
    /// completions with image output), which OpenRouter's guide no longer describes and
    /// serves for a few models only; <c>Images</c> is its documented Image API. <c>Chat</c> stays the
    /// default until the first real call through <c>Images</c> has been made (docs/RELEASE.md).
    /// </summary>
    public enum RouteKind { Chat, Images }

    public OpenRouterClient Client { get; }
    public string Model { get; }
    /// <summary>Inputs are downscaled to this long side before sending.</summary>
    public int MaxInputSide { get; }
    /// <summary>Where results are kept (the app passes %LOCALAPPDATA%\Glimmer\Cache\AIResults); null for no cache.</summary>
    public string? CacheDirectory { get; }
    public RouteKind Route { get; }

    public OpenRouterImageModel(OpenRouterClient client, string model, RouteKind route = RouteKind.Chat, int maxInputSide = 2048, string? cacheDirectory = null)
    {
        Client = client;
        Route = route;
        Model = model;
        MaxInputSide = maxInputSide;
        CacheDirectory = cacheDirectory;
    }

    public async Task<AIImageResult> GenerateAsync(string prompt, IReadOnlyList<RGBAImage> images, int? seed, CancellationToken cancellationToken = default)
    {
        var pngs = images.Select(i => i.Limited(MaxInputSide).PngData()).ToList();
        var cache = CacheDirectory is null ? null : new AIResultCache(CacheDirectory);
        var key = AIResultCache.Key(Model, prompt, pngs, seed);
        if (cache?.Load(key) is { } hit)
        {
            try { return new AIImageResult(RGBAImage.Decode(hit), 0); } catch (InvalidDataException) { /* an unreadable entry is a miss */ }
        }
        var response = Route == RouteKind.Chat
            ? await Client.ImageCompletionAsync(Model, prompt, pngs, seed, cancellationToken).ConfigureAwait(false)
            : await Client.ImageGenerationAsync(Model, prompt, pngs, seed, cancellationToken).ConfigureAwait(false);
        RGBAImage image;
        try { image = RGBAImage.Decode(response.ImageData); }
        catch (InvalidDataException) { throw OpenRouterError.NoImageInResponse("The picture couldn't be read."); }
        cache?.Store(response.ImageData, key);
        return new AIImageResult(image, response.Cost);
    }
}

/// <summary>On-disk result cache, so retrying or re-running an identical request doesn't pay twice.</summary>
public sealed class AIResultCache
{
    // The root is injected (the Mac's default was Caches/AIResults); the app passes %LOCALAPPDATA%\Glimmer\Cache\AIResults.

    readonly string _directory;

    public AIResultCache(string directory) { _directory = directory; }

    public static string Key(string model, string prompt, IReadOnlyList<byte[]> images, int? seed)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(model));
        hasher.AppendData(Encoding.UTF8.GetBytes(prompt));
        foreach (var image in images) hasher.AppendData(SHA256.HashData(image));
        hasher.AppendData(Encoding.UTF8.GetBytes((seed ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    public byte[]? Load(string key)
    {
        try { return File.ReadAllBytes(Path.Combine(_directory, key + ".png")); } catch { return null; }
    }

    public void Store(byte[] data, string key)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            // Atomic, as Swift's .atomic: write beside, then move over.
            var temp = Path.Combine(_directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temp, data);
            File.Move(temp, Path.Combine(_directory, key + ".png"), overwrite: true);
        }
        catch { /* a cache that can't be written is just a cache that misses */ }
    }
}

/// <summary>The web app's AI pipelines, ported (<c>workers/aips/pipelines</c>, <c>tasks</c>).</summary>
public static partial class AIPipelines
{
    // MARK: Generate

    public static Task<AIImageResult> GenerateAsync(string prompt, string? negative, int width, int height, IImageModel model,
                                                    int? seed = null, CancellationToken cancellationToken = default)
    {
        var text = prompt;
        if (!string.IsNullOrEmpty(negative)) text += $"\n\nAvoid: {negative}";
        text += $"\n\nTarget dimensions: {width}x{height} pixels.";
        return model.GenerateAsync(text, [], seed, cancellationToken);
    }

    // MARK: Edit the whole image

    /// <summary>Instruction edit of the flattened image, re-aligned to the source size.</summary>
    public static async Task<AIImageResult> EditAsync(RGBAImage image, string instruction, IImageModel model,
                                                      int? seed = null, CancellationToken cancellationToken = default)
    {
        var result = await model.GenerateAsync(instruction, [image], seed, cancellationToken).ConfigureAwait(false);
        return result with { Image = result.Image.Resized(image.Width, image.Height) };
    }

    // MARK: Inpaint / generative fill

    public enum InpaintMode { Fill, Remove }

    const string MaskPreamble = "You are editing the FIRST image. The SECOND image is a mask: pure white "
        + "marks the ONLY region you may change; black pixels must stay byte-for-byte "
        + "identical.";

    public static string InpaintInstruction(InpaintMode mode, string prompt)
    {
        var @base = MaskPreamble + " Do not recompose, crop, rotate or resize. Keep the framing, "
            + "perspective, lighting, colors and exposure of the unmasked area identical.";
        var p = prompt.Trim();
        if (mode == InpaintMode.Remove)
        {
            return @base + " Task: REMOVE whatever object occupies the white region and "
                + "plausibly reconstruct the background that would be behind it, matching "
                + "surrounding texture, lighting and color."
                + (p.Length == 0 ? "" : $" The user describes what to remove or how to fill in: {p}.");
        }
        return @base + $" Task: replace ONLY the white region with: {(p.Length == 0 ? "content that blends seamlessly with the surroundings" : p)}.";
    }

    public static string ReferenceInstruction(string prompt)
    {
        var @base = MaskPreamble + " The THIRD image is a REFERENCE showing the object to insert. "
            + "Fill ONLY the masked (white) region with the object shown in the reference "
            + "image; match its identity and appearance while adapting its scale, "
            + "perspective, lighting and color to the scene. Do not recompose, crop, "
            + "rotate or resize the FIRST image, and keep every unmasked pixel identical.";
        var p = prompt.Trim();
        return p.Length == 0 ? @base : @base + $" Additional guidance: {p}.";
    }

    public sealed record InpaintResult(
        /// <summary>The new pixels; alpha is the feathered mask so only the edited area shows.</summary>
        RGBAImage Image,
        /// <summary>Where <c>Image</c> goes in the document.</summary>
        (int X, int Y) Origin,
        double? Cost);

    /// <summary>
    /// Fills or removes the selected region of <paramref name="image"/> (the document composite).
    ///
    /// Crops the selection bounds plus 30 % context, sends crop + mask (+ reference),
    /// re-aligns, colour-matches against the untouched pixels and feathers the mask.
    /// </summary>
    public static async Task<InpaintResult> InpaintAsync(RGBAImage image, GrayImage mask, PixelRect selectionBounds,
                                                         InpaintMode mode, string prompt, IImageModel model,
                                                         RGBAImage? reference = null, int? seed = null,
                                                         CancellationToken cancellationToken = default)
    {
        var roi = Imaging.ClampROI(selectionBounds, image.Width, image.Height);
        var crop = image.Cropped(roi);
        var cropMask = new GrayImage(roi.Width, roi.Height, 0f);
        for (int y = 0; y < roi.Height; y++)
            for (int x = 0; x < roi.Width; x++)
                cropMask.Values[y * roi.Width + x] = mask.Values[(roi.Y + y) * mask.Width + roi.X + x];

        AIImageResult result;
        if (reference is not null && mode == InpaintMode.Fill)
            result = await model.GenerateAsync(ReferenceInstruction(prompt), [crop, cropMask.Rgba(), reference], seed, cancellationToken).ConfigureAwait(false);
        else
            result = await model.GenerateAsync(InpaintInstruction(mode, prompt), [crop, cropMask.Rgba()], seed, cancellationToken).ConfigureAwait(false);

        var aligned = result.Image.Resized(roi.Width, roi.Height);
        var matched = Imaging.ColorMatch(aligned, crop, cropMask.Inverted());
        var alpha = Imaging.FeatherMask(cropMask, erode: 2, blur: 3);
        // Only the (feathered) masked region becomes visible on the new layer.
        var o = matched.Clone();
        for (int i = 0; i < roi.Width * roi.Height; i++)
            o.Pixels[i * 4 + 3] = alpha.Values[i] * crop.Pixels[i * 4 + 3] / 255;
        return new InpaintResult(o, (roi.X, roi.Y), result.Cost);
    }

    // MARK: Expand / outpaint

    public readonly record struct Margins
    {
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
        public int Left { get; }

        public Margins(int top, int right, int bottom, int left)
        {
            Top = Math.Max(0, top); Right = Math.Max(0, right); Bottom = Math.Max(0, bottom); Left = Math.Max(0, left);
        }

        /// <summary>Symmetric margins growing the image by <paramref name="factor"/> (1.5 = 50 % larger).</summary>
        public static Margins Scaled(int width, int height, double factor)
        {
            int dx = (int)Math.Round(width * (factor - 1) / 2, MidpointRounding.AwayFromZero);
            int dy = (int)Math.Round(height * (factor - 1) / 2, MidpointRounding.AwayFromZero);
            return new Margins(dy, dx, dy, dx);
        }

        public bool IsEmpty => Top + Right + Bottom + Left == 0;
    }

    public static string OutpaintInstruction(string prompt)
    {
        var @base = "You are extending the FIRST image onto a larger canvas. The SECOND image "
            + "is a mask: white marks the NEW border area you must paint; black marks the "
            + "original photo, which must remain byte-for-byte identical. Continue the "
            + "scene naturally outward — matching perspective, lighting, color and texture "
            + "at the seam. Do not add a frame, border or vignette, and do not alter, "
            + "shift or resize the original (black) region.";
        var p = prompt.Trim();
        return p.Length == 0 ? @base : @base + $" The newly revealed area should depict: {p}.";
    }

    /// <summary>Extends <paramref name="image"/> by <paramref name="m"/>. Returns the full expanded image.</summary>
    public static async Task<AIImageResult> ExpandAsync(RGBAImage image, Margins m, string prompt, IImageModel model,
                                                        int? seed = null, CancellationToken cancellationToken = default)
    {
        int w = image.Width + m.Left + m.Right, h = image.Height + m.Top + m.Bottom;
        var canvas = new RGBAImage(w, h, new System.Numerics.Vector4(128, 128, 128, 255));
        canvas.Paste(image, m.Left, m.Top);
        var mask = new GrayImage(w, h, 255f);
        mask.Paste(new GrayImage(image.Width, image.Height, 0f), m.Left, m.Top);
        var seam = Imaging.FeatherMask(mask, erode: 0, blur: 4);

        var result = await model.GenerateAsync(OutpaintInstruction(prompt), [canvas, mask.Rgba()], seed, cancellationToken).ConfigureAwait(false);
        var aligned = result.Image.Resized(w, h);
        var matched = Imaging.ColorMatch(aligned, canvas, mask.Inverted());
        var composited = Imaging.BlendMasked(canvas, matched, seam);
        var hardInset = new GrayImage(w, h, 0f);
        hardInset.Paste(new GrayImage(image.Width, image.Height, 255f), m.Left, m.Top);
        composited = Imaging.BlendMasked(composited, canvas, Imaging.FeatherMask(hardInset, erode: 4, blur: 2));
        return result with { Image = composited };
    }
}
