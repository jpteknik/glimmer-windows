// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/GoldenImageTests.swift (the scene, the comparison and the
// reference files; the scene is drawn with SkiaSharp the way CoreGraphics draws it on the Mac)
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Render.Tests;

internal static class GoldenHarness
{
    public const int Size = 96;

    /// <summary>The checked-in references, next to this file.</summary>
    public static readonly string Directory = Path.Combine(Path.GetDirectoryName(ThisFile())!, "Golden");

    private static string ThisFile([CallerFilePath] string path = "") => path;

    // MARK: Scene

    public static (double r, double g, double b) Hsv(double h, double s, double v)
    {
        int i = (int)(h * 6) % 6;
        double f = h * 6 - (int)(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private static SKBitmap NewBitmap() =>
        new(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));

    /// <summary>
    /// A hue sweep across, darker towards the bottom, with a grey ramp strip (rows 84…95: CoreGraphics' y-up rect
    /// (x, 0, 1, 12) is the bottom of the image).
    /// </summary>
    public static SKBitmap BaseBitmap()
    {
        var bitmap = NewBitmap();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = false, BlendMode = SKBlendMode.Src };
        for (int x = 0; x < Size; x++)
        {
            for (int y = 0; y < Size; y++)
            {
                var (r, g, b) = Hsv((double)x / Size, 0.8, 1 - (double)y / Size * 0.7);
                paint.Color = Color(r, g, b, 1);
                canvas.DrawRect(new SKRect(x, y, x + 1, y + 1), paint);
            }
        }
        for (int x = 0; x < Size; x++)
        {
            double v = (double)x / (Size - 1);
            paint.Color = Color(v, v, v, 1);
            canvas.DrawRect(new SKRect(x, Size - 12, x + 1, Size), paint);
        }
        return bitmap;
    }

    /// <summary>
    /// 8-bit colour from components as CoreGraphics quantizes a fill colour for an 8-bit context: in float, rounded half
    /// up. Measured on blend-normal.png: components the hue sweep computes as 76.4999… (Double) × 255 come out 77, which
    /// only float arithmetic gives.
    /// </summary>
    private static SKColor Color(double r, double g, double b, double a)
    {
        static byte Q(double c) => (byte)MathF.Floor((float)c * 255f + 0.5f);
        return new SKColor(Q(r), Q(g), Q(b), Q(a));
    }

    /// <summary>
    /// A soft-edged orange disc with a cyan square, on transparency. CoreGraphics' y-up rects (18, 30, 50, 50) and
    /// (50, 20, 30, 30) are (18, 16)…(68, 66) and (50, 46)…(80, 76) y-down.
    /// </summary>
    /// <remarks>
    /// CoreGraphics anti-aliases <c>fillEllipse</c> as an inscribed polygon of about 64 sides (fitted to
    /// blend-normal.png's edge pixels: mean coverage error 0.0047 against 0.015 for the exact disc); Skia's own oval is
    /// coarser and fails 33 of the 59 references. The disc is therefore drawn as that polygon with exact area coverage.
    /// </remarks>
    public static SKBitmap ShapeBitmap()
    {
        var bitmap = NewBitmap();
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var pixels = bitmap.GetPixelSpan().ToArray();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double cov = PolygonCoverage(x, y, 43, 41, 25, 64);
                if (cov <= 0) continue;
                int i = (y * Size + x) * 4;
                pixels[i] = (byte)Math.Floor(0.9 * cov * 255 + 0.5);
                pixels[i + 1] = (byte)Math.Floor(0.9 * 0.55 * cov * 255 + 0.5);
                pixels[i + 2] = (byte)Math.Floor(0.9 * 0.1 * cov * 255 + 0.5);
                pixels[i + 3] = (byte)Math.Floor(0.9 * cov * 255 + 0.5);
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using (var paint = new SKPaint { IsAntialias = true, ColorF = new SKColorF(0.1f, 0.8f, 0.9f, 0.7f) })
            canvas.DrawRect(new SKRect(50, 46, 80, 76), paint);
        return bitmap;
    }

    /// <summary>Area of a regular n-gon inscribed in the circle (vertex at angle 0) inside pixel (x, y), by 64 × 64 sampling.</summary>
    public static double PolygonCoverage(int x, int y, double cx, double cy, double r, int n)
    {
        if (Math.Abs(x + 0.5 - cx) > r + 1 || Math.Abs(y + 0.5 - cy) > r + 1) return 0;
        int inside = 0;
        const int k = 64;
        double seg = 2 * Math.PI / n, apothem = r * Math.Cos(seg / 2);
        for (int j = 0; j < k; j++)
            for (int i = 0; i < k; i++)
            {
                double dx = x + (i + 0.5) / k - cx, dy = y + (j + 0.5) / k - cy;
                double ang = Math.Atan2(dy, dx);
                double local = (ang % seg + seg) % seg - seg / 2;
                if (Math.Sqrt(dx * dx + dy * dy) * Math.Cos(local) <= apothem) inside++;
            }
        return inside / (double)(k * k);
    }

    // MARK: Conversions

    /// <summary>
    /// 8-bit premultiplied sRGB → premultiplied linear, in the steps of 1/4096 the Mac's importer gives.
    /// </summary>
    /// <remarks>
    /// The Mac's importer (CoreGraphics drawing 8-bit sRGB into a linear Float16 context) gives linear values in steps of
    /// 1/4096. Measured on filter-oil_paint.png, the reference most sensitive to it (Kuwahara picks among near-equal
    /// variances): exact linear values leave 436 of 9216 pixels off by more than 4 levels; steps of 1/4095, 1/8191 and
    /// 1/65535 leave 212, 412 and 504; steps of 1/4096 leave 9. The test scene reproduces it so it is the Mac's scene;
    /// the importer itself (Glimmer.Formats) converts exactly.
    /// </remarks>
    public static Vector4[] ToLinear(SKBitmap bitmap)
    {
        var bytes = bitmap.GetPixelSpan();
        var output = new Vector4[Size * Size];
        for (int i = 0; i < Size * Size; i++)
        {
            float a = bytes[i * 4 + 3] / 255f;
            if (a <= 0) continue;
            var straight = new Vector3(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]) / 255f / a;
            Vector3 linear = SrgbTransfer.Decode(straight);
            linear = new Vector3(MathF.Round(linear.X * 4096f), MathF.Round(linear.Y * 4096f), MathF.Round(linear.Z * 4096f)) / 4096f;
            output[i] = new Vector4(linear * a, a);
        }
        return output;
    }

    /// <summary>A 96 × 96 image as tiles (Float16, as the importer stores it).</summary>
    public static TileGrid Tiles(Vector4[] image)
    {
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(image.AsSpan());
        return TileGrid.Empty.WriteRegion(new PixelRect(0, 0, Size, Size), floats);
    }

    private static readonly Lazy<TileGrid> BaseTiles = new(() => { using var b = BaseBitmap(); return Tiles(ToLinear(b)); });
    private static readonly Lazy<TileGrid> ShapeTiles = new(() => { using var b = ShapeBitmap(); return Tiles(ToLinear(b)); });

    public static Layer BaseLayer() => new("Base", new RasterContent(BaseTiles.Value));

    public static Layer ShapeLayer(BlendMode blend = BlendMode.Normal) =>
        new("Shapes", new RasterContent(ShapeTiles.Value), blendMode: blend);

    public static DocumentState State(params Layer[] layers) => new(Size, Size, layers: layers);

    /// <summary>The flattened document through the real compositor, as floats.</summary>
    public static Vector4[] Render(DocumentState state)
    {
        var (buffer, _, rect) = new DocumentCompositor().RenderPixels(state);
        var output = new Vector4[rect.Width * rect.Height];
        TensorPrimitives.ConvertToSingle(buffer, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(output.AsSpan()));
        return output;
    }

    /// <summary>Premultiplied linear → 8-bit straight sRGB (the Mac's <c>ImageExporter.flattenedSRGB</c>, then the PNG writer).</summary>
    public static byte[] To8Bit(Vector4[] image)
    {
        var output = new byte[image.Length * 4];
        for (int i = 0; i < image.Length; i++)
        {
            Vector4 c = image[i];
            float a = Math.Clamp(c.W, 0f, 1f);
            if (a <= 0) continue;
            Vector3 e = SrgbTransfer.Encode(new Vector3(c.X, c.Y, c.Z) / c.W);
            output[i * 4] = (byte)MathF.Floor(Math.Clamp(e.X, 0f, 1f) * 255f + 0.5f);
            output[i * 4 + 1] = (byte)MathF.Floor(Math.Clamp(e.Y, 0f, 1f) * 255f + 0.5f);
            output[i * 4 + 2] = (byte)MathF.Floor(Math.Clamp(e.Z, 0f, 1f) * 255f + 0.5f);
            output[i * 4 + 3] = (byte)MathF.Floor(a * 255f + 0.5f);
        }
        return output;
    }

    public static byte[] LoadGolden(string name)
    {
        using var codec = SKCodec.Create(Path.Combine(Directory, name + ".png"))
            ?? throw new FileNotFoundException("No reference.", Path.Combine(Directory, name + ".png"));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = SKBitmap.Decode(codec, info) ?? throw new InvalidOperationException($"decode {name}");
        return bitmap.GetPixelSpan().ToArray();
    }

    // MARK: Comparison (GoldenImageTests.deltaE)

    /// <summary>sRGB 8-bit → CIELAB (D65).</summary>
    public static (double L, double A, double B) Lab(int r, int g, int b)
    {
        static double Lin(int c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        double R = Lin(r), G = Lin(g), B = Lin(b);
        double x = (0.4124 * R + 0.3576 * G + 0.1805 * B) / 0.95047;
        double y = 0.2126 * R + 0.7152 * G + 0.0722 * B;
        double z = (0.0193 * R + 0.1192 * G + 0.9505 * B) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    /// <summary>Premultiplies straight 8-bit RGBA as CoreGraphics does when drawing into a premultiplied context.</summary>
    public static byte[] Premultiply(byte[] straight)
    {
        var p = new byte[straight.Length];
        for (int i = 0; i < straight.Length; i += 4)
        {
            int a = straight[i + 3];
            for (int c = 0; c < 3; c++) p[i + c] = (byte)((straight[i + c] * a + 127) / 255);
            p[i + 3] = (byte)a;
        }
        return p;
    }

    /// <summary>
    /// Mean and 99th-percentile ΔE76 over pixels visible in either image (colours over a mid-grey matte, so alpha
    /// differences count too). Inputs are premultiplied 8-bit.
    /// </summary>
    public static (double mean, double p99, double max) DeltaE(byte[] a, byte[] b)
    {
        var values = new List<double>();
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i + 3] == 0 && b[i + 3] == 0) continue;
            static int Over(byte[] p, int i, int c) => Math.Min(255, p[i + c] + (255 - p[i + 3]) / 2);  // premultiplied over 50 % grey
            var la = Lab(Over(a, i, 0), Over(a, i, 1), Over(a, i, 2));
            var lb = Lab(Over(b, i, 0), Over(b, i, 1), Over(b, i, 2));
            double d = Math.Sqrt((la.L - lb.L) * (la.L - lb.L) + (la.A - lb.A) * (la.A - lb.A) + (la.B - lb.B) * (la.B - lb.B));
            values.Add(d);
        }
        if (values.Count == 0) return (0, 0, 0);
        values.Sort();
        return (values.Average(), values[Math.Min(values.Count - 1, values.Count * 99 / 100)], values[^1]);
    }

    public static (double mean, double p99, double max) Compare(string name, Vector4[] rendered) =>
        DeltaE(Premultiply(To8Bit(rendered)), Premultiply(LoadGolden(name)));
}
