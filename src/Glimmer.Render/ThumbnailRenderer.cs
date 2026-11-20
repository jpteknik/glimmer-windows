// Ported from Packages/GlimmerKit/Sources/GlimmerRender/ThumbnailRenderer.swift
using System.Numerics;
using System.Runtime.InteropServices;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Render;

/// <summary>Renders small previews of layers for the Layers panel.</summary>
public sealed class ThumbnailRenderer
{
    public ThumbnailRenderer() { }

    /// <summary>
    /// A preview of <paramref name="grid"/>, clipped to <paramref name="documentBounds"/>, at most
    /// <paramref name="maxPixels"/> on its long side: RgbaF16, premultiplied, linear sRGB (the Mac returns a CGImage of
    /// the same pixels). Null when there is nothing to draw.
    /// </summary>
    public SKBitmap? Thumbnail(TileGrid grid, PixelRect documentBounds, int maxPixels, float background = 0)
    {
        if (documentBounds.IsEmpty || maxPixels <= 0) return null;
        double scale = (double)maxPixels / Math.Max(documentBounds.Width, documentBounds.Height);
        int w = Math.Max(1, (int)Math.Round(documentBounds.Width * scale, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(documentBounds.Height * scale, MidpointRounding.AwayFromZero));

        var pixels = new Vector4[w * h];
        pixels.AsSpan().Fill(new Vector4(background));
        const double s = TileGeometry.Size;
        foreach (var coord in documentBounds.TileCoords)
        {
            if (grid[coord] is not { } tile) continue;
            var r = coord.Rect.Intersection(documentBounds);
            double u0 = (r.X - coord.Rect.X) / s, v0 = (r.Y - coord.Rect.Y) / s;
            // The tile's quad in output pixels and its uv rectangle, as the Mac draws it.
            float qx = (float)((r.X - documentBounds.X) * scale), qy = (float)((r.Y - documentBounds.Y) * scale);
            float qw = (float)(r.Width * scale), qh = (float)(r.Height * scale);
            float ua = (float)u0, va = (float)v0, ub = (float)(u0 + r.Width / s), vb = (float)(v0 + r.Height / s);
            float du = (ub - ua) / qw, dv = (vb - va) / qh;  // the uv derivatives per output pixel
            // Pixels whose centres fall inside the quad.
            int x0 = Math.Max(0, (int)MathF.Ceiling(qx - 0.5f)), x1 = Math.Min(w, (int)MathF.Ceiling(qx + qw - 0.5f));
            int y0 = Math.Max(0, (int)MathF.Ceiling(qy - 0.5f)), y1 = Math.Min(h, (int)MathF.Ceiling(qy + qh - 0.5f));
            for (int y = y0; y < y1; y++)
            {
                float v = va + (y + 0.5f - qy) * dv;
                for (int x = x0; x < x1; x++)
                {
                    float u = ua + (x + 0.5f - qx) * du;
                    Vector4 c = Supersample(tile, new Vector2(u, v), new Vector2(du, dv));
                    ref var d = ref pixels[y * w + x];
                    d = c + d * (1f - c.W);  // premultiplied, blended "over"
                }
            }
        }

        var info = new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul, SKColorSpace.CreateSrgbLinear());
        var bitmap = new SKBitmap(info);
        var halves = new Half[w * h * 4];
        var floats = MemoryMarshal.Cast<Vector4, float>(pixels.AsSpan());
        for (int i = 0; i < floats.Length; i++) halves[i] = (Half)floats[i];
        int bytesPerRow = bitmap.RowBytes;
        var bytes = MemoryMarshal.AsBytes(halves.AsSpan());
        for (int y = 0; y < h; y++)
            Marshal.Copy(bytes.Slice(y * w * 8, w * 8).ToArray(), 0, bitmap.GetPixels() + y * bytesPerRow, w * 8);
        return bitmap;
    }

    /// <summary><c>thumbnail_fragment</c>: 4×4 supersampled minification (bilinear, edges clamped) of one tile.</summary>
    internal static Vector4 Supersample(Tile tile, Vector2 uv, Vector2 step)
    {
        if (tile.IsSolid) return tile.SolidColor;
        Vector4 sum = Vector4.Zero;
        for (int j = 0; j < 4; j++)
        {
            for (int i = 0; i < 4; i++)
            {
                var o = (new Vector2(i, j) + new Vector2(0.5f)) / 4f - new Vector2(0.5f);
                sum += SampleLinear(tile.PixelSpan, (uv + o * step) * TileGeometry.Size);
            }
        }
        return sum / 16f;
    }

    /// <summary>Bilinear sample of a tile at texel coordinate <paramref name="p"/> (centres at i + 0.5), edges clamped.</summary>
    private static Vector4 SampleLinear(ReadOnlySpan<Half> px, Vector2 p)
    {
        const int size = TileGeometry.Size;
        float tx = p.X - 0.5f, ty = p.Y - 0.5f;
        float fx0 = MathF.Floor(tx), fy0 = MathF.Floor(ty);
        float fx = tx - fx0, fy = ty - fy0;
        int xa = Math.Clamp((int)fx0, 0, size - 1), xb = Math.Clamp((int)fx0 + 1, 0, size - 1);
        int ya = Math.Clamp((int)fy0, 0, size - 1), yb = Math.Clamp((int)fy0 + 1, 0, size - 1);
        static Vector4 Read(ReadOnlySpan<Half> px, int x, int y)
        {
            int i = (y * size + x) * 4;
            return new Vector4((float)px[i], (float)px[i + 1], (float)px[i + 2], (float)px[i + 3]);
        }
        Vector4 a = Read(px, xa, ya), b = Read(px, xb, ya), c = Read(px, xa, yb), d = Read(px, xb, yb);
        Vector4 top = a + (b - a) * fx, bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }

    // MARK: 8-bit PNG

    /// <summary>
    /// PNG bytes (8-bit sRGB, straight alpha) of a premultiplied linear image such as <see cref="Thumbnail"/> returns:
    /// the .glimmer package's QuickLook/Thumbnail.png and Preview.png, and previews in the UI.
    /// </summary>
    public static byte[] PngData(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        var rgba = new Half[w * h * 4];
        if (image.ColorType == SKColorType.RgbaF16 && image.AlphaType == SKAlphaType.Premul
            && (image.ColorSpace is null || image.ColorSpace.GammaIsLinear))
        {
            var bytes = MemoryMarshal.AsBytes(rgba.AsSpan());
            var row = new byte[w * 8];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(image.GetPixels() + y * image.RowBytes, row, 0, row.Length);
                row.CopyTo(bytes.Slice(y * w * 8, w * 8));
            }
        }
        else
        {
            // Any other format: let Skia convert it to premultiplied linear half floats first.
            var info = new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul, SKColorSpace.CreateSrgbLinear());
            using var converted = new SKBitmap(info);
            if (!image.CopyTo(converted, SKColorType.RgbaF16)) throw RenderError.ResourceCreationFailed("PNG conversion");
            return PngData(converted);
        }
        return PngData(rgba, w, h);
    }

    /// <summary>
    /// PNG bytes (8-bit sRGB, straight alpha) of <paramref name="width"/> × <paramref name="height"/> premultiplied
    /// linear RGBA values, row-major (what <see cref="DocumentCompositor.RenderPixels"/> returns).
    /// </summary>
    public static byte[] PngData(ReadOnlySpan<Half> premultipliedLinear, int width, int height)
    {
        if (width <= 0 || height <= 0 || premultipliedLinear.Length < width * height * 4)
            throw new ArgumentException("The pixel buffer doesn't match the size.", nameof(premultipliedLinear));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        using var bitmap = new SKBitmap(info);
        var row = new byte[width * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float a = Math.Clamp((float)premultipliedLinear[i + 3], 0, 1);
                byte a8 = (byte)MathF.Round(a * 255, MidpointRounding.AwayFromZero);
                if (a8 == 0)
                {
                    row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = row[x * 4 + 3] = 0;
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    float v = (float)premultipliedLinear[i + c] / a;
                    float e = SrgbTransfer.Encode(Math.Clamp(v, 0, 1));
                    row[x * 4 + c] = (byte)MathF.Round(e * 255, MidpointRounding.AwayFromZero);
                }
                row[x * 4 + 3] = a8;
            }
            Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
