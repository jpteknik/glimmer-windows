// Ported from Packages/GlimmerKit/Sources/GlimmerRender/PSDAppearance.swift. The file's own picture is read by Glimmer's
// flattened-picture reader (PSDFlattenedImage, every colour mode it knows, the file's profile applied, a further channel
// left alone) where the Mac builds a CGImage from RGB planes and falls back to ImageIO for other modes. The pictures are
// laid on white in 8-bit sRGB values, as the Mac's CGContext draws them.
#if GLIMMER_PSD   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>
/// Measures how far Glimmer's picture of an imported Photoshop file is from the flattened picture the file itself
/// carries. A file saved for compatibility holds that picture, so an import can be checked against what the program
/// that wrote it saw.
/// </summary>
public static class PSDAppearance
{
    /// <summary>Documents larger than this are not compared: drawing the whole picture twice to measure it would cost
    /// more memory than the import itself.</summary>
    public const int LargestCompared = 64_000_000;

    /// <summary>The mean difference per channel (0…255) and the share of pixels that differ by more than 8 in some
    /// channel, both over sRGB with the pictures laid on white. Null when the file's own picture can't be read or the
    /// sizes differ.</summary>
    public static (double Mean, double ShareVisible)? Difference(DocumentState state, ReadOnlyMemory<byte> file)
    {
        if ((long)state.Width * state.Height > LargestCompared || PSDFile.HasFlattenedPicture(file) == false) return null;
        try
        {
            using var theirs = FlattenedPicture(file);
            if (theirs is null || theirs.Width != state.Width || theirs.Height != state.Height) return null;
            using var ours = ImageExporter.FlattenedSRGB(state);
            if (OnWhite(ours) is not { } a || OnWhite(theirs) is not { } b || a.Length != b.Length || a.Length == 0) return null;
            return Difference(a, b);
        }
        catch (Exception e) when (e is ImageImportError or PSDFile.Problem or Glimmer.Render.RenderError or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The flattened picture as the file stores it: its colour planes, in the file's profile, taken as they are.
    /// A file with a transparent background stores this picture already laid on white, with the transparency in a further
    /// channel; a general image reader applies that channel once more and gets a paler picture (seen with files from
    /// Photoshop, 2026-11-16), so the planes are read directly and the extra channel is left alone.</summary>
    internal static SKBitmap? FlattenedPicture(ReadOnlyMemory<byte> file)
    {
        DecodedImage picture;
        try { picture = PSDFlattenedImage.Image(file); }
        catch (ImageImportError) { return null; }
        var single = new DocumentState(picture.Width, picture.Height, layers: [new Layer("picture", new RasterContent(ImageImporter.Tiles(picture)))]);
        return ImageExporter.FlattenedSRGB(single);
    }

    /// <summary>The same measure for two pictures already as 8-bit sRGB over white, of the same size.</summary>
    internal static (double Mean, double ShareVisible) Difference(byte[] a, byte[] b)
    {
        long total = 0, visible = 0;
        int pixels = Math.Min(a.Length, b.Length) / 4;
        if (pixels == 0) return (0, 0);
        for (int i = 0; i < pixels; i++)
        {
            int worst = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(a[i * 4 + c] - b[i * 4 + c]);
                total += d;
                worst = Math.Max(worst, d);
            }
            if (worst > 8) visible += 1;
        }
        return ((double)total / (pixels * 3L), (double)visible / pixels);
    }

    /// <summary>The picture as 8-bit sRGB over white, so that transparency compares as what is seen (RGBA, the fourth
    /// byte 255).</summary>
    internal static unsafe byte[]? OnWhite(SKBitmap image)
    {
        int width = image.Width, height = image.Height;
        if (width < 1 || height < 1) return null;
        // As CoreGraphics draws into an 8-bit sRGB context: premultiplied 8-bit sRGB values, white under them.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bytes = new byte[(long)width * height * 4];
        fixed (byte* p = bytes)
        {
            var sourceInfo = image.ColorSpace is null ? image.Info.WithColorSpace(SKColorSpace.CreateSrgb()) : image.Info;
            using var source = new SKPixmap(sourceInfo, image.GetPixels(), image.RowBytes);
            if (!source.ReadPixels(info, (IntPtr)p, width * 4)) return null;
        }
        for (long i = 0; i < bytes.Length; i += 4)
        {
            int under = 255 - bytes[i + 3];
            bytes[i] = (byte)Math.Min(255, bytes[i] + under);
            bytes[i + 1] = (byte)Math.Min(255, bytes[i + 1] + under);
            bytes[i + 2] = (byte)Math.Min(255, bytes[i + 2] + under);
            bytes[i + 3] = 255;
        }
        return bytes;
    }
}
#endif
