// Ported from Packages/GlimmerKit/Sources/GlimmerCore/ImageImporter.swift
//
// The Mac decodes every picture with ImageIO into a CGImage and draws that into tiles with CoreGraphics. Here SkiaSharp
// decodes PNG, JPEG, WebP, GIF, BMP and ICO, Glimmer's own reader takes a Photoshop file's flattened picture, and the
// system decoder (IPlatformImageDecoder, WIC on Windows) takes the rest. The checks before decoding (the size from the
// header, whether the file has its end) are the Mac's, unchanged.
using System.Globalization;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>Why a picture can't be opened (Swift's <c>ImageImportError</c>).</summary>
public sealed class ImageImportError : Exception
{
    // Swift's cases; FileTooLarge is new (a .NET array holds at most 2 GB).
    public enum Kind { Unreadable, TooLarge, Damaged, FileTooLarge }

    private ImageImportError(Kind reason, string message, long width = 0, long height = 0) : base(message)
    {
        Reason = reason;
        Width = width;
        Height = height;
    }

    public Kind Reason { get; }
    /// <summary>The size a <see cref="Kind.TooLarge"/> file claims.</summary>
    public long Width { get; }
    public long Height { get; }

    public static ImageImportError Unreadable() => new(Kind.Unreadable, "The image could not be read.");

    /// <summary>The file starts like an image but is cut short or broken inside.</summary>
    public static ImageImportError Damaged() => new(Kind.Damaged, "The image file is damaged or incomplete.");

    public static ImageImportError TooLarge(long width, long height) =>
        new(Kind.TooLarge, string.Create(CultureInfo.InvariantCulture,
            $"The image is too large ({width} × {height} pixels). Glimmer opens images up to {ImageImporter.MaxPixels / 1_000_000} megapixels and {DocumentManifest.MaxDimension} pixels a side."),
            width, height);

    /// <summary>New: a file larger than a .NET array can hold.</summary>
    public static ImageImportError FileTooLarge() => new(Kind.FileTooLarge, "The image file is larger than 2 GB, more than Glimmer can open.");
}

/// <summary>
/// Decodes images (PNG, JPEG, WebP, GIF, BMP, ICO; Photoshop's flattened picture; whatever
/// <see cref="PlatformDecoder"/> reads), honouring EXIF orientation and ICC profiles, into linear, premultiplied
/// Float16 tiles.
/// </summary>
public static class ImageImporter
{
    /// <summary>The most pixels an opened image may have (256 megapixels: 16 000 × 16 000, well past
    /// any camera). Decoded, such an image takes about 1 GB, and 2 GB as tiles.</summary>
    public const long MaxPixels = 256_000_000;

    /// <summary>The system's decoder for the formats SkiaSharp can't read. The app sets it at start-up; null reads only
    /// what SkiaSharp and Glimmer read.</summary>
    public static IPlatformImageDecoder? PlatformDecoder { get; set; }

    /// <summary>The picture in a file's bytes (Swift's <c>image(from:)</c>).</summary>
    /// <exception cref="ImageImportError">The bytes are no picture Glimmer reads, the picture is too large, or the file
    /// is damaged or cut short.</exception>
    public static DecodedImage Image(ReadOnlyMemory<byte> data) => Image(data, PlatformDecoder);

    /// <summary>The same with the system's decoder given (the tests give their own instead of setting the shared one).</summary>
    internal static DecodedImage Image(ReadOnlyMemory<byte> data, IPlatformImageDecoder? platformDecoder)
    {
        // In every build, with or without GLIMMER_PSD, as the Mac's ImageIO shows a Photoshop file's picture.
        if (IsPhotoshopFile(data.Span)) return PSDFlattenedImage.Image(data);
        SKCodec? codec = null;
        try
        {
            using (var skData = SKData.CreateCopy(data.Span)) codec = SKCodec.Create(skData);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { codec = null; }
        if (codec is not null)
        {
            using (codec)
            {
                // Checked on the header, before anything is decoded: a small file can claim a huge
                // picture (a decompression bomb), and decoding is what takes the memory.
                CheckSize(codec.Info.Width, codec.Info.Height);
                if (!LooksComplete(data.Span)) throw ImageImportError.Damaged();
                return Decode(codec, data.Span);
            }
        }
        if (platformDecoder is { } platform)
        {
            (int Width, int Height)? size;
            try { size = platform.ReadSize(data); }
            catch (Exception e) when (e is not OutOfMemoryException) { size = null; }
            if (size is { } s)
            {
                CheckSize(s.Width, s.Height);
                if (!LooksComplete(data.Span)) throw ImageImportError.Damaged();
                DecodedImage image;
                try { image = platform.Decode(data); }
                catch (ImageImportError) { throw; }
                catch (Exception e) when (e is not OutOfMemoryException) { throw ImageImportError.Damaged(); }
                bool asPromised = image.Orientation >= 5 ? image.Width == s.Height && image.Height == s.Width : image.Width == s.Width && image.Height == s.Height;
                if (!asPromised) throw ImageImportError.Damaged();
                return image;
            }
        }
        throw ImageImportError.Unreadable();
    }

    /// <summary>The picture in a file (Swift's <c>image(contentsOf:)</c>).</summary>
    public static DecodedImage Image(string path)
    {
        byte[] data;
        try { data = FileInput.Read(path, ImageImportError.FileTooLarge); }
        catch (ImageImportError) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ImageImportError.Unreadable(); }
        return Image(data);
    }

    /// <summary>Converts <paramref name="image"/> to tiles with its top-left at <paramref name="origin"/> (document pixels).</summary>
    public static TileGrid Tiles(DecodedImage image, (int X, int Y) origin = default) => image.ToTiles(origin);

    internal static bool IsPhotoshopFile(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("8BPS"u8);

    internal static void CheckSize(long width, long height)
    {
        if (width < 1 || height < 1 || width > DocumentManifest.MaxDimension || height > DocumentManifest.MaxDimension || width * height > MaxPixels)
            throw ImageImportError.TooLarge(width, height);
    }

    // MARK: SkiaSharp

    private static DecodedImage Decode(SKCodec codec, ReadOnlySpan<byte> data)
    {
        var info = codec.Info;
        int width = info.Width, height = info.Height;
        int orientation = (int)codec.EncodedOrigin is >= 1 and <= 8 and var o ? o : 1;
        var alpha = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var color = SourceColor.FromSkia(info.ColorSpace);
        long pixels = (long)width * height;
        // Skia converts the picture to linear sRGB itself when the colour space is one it can't describe with numbers (a
        // CMYK JPEG's profile, a grey profile, PQ or HLG), and for a 16-bit PNG: Skia hands over its samples only as half
        // floats, and converted from the 16-bit samples they come within one step of a half float of the exact value,
        // where rounding the file's own values to half floats first and converting after costs up to three (measured
        // over all 65,536 levels; ImageImporterTests.SixteenBitPngsKeepTheirPrecision).
        if (color is null || IsSixteenBitPng(data))
        {
            using var linear = SKColorSpace.CreateSrgbLinear();
            var halves = new Half[pixels * 4];
            GetPixels(codec, new SKImageInfo(width, height, SKColorType.RgbaF16, alpha, linear), halves);
            return DecodedImage.Halves(width, height, halves, SourceColor.LinearSrgb, orientation);
        }
        if (info.ColorType == SKColorType.Gray8)
        {
            // A grey picture without a profile of its own is sRGB grey here, as SkiaSharp reads it (the Windows
            // convention). The Mac may take it as Generic Gray Gamma 2.2 instead, as its PSD import does (not verified).
            var gray = new byte[pixels];
            GetPixels(codec, new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque, info.ColorSpace), gray);
            return DecodedImage.Bytes(width, height, 1, gray, color, orientation);
        }
        var rgba = new byte[pixels * 4];
        GetPixels(codec, new SKImageInfo(width, height, SKColorType.Rgba8888, alpha, info.ColorSpace), rgba);
        return DecodedImage.Bytes(width, height, 4, rgba, color, orientation);
    }

    private static unsafe void GetPixels<T>(SKCodec codec, SKImageInfo target, T[] buffer) where T : unmanaged
    {
        SKCodecResult result;
        fixed (T* p = buffer) result = codec.GetPixels(target, (IntPtr)p);
        if (result == SKCodecResult.Success) return;
        // Skia decodes what it can of a file that is cut short or broken and says so; half a picture is never opened.
        throw result is SKCodecResult.IncompleteInput or SKCodecResult.ErrorInInput or SKCodecResult.InvalidInput
            ? ImageImportError.Damaged() : ImageImportError.Unreadable();
    }

    /// <summary>A PNG with 16 bits a sample (its header's bit depth; Skia's codec reports such files as 8-bit).</summary>
    private static bool IsSixteenBitPng(ReadOnlySpan<byte> data) =>
        data.Length > 24 && data[0] == 0x89 && data[1..4].SequenceEqual("PNG"u8) && data[12..16].SequenceEqual("IHDR"u8) && data[24] == 16;

    // MARK: Whether the file has its end

    /// <summary>Whether a PNG, JPEG, HEIC/AVIF, TIFF or WebP file has its end: ImageIO decodes what it
    /// can of a file that is cut short and fills the rest with grey or black, without saying
    /// so. Other formats pass (ImageIO refuses most of their broken files itself).</summary>
    /// <remarks>Swift's integers are 64-bit; every sum of a number from the file is <see cref="long"/> here.</remarks>
    internal static bool LooksComplete(ReadOnlySpan<byte> b)
    {
        long n = b.Length;
        if (n < 12) return true;
        long Be32(ReadOnlySpan<byte> s, long i) => (long)s[(int)i] << 24 | (long)s[(int)i + 1] << 16 | (long)s[(int)i + 2] << 8 | s[(int)i + 3];
        bool Tag(ReadOnlySpan<byte> s, long i, ReadOnlySpan<byte> t) => i + t.Length <= n && s.Slice((int)i, t.Length).SequenceEqual(t);
        if (b[0] == 0x89 && Tag(b, 1, "PNG"u8))
        {
            // Walk the chunks to IEND (whatever follows it, such as an appended file, is ignored).
            long at = 8;
            while (at + 12 <= n)
            {
                long length = Be32(b, at);
                if (Tag(b, at + 4, "IEND"u8)) return true;
                if (length < 0 || at + 12 + length > n) return false;
                at += 12 + length;
            }
            return false;
        }
        if (b[0] == 0xFF && b[1] == 0xD8)
        {
            // Walk the segments to the end-of-image marker of the first picture (phones
            // append a video or a second picture after it; that is fine).
            long at = 2;
            while (at + 4 <= n)
            {
                if (b[(int)at] != 0xFF) return false;
                byte marker = b[(int)at + 1];
                if (marker == 0xD9) return true;
                if (marker == 0xFF) { at += 1; continue; }   // fill byte
                if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
                long length = b[(int)at + 2] << 8 | b[(int)at + 3];
                if (length < 2 || at + 2 + length > n) return false;
                at += 2 + length;
                if (marker == 0xDA)
                {
                    // Entropy-coded data runs to the next real marker.
                    while (at + 1 < n && !(b[(int)at] == 0xFF && b[(int)at + 1] != 0x00 && b[(int)at + 1] is not (>= 0xD0 and <= 0xD7))) at += 1;
                }
            }
            return at + 1 < n && b[(int)at] == 0xFF && b[(int)at + 1] == 0xD9;
        }
        if (Tag(b, 0, "RIFF"u8) && Tag(b, 8, "WEBP"u8))
        {
            long size = b[4] | (long)b[5] << 8 | (long)b[6] << 16 | (long)b[7] << 24;
            return size + 8 == n || size + 9 == n;
        }
        if (Tag(b, 4, "ftyp"u8))
        {
            // ISO media boxes must tile the file exactly.
            long at = 0;
            while (at + 8 <= n)
            {
                long size = Be32(b, at);
                if (size == 1)
                {
                    if (at + 16 > n || Be32(b, at + 8) != 0) return false;
                    size = Be32(b, at + 12);
                }
                else if (size == 0)
                {
                    return true;   // "to the end of the file"
                }
                if (size < 8 || at + size > n) return false;
                at += size;
            }
            return at == n;
        }
        bool little = b[0] == 0x49 && b[1] == 0x49, big = b[0] == 0x4D && b[1] == 0x4D;
        if (little || big) return TiffLooksComplete(b, little);
        return true;
    }

    private static bool TiffLooksComplete(ReadOnlySpan<byte> b, bool little)
    {
        long n = b.Length;
        long U16(ReadOnlySpan<byte> s, long i) => i < 0 || i + 2 > n ? -1 : little ? s[(int)i] | s[(int)i + 1] << 8 : s[(int)i] << 8 | s[(int)i + 1];
        long U32(ReadOnlySpan<byte> s, long i) => i < 0 || i + 4 > n ? -1 : little
            ? s[(int)i] | (long)s[(int)i + 1] << 8 | (long)s[(int)i + 2] << 16 | (long)s[(int)i + 3] << 24
            : (long)s[(int)i] << 24 | (long)s[(int)i + 1] << 16 | (long)s[(int)i + 2] << 8 | s[(int)i + 3];
        if (U16(b, 2) != 42) return true;   // BigTIFF and others: left to the decoder
        long ifd = U32(b, 4);
        if (ifd < 0 || ifd + 2 > n) return false;
        long count = U16(b, ifd);
        if (ifd + 2 + count * 12 > n) return false;
        // Every strip or tile (offset + byte count) must lie inside the file.
        long[]? Values(ReadOnlySpan<byte> s, long entry)
        {
            long type = U16(s, entry + 2), k = U32(s, entry + 4);
            long width = type == 3 ? 2 : type == 4 ? 4 : 0;
            if (width == 0 || k < 1 || k > 1_000_000) return null;
            long at = k * width <= 4 ? entry + 8 : U32(s, entry + 8);
            if (at < 0 || at + k * width > n) return null;
            var values = new long[k];
            for (long i = 0; i < k; i++) values[i] = width == 2 ? U16(s, at + i * 2) : U32(s, at + i * 4);
            return values;
        }
        long[]? offsets = null, lengths = null;
        for (long e = 0; e < count; e++)
        {
            long entry = ifd + 2 + e * 12;
            // Whatever an entry points at (a colour profile, metadata) must be in the file too.
            long unit = U16(b, entry + 2) switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };
            if (unit > 0)
            {
                long bytes = U32(b, entry + 4) * unit;
                if (bytes > 4 && U32(b, entry + 8) + bytes > n) return false;
            }
            switch (U16(b, entry))
            {
                case 273:
                case 324:
                    offsets = Values(b, entry);
                    if (offsets is null) return false;
                    break;
                case 279:
                case 325:
                    lengths = Values(b, entry);
                    if (lengths is null) return false;
                    break;
            }
        }
        if (offsets is not null && lengths is not null && offsets.Length == lengths.Length)
        {
            for (int i = 0; i < offsets.Length; i++) if (offsets[i] + lengths[i] > n) return false;
        }
        return true;
    }
}
