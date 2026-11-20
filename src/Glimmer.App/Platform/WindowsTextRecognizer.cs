// Text and faces in pictures on Windows, where the Mac has Vision: Windows.Media.Ocr reads the languages whose OCR packs
// are installed (Settings › Time & language › Language: a language's "Optical character recognition" feature), and
// Windows.Media.FaceAnalysis finds faces. Both run on the device and cost nothing. They are Glimmer.AI's
// IImageTextRecognizer (Copy Text in Image, Image Text to Layers, the smart analysis) and IFaceDetector, set on
// OnDeviceServices at start-up.
using System.Runtime.InteropServices.WindowsRuntime;
using Glimmer.App.Localization;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace Glimmer.App.Platform;

public sealed class WindowsTextRecognizer : IImageTextRecognizer
{
    public IReadOnlyList<string> Languages
    {
        get
        {
            var tags = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            // The user's own languages first, in their order.
            var preferred = Windows.System.UserProfile.GlobalizationPreferences.Languages.ToList();
            int Rank(string tag)
            {
                int i = preferred.FindIndex(p => Same(p, tag));
                return i < 0 ? int.MaxValue : i;
            }
            return [.. tags.OrderBy(Rank)];
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private static OcrEngine Engine(string? language) =>
        (language is not null && OcrEngine.IsLanguageSupported(new Language(language)) ? OcrEngine.TryCreateFromLanguage(new Language(language)) : null)
        ?? OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException(Loc.T(
            "Windows has no text recognition for your languages. Add one under Settings › Time & language › Language & region (Optical character recognition)."));

    /// <summary>IImageTextRecognizer: straight sRGB float RGBA from Glimmer.AI. Windows reports no confidence: 1.</summary>
    public async Task<IReadOnlyList<ImageText.Line>> RecognizeAsync(RGBAImage image, CancellationToken ct = default)
    {
        OcrEngine engine;
        try { engine = Engine(null); }
        catch (InvalidOperationException) { throw new TextRecognitionUnavailableError(); }
        var (pixels, width, height) = Gray.Bgra(image);
        var (lines, _) = await Read(engine, pixels, width, height, ct);
        return [.. lines.Select(l => new ImageText.Line(l.Text, l.Box, 1))];
    }

    private sealed record Word(string Text, CGRectLike Bounds);

    private sealed record Found(string Text, CGRectLike Box, IReadOnlyList<Word> Words);

    /// <summary>OCR has a size limit: a larger picture is read at a reduced size and the boxes are scaled back.</summary>
    private static async Task<(List<Found> Lines, double? Angle)> Read(OcrEngine engine, byte[] bgra, int width, int height, CancellationToken cancellation)
    {
        double scale = Math.Min(1, (double)OcrEngine.MaxImageDimension / Math.Max(width, height));
        var (pixels, w, h) = scale < 1 ? Gray.Downscale(bgra, width, height, scale) : (bgra, width, height);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellation);
        double back = 1 / scale;
        var lines = new List<Found>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word => new Word(word.Text, new CGRectLike(word.BoundingRect.X * back, word.BoundingRect.Y * back,
                (word.BoundingRect.X + word.BoundingRect.Width) * back, (word.BoundingRect.Y + word.BoundingRect.Height) * back))).ToList();
            if (words.Count == 0) continue;
            var box = new CGRectLike(words.Min(x => x.Bounds.MinX), words.Min(x => x.Bounds.MinY), words.Max(x => x.Bounds.MaxX), words.Max(x => x.Bounds.MaxY));
            lines.Add(new Found(line.Text, box, words));
        }
        return (lines, result.TextAngle);
    }
}

/// <summary>Glimmer.AI's IFaceDetector with Windows.Media.FaceAnalysis.FaceDetector, on a Gray8 copy of the picture.</summary>
public sealed class WindowsFaceDetector : IFaceDetector
{
    /// <summary>Faces are found as well at this size as at full size, and much faster.</summary>
    private const int MaxSide = 2048;

    public async Task<IReadOnlyList<CGRectLike>> DetectAsync(RGBAImage image, CancellationToken ct = default)
    {
        if (!FaceDetector.IsSupported) return [];
        var detector = await FaceDetector.CreateAsync().AsTask(ct);
        double scale = Math.Min(1, (double)MaxSide / Math.Max(image.Width, image.Height));
        var (gray, w, h) = Gray.Luminance(image, scale);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(), BitmapPixelFormat.Gray8, w, h);
        var faces = await detector.DetectFacesAsync(bitmap).AsTask(ct);
        double back = 1 / scale;
        return [.. faces.Select(f => new CGRectLike(f.FaceBox.X * back, f.FaceBox.Y * back, (f.FaceBox.X + f.FaceBox.Width) * back, (f.FaceBox.Y + f.FaceBox.Height) * back))];
    }
}

/// <summary>The pixel conversions OCR and face detection need.</summary>
internal static class Gray
{
    /// <summary>Glimmer.AI's straight sRGB floats as BGRA bytes, composited onto white (transparent areas read as paper).</summary>
    public static (byte[] Pixels, int Width, int Height) Bgra(RGBAImage image)
    {
        var p = image.Pixels;
        var dst = new byte[image.Width * image.Height * 4];
        for (int i = 0, n = image.Width * image.Height; i < n; i++)
        {
            float a = Math.Clamp(p[i * 4 + 3], 0, 1);
            for (int c = 0; c < 3; c++) dst[i * 4 + 2 - c] = (byte)Math.Round(255 * (Math.Clamp(p[i * 4 + c], 0, 1) * a + (1 - a)));
            dst[i * 4 + 3] = 255;
        }
        return (dst, image.Width, image.Height);
    }

    /// <summary>Rec. 709 luminance of the straight sRGB floats, box-reduced by `scale` (≤ 1).</summary>
    public static (byte[] Pixels, int Width, int Height) Luminance(RGBAImage image, double scale)
    {
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var dst = new byte[w * h];
        var p = image.Pixels;
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)(y / scale), y1 = Math.Min(image.Height, Math.Max(y0 + 1, (int)((y + 1) / scale)));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x / scale), x1 = Math.Min(image.Width, Math.Max(x0 + 1, (int)((x + 1) / scale)));
                double sum = 0;
                int n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * image.Width + xx) * 4;
                        sum += 0.2126 * p[i] + 0.7152 * p[i + 1] + 0.0722 * p[i + 2];
                        n++;
                    }
                }
                dst[y * w + x] = (byte)Math.Round(255 * Math.Clamp(sum / Math.Max(n, 1), 0, 1));
            }
        }
        return (dst, w, h);
    }

    /// <summary>Box-filtered reduction of unpadded BGRA; text stays legible because OCR's limit is thousands of pixels.</summary>
    public static (byte[] Pixels, int Width, int Height) Downscale(byte[] src, int width, int height, double scale)
    {
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = (int)(y / scale), y1 = Math.Min(height, Math.Max(y0 + 1, (int)((y + 1) / scale)));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x / scale), x1 = Math.Min(width, Math.Max(x0 + 1, (int)((x + 1) / scale)));
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int s = (yy * width + xx) * 4;
                        b += src[s]; g += src[s + 1]; r += src[s + 2]; n++;
                    }
                }
                int d = (y * w + x) * 4;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); dst[d + 3] = 255;
            }
        }
        return (dst, w, h);
    }
}
