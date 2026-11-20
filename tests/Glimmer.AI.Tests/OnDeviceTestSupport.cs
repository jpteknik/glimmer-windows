// New in the port: the pictures the Mac's on-device tests draw with Core Graphics, drawn here with SkiaSharp into an
// 8-bit premultiplied sRGB bitmap as a CGContext would (y down here, so the original's y-up coordinates are flipped), and
// the switches for the opt-in tests that need downloaded models or measure speed.
using System.Numerics;
using Glimmer.AI;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.AI.Tests;

/// <summary>
/// The tests that run an ONNX model use every core while they run; they run on their own, after the rest, so the
/// timing-sensitive tests elsewhere (a sibling polled every 5 ms) aren't starved.
/// </summary>
[CollectionDefinition("On-device models", DisableParallelization = true)]
public sealed class OnDeviceModelsCollection { }

/// <summary>Runs only when GLIMMER_MODELS_DIR names a folder with the downloaded models (laid out as the models folder: <c>&lt;id&gt;/onnx/…</c>).</summary>
public sealed class ModelsFactAttribute : FactAttribute
{
    public ModelsFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLIMMER_MODELS_DIR")))
            Skip = "Needs the downloaded models: set GLIMMER_MODELS_DIR to a folder that holds them.";
    }
}

/// <summary>Reaches Hugging Face: runs only when GLIMMER_DOWNLOAD_MODELS names the folder to download the models into.</summary>
public sealed class DownloadFactAttribute : FactAttribute
{
    public DownloadFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GLIMMER_DOWNLOAD_MODELS")))
            Skip = "Downloads the models from Hugging Face: set GLIMMER_DOWNLOAD_MODELS to a folder to run it.";
    }
}

/// <summary>Speed measurements: GLIMMER_PERF=1 (and GLIMMER_MODELS_DIR for the downloaded models).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_PERF") != "1") Skip = "A speed measurement: set GLIMMER_PERF=1 to run it.";
    }
}

internal static class TestPictures
{
    /// <summary>Draws with SkiaSharp on a w × h canvas (y down) and returns the result as a straight sRGB RGBAImage.</summary>
    public static RGBAImage Draw(int w, int h, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            draw(canvas);
        }
        return RGBAImage.FromSKBitmap(bitmap);
    }

    public static SKPaint Fill(double r, double g, double b, double a = 1) =>
        new() { Color = new SKColor(Byte(r), Byte(g), Byte(b), Byte(a)), IsAntialias = true, Style = SKPaintStyle.Fill };

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    /// <summary>
    /// A landscape: sky over ground, the horizon tilted by <paramref name="tilt"/> degrees (positive = rising to the
    /// right), with a little texture so it looks like a photo.
    /// </summary>
    public static RGBAImage Landscape(int width = 800, int height = 600, double tilt = 0,
                                      (double, double, double)? sky = null, (double, double, double)? ground = null, double gain = 1)
    {
        var s = sky ?? (0.45, 0.65, 0.95);
        var g = ground ?? (0.3, 0.45, 0.2);
        return Draw(width, height, canvas =>
        {
            using (var paint = Fill(g.Item1 * gain, g.Item2 * gain, g.Item3 * gain)) canvas.DrawRect(0, 0, width, height, paint);
            double t = Math.Tan(tilt * Math.PI / 180), mid = height * 0.5, W = width;
            // The original fills the sky polygon in CG's y-up space; flipped here.
            using (var path = new SKPath())
            using (var paint = Fill(s.Item1 * gain, s.Item2 * gain, s.Item3 * gain))
            {
                path.MoveTo(0, (float)(height - (mid - t * W / 2)));
                path.LineTo((float)W, (float)(height - (mid + t * W / 2)));
                path.LineTo((float)W, 0);
                path.LineTo(0, 0);
                path.Close();
                canvas.DrawPath(path, paint);
            }
            uint seed = 7;
            for (int i = 0; i < 4000; i++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                double x = seed % (uint)width;
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                double y = seed % (uint)height;
                double gray = seed % 100 / 100.0;
                using var paint = Fill(gray, gray, gray, 0.15);
                paint.IsAntialias = false;
                canvas.DrawRect((float)x, (float)(height - y - 2), 2, 2, paint);
            }
        });
    }

    /// <summary>A grey scene (several neutral tones) with an optional cast.</summary>
    public static RGBAImage GreyScene(Vec3? cast = null, int width = 300, int height = 200)
    {
        var c = cast ?? new Vec3(0, 0, 0);
        return Draw(width, height, canvas =>
        {
            var tones = new[] { 0.25, 0.4, 0.55, 0.7, 0.85 };
            for (int i = 0; i < tones.Length; i++)
            {
                using var paint = Fill(tones[i] + c.X, tones[i] + c.Y, tones[i] + c.Z);
                paint.IsAntialias = false;
                canvas.DrawRect(i * width / 5, 0, width / 5 + 1, height, paint);
            }
            // A saturated object that must not count as a cast.
            using var red = Fill(0.9, 0.1, 0.1);
            red.IsAntialias = false;
            canvas.DrawRect(20, height - 80, 60, 60, red);
        });
    }

    /// <summary>A textured, photo-like scene with fine detail (the original's formula, pixel for pixel).</summary>
    public static RGBAImage Detailed(int width = 1200, int height = 800)
    {
        var px = new float[width * height * 4];
        uint seed = 11;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int i = (y * width + x) * 4;
                int stripe = ((x / 6 + y / 9) % 2) * 90;
                px[i] = Math.Min(255, 60 + stripe + (int)(seed >> 27));
                px[i + 1] = Math.Min(255, 80 + stripe / 2 + (int)((seed >> 22) & 31));
                px[i + 2] = Math.Min(255, 100 + (x * 100 / width) + (int)((seed >> 17) & 31));
                px[i + 3] = 255;
            }
        }
        return new RGBAImage(width, height, px);
    }

    /// <summary>A Gaussian blur of every channel, edges extended (CIGaussianBlur on a clamped image).</summary>
    public static RGBAImage Blurred(RGBAImage image, double radius)
    {
        int w = image.Width, h = image.Height;
        var o = image.Clone();
        for (int c = 0; c < 3; c++)
        {
            var plane = new float[w * h];
            for (int i = 0; i < plane.Length; i++) plane[i] = image.Pixels[i * 4 + c];
            var blurred = Imaging.Gaussian(plane, w, h, radius);
            for (int i = 0; i < plane.Length; i++) o.Pixels[i * 4 + c] = blurred[i];
        }
        return o;
    }

    /// <summary>A flat graphic: a white circle on blue.</summary>
    public static RGBAImage FlatGraphic() => Draw(400, 300, canvas =>
    {
        using (var blue = Fill(0.2, 0.4, 0.9)) canvas.DrawRect(0, 0, 400, 300, blue);
        using var white = Fill(1, 1, 1);
        canvas.DrawOval(new SKRect(100, 50, 300, 250), white);
    });

    /// <summary>A sheet of paper with lines of text, seen at an angle, on a darker desk.</summary>
    public static (RGBAImage Image, DocumentDetection.Quad Quad) PaperInPerspective()
    {
        const int w = 1200, h = 900;
        var quad = new DocumentDetection.Quad(new Vec2(330, 140), new Vec2(900, 190), new Vec2(960, 800), new Vec2(240, 760));
        var image = Draw(w, h, canvas =>
        {
            using (var desk = Fill(0.35, 0.25, 0.18)) canvas.DrawRect(0, 0, w, h, desk);
            using (var path = new SKPath())
            using (var paper = Fill(0.97, 0.97, 0.95))
            {
                path.MoveTo((float)quad.TopLeft.X, (float)quad.TopLeft.Y);
                foreach (var p in new[] { quad.TopRight, quad.BottomRight, quad.BottomLeft }) path.LineTo((float)p.X, (float)p.Y);
                path.Close();
                canvas.DrawPath(path, paper);
            }
            using var ink = Fill(0.15, 0.15, 0.15);
            for (int row = 0; row < 10; row++)
            {
                double t = (row + 1.5) / 12;
                double ax = quad.TopLeft.X + (quad.BottomLeft.X - quad.TopLeft.X) * t + 60;
                double ay = quad.TopLeft.Y + (quad.BottomLeft.Y - quad.TopLeft.Y) * t;
                canvas.DrawRect((float)ax, (float)(ay - 2), 380, 12, ink);
            }
        });
        return (image, quad);
    }

    /// <summary>
    /// A line of large bold text (the original draws Helvetica-Bold at 72 pt with its baseline 80 px from the bottom),
    /// with the box the text covers.
    /// </summary>
    public static (RGBAImage Image, CGRectLike Box) TextImage(string text, bool light)
    {
        const int w = 900, h = 220;
        using var typeface = SKTypeface.FromFamilyName("Helvetica", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, 72);
        font.MeasureText(text, out var bounds);
        float x = 60, baseline = h - 80;
        var image = Draw(w, h, canvas =>
        {
            double bg = light ? 0.1 : 0.95;
            using (var back = Fill(bg, bg, bg)) canvas.DrawRect(0, 0, w, h, back);
            using var ink = light ? Fill(1, 0.9, 0.2) : Fill(0.05, 0.05, 0.05);
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, ink);
        });
        var box = new CGRectLike(x + bounds.Left - 2, baseline + bounds.Top - 2, x + bounds.Right + 2, baseline + bounds.Bottom + 2);
        return (image, box);
    }

    /// <summary>A red mug on a two-tone background (sky-blue wall, green table).</summary>
    public static RGBAImage Mug() => Draw(640, 480, canvas =>
    {
        using (var wall = Fill(0.75, 0.85, 0.95)) canvas.DrawRect(0, 0, 640, 480, wall);
        using (var table = Fill(0.3, 0.6, 0.3)) canvas.DrawRect(0, 340, 640, 140, table);
        using var mug = Fill(0.85, 0.2, 0.15);
        canvas.DrawRect(240, 150, 160, 210, mug);
    });

    /// <summary>
    /// A 4000 × 3000 (12 MP) photo-like picture for speed measurements: a landscape with fine texture and a red
    /// barrel standing in it (so the subject mask has something to find).
    /// </summary>
    public static RGBAImage TwelveMegapixels()
    {
        const int w = 4000, h = 3000;
        var px = new float[w * h * 4];
        Parallel.For(0, h, y =>
        {
            uint seed = (uint)(y * 7919 + 13);
            for (int x = 0; x < w; x++)
            {
                seed = unchecked(seed * 1_664_525 + 1_013_904_223);
                int i = (y * w + x) * 4;
                bool sky = y < h * 0.45 + (x - w / 2) * 0.05;
                bool barrel = x > 1700 && x < 2300 && y > 1100 && y < 2300;
                float n = (seed >> 24) / 255f * 20 - 10;
                px[i] = Math.Clamp((barrel ? 200 : sky ? 110 : 70) + n + (x * 40f / w), 0, 255);
                px[i + 1] = Math.Clamp((barrel ? 40 : sky ? 160 : 110) + n, 0, 255);
                px[i + 2] = Math.Clamp((barrel ? 30 : sky ? 235 : 60) + n, 0, 255);
                px[i + 3] = 255;
            }
        });
        return new RGBAImage(w, h, px);
    }

    /// <summary>The value at (x, y) of a mask.</summary>
    public static float At(GrayImage mask, int x, int y) => mask.Values[y * mask.Width + x];

    /// <summary>The pixel (r, g, b) at (x, y).</summary>
    public static Vector3 Pixel(RGBAImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return new Vector3(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }
}

/// <summary>A text recognizer that reports what the test says the picture contains (the OCR itself is the app's).</summary>
internal sealed class FakeTextRecognizer : IImageTextRecognizer
{
    private readonly IReadOnlyList<ImageText.Line> lines;
    public int Calls { get; private set; }

    public FakeTextRecognizer(params ImageText.Line[] lines) { this.lines = lines; }

    public Task<IReadOnlyList<ImageText.Line>> RecognizeAsync(RGBAImage image, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(lines);
    }
}

/// <summary>A face detector that reports the faces the test names, in the picture's pixels.</summary>
internal sealed class FakeFaceDetector : IFaceDetector
{
    private readonly IReadOnlyList<CGRectLike> faces;

    public FakeFaceDetector(params CGRectLike[] faces) { this.faces = faces; }

    public Task<IReadOnlyList<CGRectLike>> DetectAsync(RGBAImage image, CancellationToken ct = default) => Task.FromResult(faces);
}
