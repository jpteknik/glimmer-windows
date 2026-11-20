// New file, no Swift original: the TIFF writer is new in the port (the Mac uses ImageIO). Every file is read back by
// outside readers (tifffile, and Pillow, which decodes through libtiff), never by Glimmer's own code.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Glimmer.Formats;

namespace Glimmer.Formats.Tests;

public class TiffWriterTests
{
    private static readonly byte[] Profile = [.. "a profile, any bytes: TIFF stores it as it is"u8.ToArray(), 0, 1, 2, 3, 255];

    private static int Samples(TiffWriter.Alpha alpha) => alpha == TiffWriter.Alpha.None ? 3 : 4;

    /// <summary>A picture with smooth parts, noise, and in floats values above 1, below 0, tiny and huge.</summary>
    private static Array Picture(TiffWriter.SampleFormat format, int width, int height, TiffWriter.Alpha alpha, int seed = 1)
    {
        int n = width * height * Samples(alpha);
        var random = new Random(seed);
        switch (format)
        {
            case TiffWriter.SampleFormat.UInt8:
                return Enumerable.Range(0, n).Select(i => (byte)(i % 7 == 0 ? random.Next(256) : (i / Samples(alpha) % width) * 255 / Math.Max(1, width - 1))).ToArray();
            case TiffWriter.SampleFormat.UInt16:
                return Enumerable.Range(0, n).Select(i => (ushort)(i % 5 == 0 ? random.Next(65536) : (i / Samples(alpha) / width) * 65535 / Math.Max(1, height - 1))).ToArray();
            default:
                float[] special = [0f, -0f, 2f, 0.5f, -1.25f, 1e-30f, 65504f, 3.4e38f, float.Epsilon, 1f / 3];
                return Enumerable.Range(0, n).Select(i => i % 11 == 0 ? special[i / 11 % special.Length] : (float)(random.NextDouble() * 4 - 1)).ToArray();
        }
    }

    private static byte[] Bytes(Array samples) => samples switch
    {
        byte[] b => b,
        ushort[] s => MemoryMarshal.AsBytes(s.AsSpan()).ToArray(),
        float[] f => MemoryMarshal.AsBytes(f.AsSpan()).ToArray(),
        _ => throw new ArgumentException("samples"),
    };

    private static byte[] Encode(Array samples, int width, int height, TiffWriter.Alpha alpha, TiffWriter.Options options)
    {
        using var stream = new MemoryStream();
        switch (samples)
        {
            case byte[] b: TiffWriter.Write(stream, width, height, b, alpha, options); break;
            case ushort[] s: TiffWriter.Write(stream, width, height, s, alpha, options); break;
            case float[] f: TiffWriter.Write(stream, width, height, f, alpha, options); break;
        }
        return stream.ToArray();
    }

    private static JsonElement ReadOutside(string script, byte[] tiff)
    {
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-tiff-{Guid.NewGuid():N}.tif");
        try
        {
            File.WriteAllBytes(path, tiff);
            return OutsideReader.Read(script, path);
        }
        finally { File.Delete(path); }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int[] Ints(JsonElement value) => value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(v => v.GetInt32())] : [value.GetInt32()];

    public static TheoryData<TiffWriter.SampleFormat, TiffWriter.Alpha, TiffWriter.Compression, bool> EveryForm()
    {
        var data = new TheoryData<TiffWriter.SampleFormat, TiffWriter.Alpha, TiffWriter.Compression, bool>();
        foreach (var format in Enum.GetValues<TiffWriter.SampleFormat>())
            foreach (var alpha in Enum.GetValues<TiffWriter.Alpha>())
            {
                data.Add(format, alpha, TiffWriter.Compression.None, false);
                data.Add(format, alpha, TiffWriter.Compression.Deflate, false);
                data.Add(format, alpha, TiffWriter.Compression.Deflate, true);
            }
        return data;
    }

    /// <summary>Every depth, alpha and compression, read back by tifffile: the tags say what was written and the
    /// samples come back bit for bit, floats included.</summary>
    [OutsideReaderTheory("tifffile", "imagecodecs", "numpy")]
    [MemberData(nameof(EveryForm))]
    public void AnOutsideReaderReadsEveryFormExactly(TiffWriter.SampleFormat format, TiffWriter.Alpha alpha, TiffWriter.Compression compression, bool predictor)
    {
        // 301 × 257 is several strips at every depth, and odd in both directions.
        const int width = 301, height = 257;
        var samples = Picture(format, width, height, alpha);
        var tiff = Encode(samples, width, height, alpha, new TiffWriter.Options { Compression = compression, Predictor = predictor, IccProfile = Profile });
        var read = ReadOutside("read_tiff_with_tifffile.py", tiff);
        var tags = read.GetProperty("tags");
        int bits = format switch { TiffWriter.SampleFormat.UInt8 => 8, TiffWriter.SampleFormat.UInt16 => 16, _ => 32 };
        Assert.False(read.GetProperty("bigtiff").GetBoolean());
        Assert.Equal(1, read.GetProperty("pages").GetInt32());
        Assert.Equal(width, tags.GetProperty("256").GetInt32());
        Assert.Equal(height, tags.GetProperty("257").GetInt32());
        Assert.All(Ints(tags.GetProperty("258")), b => Assert.Equal(bits, b));
        Assert.Equal((int)compression, tags.GetProperty("259").GetInt32());
        Assert.Equal(2, tags.GetProperty("262").GetInt32());               // RGB
        Assert.Equal(Samples(alpha), tags.GetProperty("277").GetInt32());
        Assert.Equal(1, tags.GetProperty("284").GetInt32());               // chunky
        int expectedPredictor = compression == TiffWriter.Compression.Deflate && predictor ? (format == TiffWriter.SampleFormat.Float32 ? 3 : 2) : 1;
        Assert.Equal(expectedPredictor, tags.TryGetProperty("317", out var p) ? p.GetInt32() : 1);
        if (alpha == TiffWriter.Alpha.None) Assert.False(tags.TryGetProperty("338", out _));
        else Assert.Equal([alpha == TiffWriter.Alpha.Associated ? 1 : 2], Ints(tags.GetProperty("338")));
        Assert.All(Ints(tags.GetProperty("339")), f => Assert.Equal(format == TiffWriter.SampleFormat.Float32 ? 3 : 1, f));
        Assert.Equal(Sha(Profile), read.GetProperty("icc").GetString());
        Assert.Equal([72, 1], Ints(read.GetProperty("xresolution")));
        Assert.Equal([height, width, Samples(alpha)], Ints(read.GetProperty("shape")));
        Assert.Equal(Sha(Bytes(samples)), read.GetProperty("sha").GetString());
    }

    /// <summary>Pillow decodes through libtiff, a second implementation: the 8-bit files come back sample for sample.</summary>
    [OutsideReaderTheory("PIL")]
    [InlineData(TiffWriter.Alpha.None, TiffWriter.Compression.None)]
    [InlineData(TiffWriter.Alpha.Unassociated, TiffWriter.Compression.Deflate)]
    [InlineData(TiffWriter.Alpha.Associated, TiffWriter.Compression.Deflate)]
    public void LibtiffReadsTheEightBitFilesToo(TiffWriter.Alpha alpha, TiffWriter.Compression compression)
    {
        const int width = 97, height = 61;
        var samples = Picture(TiffWriter.SampleFormat.UInt8, width, height, alpha);
        var tiff = Encode(samples, width, height, alpha, new TiffWriter.Options { Compression = compression, IccProfile = Profile });
        var read = ReadOutside("read_tiff_with_pillow.py", tiff);
        Assert.Equal("TIFF", read.GetProperty("format").GetString());
        Assert.Equal(alpha == TiffWriter.Alpha.None ? "RGB" : "RGBA", read.GetProperty("mode").GetString());
        Assert.True(read.GetProperty("width").GetInt32() == width && read.GetProperty("height").GetInt32() == height);
        Assert.Equal(Sha(Profile), read.GetProperty("icc").GetString());
        // Pillow opens premultiplied ("associated") alpha as straight RGBA, dividing as its unpackRGBa does:
        // c × 255 / a, truncated and clipped to 255; a pixel with no alpha becomes all zeros.
        var expected = (byte[])samples;
        if (alpha == TiffWriter.Alpha.Associated)
        {
            expected = (byte[])expected.Clone();
            for (int i = 0; i < expected.Length; i += 4)
            {
                int a = expected[i + 3];
                if (a == 0) { expected[i] = expected[i + 1] = expected[i + 2] = 0; continue; }
                if (a == 255) continue;
                for (int c = 0; c < 3; c++) expected[i + c] = (byte)Math.Min(255, expected[i + c] * 255 / a);
            }
        }
        Assert.Equal(Sha(expected), read.GetProperty("sha").GetString());
    }

    /// <summary>After WebPEncoderTests.floatTIFFKeepsHDRValues (Swift), the writer's part: linear 2.0, brighter than
    /// white, comes back as a float above 1.</summary>
    [OutsideReaderFact("tifffile", "imagecodecs", "numpy")]
    public void AFloatFileKeepsValuesAboveOne()
    {
        const int side = 64;
        var samples = new float[side * side * 4];
        for (int i = 0; i < side * side; i++) { samples[i * 4] = 2; samples[i * 4 + 1] = 0.5f; samples[i * 4 + 2] = 0; samples[i * 4 + 3] = 1; }
        var tiff = Encode(samples, side, side, TiffWriter.Alpha.Associated, new TiffWriter.Options());
        var read = ReadOutside("read_tiff_with_tifffile.py", tiff);
        Assert.Equal("float32", read.GetProperty("dtype").GetString());
        Assert.Equal(Sha(Bytes(samples)), read.GetProperty("sha").GetString());
    }

    /// <summary>BigTIFF, as a file over 4 GB is written: the outside reader takes it and the samples are the same.</summary>
    [OutsideReaderFact("tifffile", "imagecodecs", "numpy")]
    public void BigTiffIsReadBackTheSame()
    {
        const int width = 211, height = 1_303;   // several strips
        var samples = Picture(TiffWriter.SampleFormat.UInt16, width, height, TiffWriter.Alpha.Unassociated);
        var tiff = Encode(samples, width, height, TiffWriter.Alpha.Unassociated, new TiffWriter.Options { BigTiff = true, IccProfile = Profile });
        Assert.Equal([(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0], tiff[..8]);
        var read = ReadOutside("read_tiff_with_tifffile.py", tiff);
        Assert.True(read.GetProperty("bigtiff").GetBoolean());
        Assert.Equal(Sha(Profile), read.GetProperty("icc").GetString());
        Assert.Equal(Sha(Bytes(samples)), read.GetProperty("sha").GetString());
    }

    /// <summary>A single row, a single column, a single pixel, and more strips than rows in a strip.</summary>
    [OutsideReaderTheory("tifffile", "imagecodecs", "numpy")]
    [InlineData(1, 1)]
    [InlineData(5_000, 1)]
    [InlineData(1, 3_000)]
    [InlineData(3, 70_001)]
    public void OddShapesComeBackTheSame(int width, int height)
    {
        var samples = Picture(TiffWriter.SampleFormat.UInt8, width, height, TiffWriter.Alpha.Unassociated, seed: width + height);
        foreach (var compression in new[] { TiffWriter.Compression.None, TiffWriter.Compression.Deflate })
        {
            var tiff = Encode(samples, width, height, TiffWriter.Alpha.Unassociated, new TiffWriter.Options { Compression = compression });
            var read = ReadOutside("read_tiff_with_tifffile.py", tiff);
            Assert.Equal(Sha(Bytes(samples)), read.GetProperty("sha").GetString());
        }
    }

    [Fact]
    public void TheSameInputGivesTheSameBytesHoweverManyThreadsMakeThem()
    {
        var samples = (float[])Picture(TiffWriter.SampleFormat.Float32, 333, 517, TiffWriter.Alpha.Associated);
        var one = Encode(samples, 333, 517, TiffWriter.Alpha.Associated, new TiffWriter.Options { Parallelism = 1 });
        var many = Encode(samples, 333, 517, TiffWriter.Alpha.Associated, new TiffWriter.Options { Parallelism = 8 });
        Assert.Equal(one, many);
        Assert.Equal(one, Encode(samples, 333, 517, TiffWriter.Alpha.Associated, new TiffWriter.Options { Parallelism = 8 }));
    }

    [Fact]
    public void WhatCantBeWrittenIsRefusedBeforeAByteIsWritten()
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => TiffWriter.Write(stream, 0, 4, new byte[0], TiffWriter.Alpha.None));
        Assert.Throws<ArgumentException>(() => TiffWriter.Write(stream, 4, 4, new byte[4 * 4 * 3 - 1], TiffWriter.Alpha.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => TiffWriter.Write(stream, 4, 4, new byte[4 * 4 * 4], TiffWriter.Alpha.Associated, new TiffWriter.Options { Parallelism = 0 }));
        Assert.Equal(0, stream.Length);
    }

    /// <summary>Deflate with the predictor makes a smooth picture much smaller than it is raw.</summary>
    [Fact]
    public void ThePredictorPaysOnASmoothPicture()
    {
        const int width = 512, height = 512;
        var samples = new ushort[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++) samples[(y * width + x) * 3 + c] = (ushort)((x * 97 + y * 61 + c * 1000) & 0xFFFF);
        long raw = Encode(samples, width, height, TiffWriter.Alpha.None, new TiffWriter.Options { Compression = TiffWriter.Compression.None }).Length;
        long deflate = Encode(samples, width, height, TiffWriter.Alpha.None, new TiffWriter.Options { Predictor = false }).Length;
        long predicted = Encode(samples, width, height, TiffWriter.Alpha.None, new TiffWriter.Options()).Length;
        Assert.True(predicted < deflate && predicted < raw / 20, $"raw {raw}, deflate {deflate}, deflate with predictor {predicted}");
    }
}
