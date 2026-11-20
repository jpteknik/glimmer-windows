using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Glimmer.Lzfse.Tests;

/// <summary>The embedded test vectors (see Vectors/SOURCES.txt) and deterministic test inputs.</summary>
internal static class TestData
{
    internal const int TileByteCount = 256 * 256 * 8;

    internal static byte[] Vector(string name)
    {
        using var stream = typeof(TestData).Assembly.GetManifestResourceStream($"Vectors.{name}.lzfse")
            ?? throw new FileNotFoundException($"No embedded vector {name}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The four-byte magic of each block of a stream, walking the block headers.</summary>
    internal static List<string> BlockMagics(byte[] stream) => Blocks(stream).Select(b => b.Magic).ToList();

    /// <summary>Each block of a stream: its magic, where it starts and how long it is.</summary>
    internal static List<(string Magic, int Offset, int Length)> Blocks(byte[] stream)
    {
        var blocks = new List<(string, int, int)>();
        int p = 0;
        while (p + 4 <= stream.Length)
        {
            string magic = System.Text.Encoding.ASCII.GetString(stream, p, 4);
            int length;
            switch (magic)
            {
                case "bvx$":
                    blocks.Add((magic, p, 4));
                    return blocks;
                case "bvx-":
                    length = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(p + 4));
                    break;
                case "bvxn":
                    length = 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(p + 8));
                    break;
                case "bvx1":
                    length = 772 + (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(p + 20))
                        + (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(p + 24));
                    break;
                case "bvx2":
                    ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(p + 8));
                    ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(p + 16));
                    ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(stream.AsSpan(p + 24));
                    length = (int)(uint)v2 + (int)((v0 >> 20) & 0xfffff) + (int)((v1 >> 40) & 0xfffff);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected block {magic} at {p}.");
            }
            blocks.Add((magic, p, length));
            p += length;
        }
        throw new InvalidOperationException("No end-of-stream block.");
    }

    internal static readonly byte[] EndOfStream = "bvx$"u8.ToArray();

    /// <summary>Whole streams back to back: every end-of-stream block but the last is dropped.</summary>
    internal static byte[] Concatenate(params byte[][] streams)
    {
        var result = new List<byte>();
        for (int i = 0; i < streams.Length; i++)
            result.AddRange(i == streams.Length - 1 ? streams[i] : streams[i][..^4]);
        return [.. result];
    }

    /// <summary>Deterministic pseudo-random bytes (xorshift64*), the same on every run.</summary>
    internal static byte[] RandomBytes(int length, ulong seed)
    {
        var data = new byte[length];
        ulong x = seed * 0x9E3779B97F4A7C15UL + 1;
        for (int i = 0; i < length; i++)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            data[i] = (byte)((x * 0x2545F4914F6CDD1DUL) >> 56);
        }
        return data;
    }

    /// <summary>Text-like data: words drawn from a small vocabulary.</summary>
    internal static byte[] Text(int length, ulong seed)
    {
        string[] words = ["tile", "layer", "pixel", "the", "of", "glimmer", "brush", "mask", "a", "and", "blend",
            "colour", "curve", "document", "\n", "selection", "is", "to", "with", "alpha"];
        var random = RandomBytes(length, seed);
        var text = new System.Text.StringBuilder();
        for (int i = 0; text.Length < length; i++)
            text.Append(words[random[i % random.Length] % words.Length]).Append(' ');
        return System.Text.Encoding.ASCII.GetBytes(text.ToString(0, length));
    }

    /// <summary>
    /// A tile of Float16 RGBA pixels as Glimmer stores them: a smooth image with sensor-like noise in the low bits,
    /// opaque, row-major, little-endian.
    /// </summary>
    internal static byte[] PhotoLikeTile(ulong seed)
    {
        var noise = RandomBytes(256 * 256 * 3, seed);
        var tile = new byte[TileByteCount];
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
        {
            int p = (y * 256 + x) * 8;
            int n = (y * 256 + x) * 3;
            float r = 0.20f + 0.50f * x / 255f + (noise[n] - 128) / 4096f;
            float g = 0.30f + 0.30f * MathF.Sin(y / 40f) + (noise[n + 1] - 128) / 4096f;
            float b = 0.10f + 0.40f * (x + y) / 510f + (noise[n + 2] - 128) / 4096f;
            BinaryPrimitives.WriteHalfLittleEndian(tile.AsSpan(p), (Half)r);
            BinaryPrimitives.WriteHalfLittleEndian(tile.AsSpan(p + 2), (Half)g);
            BinaryPrimitives.WriteHalfLittleEndian(tile.AsSpan(p + 4), (Half)b);
            BinaryPrimitives.WriteHalfLittleEndian(tile.AsSpan(p + 6), (Half)1f);
        }
        return tile;
    }

    /// <summary>A short pattern repeated: long overlapping matches.</summary>
    internal static byte[] Repeated(int length, int period, ulong seed)
    {
        var pattern = RandomBytes(period, seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = pattern[i % period];
        return data;
    }

    /// <summary>Random literals mixed with copies of earlier data at every distance, as LZ coders see in practice.</summary>
    internal static byte[] Mixed(int length, ulong seed)
    {
        var random = new Random((int)seed);
        var data = new List<byte>(length);
        while (data.Count < length)
        {
            if (data.Count > 0 && random.NextDouble() < 0.6)
            {
                int distance = Math.Min(data.Count, (int)Math.Pow(10, random.NextDouble() * 5.5));
                int count = (int)Math.Pow(10, random.NextDouble() * 3.6);
                int start = data.Count - distance;
                for (int k = 0; k < count; k++) data.Add(data[start + k]);
            }
            else
            {
                var fresh = new byte[(int)Math.Pow(10, random.NextDouble() * 2.5)];
                random.NextBytes(fresh);
                data.AddRange(fresh);
            }
        }
        return [.. data.Take(length)];
    }
}
