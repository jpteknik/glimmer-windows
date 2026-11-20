// New file, no Swift original. The Swift tests read exported files back with ImageIO and draw them into an 8-bit
// premultiplied sRGB CGContext; here SkiaSharp decodes PNG, JPEG and WebP the same way, and TIFF (which SkiaSharp
// doesn't read) is read by the small reader below: the files TiffWriter writes (classic TIFF, chunky strips,
// uncompressed or Deflate, predictors 2 and 3). The writer itself is checked against outside readers in TiffWriterTests.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

internal static class ExportedFiles
{
    /// <summary>The Swift tests' <c>solidLayer</c>: a raster layer of one colour covering size × size.</summary>
    public static Layer SolidLayer(Vector4 color, int size = 256, double opacity = 1, BlendMode mode = BlendMode.Normal) =>
        new("L", new RasterContent(TileGrid.Solid(color, new PixelRect(0, 0, size, size))), opacity: opacity, blendMode: mode);

    /// <summary>An 8-bit sRGB bitmap from straight RGBA bytes, premultiplied as the Swift tests' CGContexts.</summary>
    public static SKBitmap Srgb8(byte[] straightRgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb()));
        System.Runtime.InteropServices.Marshal.Copy(straightRgba, 0, bitmap.GetPixels(), straightRgba.Length);
        return bitmap;
    }

    /// <summary>Decodes any exported file to 8-bit premultiplied sRGB (what the Swift tests' CGContexts hold).</summary>
    public static (byte[] Pixels, int Width, int Height) Pixels8(byte[] data)
    {
        if (IsTiff(data))
        {
            var tiff = Tiff(data);
            using var bitmap = tiff.ToBitmap();
            return Read(bitmap);
        }
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException("Skia can't read this file.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var px = new byte[info.Width * info.Height * 4];
        unsafe
        {
            fixed (byte* p = px)
            {
                var result = codec.GetPixels(info, (IntPtr)p);
                if (result != SKCodecResult.Success) throw new InvalidDataException($"Skia decoded {result}.");
            }
        }
        return (px, info.Width, info.Height);
    }

    /// <summary>A bitmap's pixels as 8-bit premultiplied sRGB.</summary>
    public static (byte[] Pixels, int Width, int Height) Read(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var px = new byte[info.Width * info.Height * 4];
        unsafe
        {
            fixed (byte* p = px)
            {
                using var pixmap = bitmap.PeekPixels();
                if (!pixmap.ReadPixels(info, (IntPtr)p, info.Width * 4)) throw new InvalidDataException("ReadPixels failed.");
            }
        }
        return (px, info.Width, info.Height);
    }

    /// <summary>A bitmap's pixels as linear extended-sRGB floats, premultiplied.</summary>
    public static float[] LinearFloats(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.RgbaF32, SKAlphaType.Premul, SKColorSpace.CreateSrgbLinear());
        var px = new float[info.Width * info.Height * 4];
        unsafe
        {
            fixed (float* p = px)
            {
                using var pixmap = bitmap.PeekPixels();
                if (!pixmap.ReadPixels(info, (IntPtr)p, info.Width * 16)) throw new InvalidDataException("ReadPixels failed.");
            }
        }
        return px;
    }

    public static (int Width, int Height) Size(byte[] data)
    {
        if (IsTiff(data)) { var t = Tiff(data); return (t.Width, t.Height); }
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException("Skia can't read this file.");
        return (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>Bits a sample, as the Swift tests' <c>image.bitsPerComponent</c>.</summary>
    public static int BitsPerComponent(byte[] data)
    {
        if (IsTiff(data)) return Tiff(data).Bits;
        if (data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) return data[24];
        return 8;
    }

    public static bool IsTiff(byte[] data) => data.Length > 4 && data[0] == 0x49 && data[1] == 0x49 && data[2] == 42;

    /// <summary>The PNG's chunks, in order: type and data.</summary>
    public static List<(string Type, byte[] Data)> PngChunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        int at = 8;
        while (at + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            chunks.Add((System.Text.Encoding.ASCII.GetString(png, at + 4, 4), png.AsSpan(at + 8, length).ToArray()));
            at += 12 + length;
        }
        return chunks;
    }

    /// <summary>The ICC profile in a PNG's iCCP chunk, or null.</summary>
    public static byte[]? PngProfile(byte[] png)
    {
        foreach (var (type, body) in PngChunks(png))
        {
            if (type != "iCCP") continue;
            int nul = Array.IndexOf(body, (byte)0);
            using var input = new MemoryStream(body, nul + 2, body.Length - nul - 2);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        return null;
    }

    /// <summary>A 16-bit RGBA PNG's samples, read independently of Skia: IDAT inflated and unfiltered.</summary>
    public static ushort[] Png16Samples(byte[] png) => Png16Samples(png, out _);

    /// <summary>The same, and which of the five filters the rows used.</summary>
    public static ushort[] Png16Samples(byte[] png, out HashSet<int> filters)
    {
        filters = [];
        var chunks = PngChunks(png);
        var ihdr = chunks.First(c => c.Type == "IHDR").Data;
        int width = BinaryPrimitives.ReadInt32BigEndian(ihdr), height = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4));
        if (ihdr[8] != 16 || ihdr[9] != 6) throw new InvalidDataException("Not 16-bit RGBA.");
        using var idat = new MemoryStream();
        foreach (var (type, body) in chunks) if (type == "IDAT") idat.Write(body);
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        int rowBytes = width * 8, bpp = 8;
        var samples = new ushort[width * height * 4];
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (int y = 0; y < height; y++)
        {
            int filter = bytes[y * (rowBytes + 1)];
            filters.Add(filter);
            var line = bytes.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? current[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                int p = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Filter {filter}."),
                };
                current[i] = (byte)(line[i] + p);
            }
            for (int i = 0; i < width * 4; i++) samples[y * width * 4 + i] = BinaryPrimitives.ReadUInt16BigEndian(current.AsSpan(i * 2));
            (previous, current) = (current, previous);
        }
        return samples;

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    /// <summary>What a TIFF holds: the samples as numbers (integers as they are stored, floats as floats).</summary>
    internal sealed record TiffImage(int Width, int Height, int Bits, int SamplesPerPixel, int ExtraSample, bool IsFloat, double[] Samples, byte[]? Icc)
    {
        /// <summary>An SKBitmap of the samples (straight or premultiplied as the file says, in the file's profile).</summary>
        public SKBitmap ToBitmap()
        {
            var space = Icc is null ? SKColorSpace.CreateSrgb() : SKColorSpace.CreateIcc(Icc);
            var alpha = SamplesPerPixel == 3 ? SKAlphaType.Opaque : ExtraSample == 1 ? SKAlphaType.Premul : SKAlphaType.Unpremul;
            var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.RgbaF32, alpha, space));
            var floats = new float[Width * Height * 4];
            double max = IsFloat ? 1 : (1 << Bits) - 1;
            for (int i = 0; i < Width * Height; i++)
                for (int c = 0; c < 4; c++)
                    floats[i * 4 + c] = c < SamplesPerPixel ? (float)(Samples[i * SamplesPerPixel + c] / max) : 1f;
            System.Runtime.InteropServices.Marshal.Copy(floats, 0, bitmap.GetPixels(), floats.Length);
            return bitmap;
        }
    }

    public static TiffImage Tiff(byte[] data)
    {
        if (!IsTiff(data)) throw new InvalidDataException("Not a little-endian classic TIFF.");
        uint ifd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)ifd));
        var entries = new Dictionary<int, (int Type, int Count, int At)>();
        for (int i = 0; i < count; i++)
        {
            int at = (int)ifd + 2 + i * 12;
            entries[BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at))] =
                (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)), (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4)), at);
        }
        long[] Values(int tag)
        {
            var (type, n, at) = entries[tag];
            int size = type switch { 3 => 2, 4 => 4, 1 or 7 => 1, _ => throw new InvalidDataException($"type {type}") };
            int start = n * size <= 4 ? at + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8));
            var v = new long[n];
            for (int i = 0; i < n; i++)
                v[i] = size switch
                {
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + i * 2)),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start + i * 4)),
                    _ => data[start + i],
                };
            return v;
        }
        int width = (int)Values(256)[0], height = (int)Values(257)[0];
        int bits = (int)Values(258)[0], spp = (int)Values(277)[0];
        int compression = (int)Values(259)[0];
        int predictor = entries.ContainsKey(317) ? (int)Values(317)[0] : 1;
        bool isFloat = entries.ContainsKey(339) && Values(339)[0] == 3;
        int extra = entries.ContainsKey(338) ? (int)Values(338)[0] : -1;
        byte[]? icc = entries.ContainsKey(34675) ? [.. Values(34675).Select(v => (byte)v)] : null;
        var offsets = Values(273);
        var counts = Values(279);
        int bytesPerSample = bits / 8, rowBytes = width * spp * bytesPerSample;
        var raw = new MemoryStream();
        for (int s = 0; s < offsets.Length; s++)
        {
            var strip = data.AsSpan((int)offsets[s], (int)counts[s]);
            if (compression == 8)
            {
                using var input = new MemoryStream(strip.ToArray());
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                zlib.CopyTo(raw);
            }
            else raw.Write(strip);
        }
        var bytes = raw.ToArray();
        var samples = new double[width * height * spp];
        for (int y = 0; y < height; y++)
        {
            var row = bytes.AsSpan(y * rowBytes, rowBytes);
            if (predictor == 3)
            {
                for (int i = spp; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - spp]);
                int n = row.Length / 4;
                var regrouped = row.ToArray();
                for (int i = 0; i < n; i++)
                    for (int b = 0; b < 4; b++)
                        row[i * 4 + b] = regrouped[(3 - b) * n + i];   // the most significant byte came first
            }
            for (int i = 0; i < width * spp; i++)
            {
                double v = bits switch
                {
                    8 => row[i],
                    16 => BinaryPrimitives.ReadUInt16LittleEndian(row[(i * 2)..]),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(row[(i * 4)..]),
                };
                if (predictor == 2 && i >= spp)
                {
                    double left = samples[y * width * spp + i - spp];
                    v = bits == 8 ? (byte)(v + left) : (ushort)(v + left);
                }
                samples[y * width * spp + i] = v;
            }
        }
        return new TiffImage(width, height, bits, spp, extra, isFloat, samples, icc);
    }
}
