// Ported from Packages/GlimmerKit/Sources/GlimmerRender/WebPEncoder.swift and VP8Encoder.swift, in spirit: the Mac
// writes WebP with its own encoders (ImageIO reads WebP but can't write it); SkiaSharp carries libwebp, which writes both
// kinds, so here the encoding is libwebp's and this keeps the Mac's entry points, limits and messages. libwebp's limit is
// one pixel less than the Mac's encoder allowed (16 383, the largest lossy WebP frame). The Mac's near-lossless mode has
// no counterpart in SkiaSharp and is not offered.
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>WebP: lossless (VP8L) and lossy (VP8, with an exact alpha plane), with the colour profile of the given
/// space embedded.</summary>
public static class WebPEncoder
{
    public sealed class Error(int width, int height)
        : Exception(FormattableString.Invariant($"WebP images can be at most 16 383 × 16 383 pixels (this one is {width} × {height})."))
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
    }

    public const int MaxDimension = 16_383;

    /// <summary>How hard libwebp works on a lossless file (0 fast … 100 small): its own default.</summary>
    private const float LosslessEffort = 75;

    /// <summary>Encodes straight-alpha RGBA8 pixels (row-major, top-down) losslessly. <paramref name="colorSpace"/>'s
    /// ICC profile is embedded through the extended (VP8X) container; null embeds none (the file is then sRGB by
    /// WebP's convention).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, SKColorSpace? colorSpace = null) =>
        Encode(rgba, width, height, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, LosslessEffort), colorSpace);

    /// <summary>Lossy WebP: a VP8 frame, plus alpha and an ICC profile when present. <paramref name="quality"/> is 0…1.</summary>
    public static byte[] EncodeLossy(ReadOnlySpan<byte> rgba, int width, int height, double quality, SKColorSpace? colorSpace = null) =>
        Encode(rgba, width, height, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, (float)Math.Clamp(quality * 100, 0, 100)), colorSpace);

    private static unsafe byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, SKWebpEncoderOptions options, SKColorSpace? colorSpace)
    {
        if (width > MaxDimension || height > MaxDimension || width <= 0 || height <= 0) throw new Error(width, height);
        if (rgba.Length < (long)width * height * 4) throw new ArgumentException("Too few pixels for the size.", nameof(rgba));
        bool opaque = true;
        for (int i = 3; i < width * height * 4; i += 4)
        {
            if (rgba[i] != 255) { opaque = false; break; }
        }
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul, colorSpace);
        fixed (byte* p = rgba)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)p, width * 4);
            using var data = pixmap.Encode(options) ?? throw Glimmer.Render.RenderError.ResourceCreationFailed("encoding WebP failed");
            return data.ToArray();
        }
    }
}
