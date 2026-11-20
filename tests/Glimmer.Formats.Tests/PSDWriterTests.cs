// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/PSDWriterTests.swift
#if GLIMMER_PSD
using System.Collections.Immutable;
using Glimmer.Formats;

namespace Glimmer.Formats.Tests;

/// <summary>The writer against the reader, and against an outside reader.</summary>
public class PSDWriterTests
{
    internal static readonly PSDFile.Limits Limits = new(maxCanvasPixels: 1_000_000, maxDecodedBytes: 8_000_000);

    private static void SameParts(PSDFile.Layer a, PSDFile.Layer b)
    {
        Assert.True(a.Name == b.Name && a.Rect == b.Rect && a.BlendKey == b.BlendKey && a.Opacity == b.Opacity, a.Name);
        Assert.True(a.IsClipped == b.IsClipped && a.IsVisible == b.IsVisible && a.Group == b.Group && ByteContent.Equal(a.Planes, b.Planes), a.Name);
        Assert.True(a.Mask?.Rect == b.Mask?.Rect && a.Mask?.DefaultColor == b.Mask?.DefaultColor && a.Mask?.IsDisabled == b.Mask?.IsDisabled
                    && ByteContent.Equal(a.Mask?.Pixels, b.Mask?.Pixels), a.Name);
        Assert.True(ByteContent.Equal(a.Blocks, b.Blocks), a.Name);
    }

    /// <summary>A file made by the tests' writer, read, written by Glimmer's writer, and read again: the same parts.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void WhatIsWrittenIsReadBackTheSame(int depth)
    {
        var source = PSDTestWriter.Sample(depth: depth, compression: 1);
        source.IccProfile = "a profile"u8.ToArray();
        source.Layers[2].ExtraBlocks = [("SoCo", PSDTestWriter.SolidColor(red: 1, green: 2, blue: 3)), ("vmsk", PSDTestWriter.VectorMask([(0, 0), (1, 0), (1, 1)]))];
        var first = new PSDFile(source.Data(), Limits);
        var written = PSDWriter.Data(first);
        var second = new PSDFile(written, Limits);
        Assert.True(second.Header == first.Header && ByteContent.Equal(second.IccProfile, first.IccProfile) && ByteContent.Equal(second.Composite, first.Composite));
        Assert.Equal(first.Layers.Length, second.Layers.Length);
        foreach (var (a, b) in first.Layers.Zip(second.Layers)) SameParts(a, b);
        // Written twice, the same bytes: nothing in the file depends on the time or on chance.
        Assert.Equal(written, PSDWriter.Data(second));
        // (New: the stream form writes the same bytes as the array form.)
        using var stream = new MemoryStream();
        PSDWriter.Write(second, stream);
        Assert.Equal(written, stream.ToArray());
    }

    [Fact]
    public void RunLengthRowsComeBackWhateverTheyHold()
    {
        List<byte[]> cases =
        [
            [], [7], Enumerable.Repeat((byte)9, 300).ToArray(), Enumerable.Range(0, 300).Select(i => (byte)(i % 251)).ToArray(),
            [1, 1, 2, 2, 2, 3, 3, 3, 3, 4, 5, 5, 6, 6, 6], new byte[128], new byte[129],
        ];
        var random = new Random();
        for (int i = 0; i < 200; i++) cases.Add(Enumerable.Range(0, random.Next(1, 401)).Select(_ => (byte)random.Next(0, 4)).ToArray());
        foreach (var row in cases)
        {
            var output = new byte[row.Length];
            PSDFile.UnpackBits(PSDWriter.PackBits(row), output);
            Assert.Equal(row, output);
        }
    }

    /// <summary>Swift: <c>macOSReadsTheFlattenedPictureOfAWrittenFile</c>. ImageIO was the one reader on the Mac that Glimmer
    /// didn't write; here Pillow is (ported in spirit: the same property, another outside reader). It must show the
    /// flattened picture as written.</summary>
    [OutsideReaderFact("PIL")]
    public void AnOutsideReaderReadsTheFlattenedPictureOfAWrittenFile()
    {
        const int width = 16, height = 12;
        byte[] PlaneOf(Func<int, int, int> value) => [.. Enumerable.Range(0, width * height).Select(i => (byte)value(i % width, i / width))];
        byte[][] composite = [PlaneOf((x, _) => 10 + x * 15), PlaneOf((_, y) => 20 + y * 18), PlaneOf((x, y) => (x + y) % 2 == 0 ? 200 : 40)];
        var layer = new PSDFile.Layer("Bakgrund åäö", new PSDFile.Rect(0, 0, height, width), "norm", 255, isClipped: false, isVisible: true, PSDFile.GroupMark.None, null,
                                      [], new Dictionary<int, ReadOnlyMemory<byte>> { [0] = composite[0], [1] = composite[1], [2] = composite[2] }, bytesPerSample: 1);
        var file = new PSDFile(new PSDFile.FileHeader(isLarge: false, channels: 3, width: width, height: height, depth: 8, mode: PSDFile.ColorMode.Rgb),
                               [layer], composite.Select(p => (ReadOnlyMemory<byte>)p), TestProfiles.Srgb());
        var data = PSDWriter.Data(file);
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-psd-{Guid.NewGuid():N}.psd");
        try
        {
            File.WriteAllBytes(path, data);
            var read = OutsideReader.Read("read_psd_with_pillow.py", path);
            Assert.Equal("PSD", read.GetProperty("format").GetString());
            Assert.True(read.GetProperty("width").GetInt32() == width && read.GetProperty("height").GetInt32() == height);
            var pixels = Convert.FromBase64String(read.GetProperty("pixels").GetString()!);
            int worst = 0;
            for (int i = 0; i < width * height; i++)
                for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(pixels[i * 3 + c] - composite[c][i]));
            Assert.True(worst <= 1, $"PSD written by Glimmer, flattened picture as Pillow decodes it: largest difference {worst} of 255");
        }
        finally { File.Delete(path); }
        // And Glimmer's own reader takes the same file, with the Unicode name.
        Assert.Equal("Bakgrund åäö", new PSDFile(data, Limits).Layers.FirstOrDefault()?.Name);
    }

    /// <summary>New in the port: the writer's half of docs/UPSTREAM-NOTES.md, "PSD reader", item 1. A 16-bit file's mask
    /// is written at 16 bits, as Photoshop stores it, and an outside reader decodes it so: the levels 0 and 200 as
    /// 0 × 257 and 200 × 257. (The Mac writes it in 8 bits.)</summary>
    [OutsideReaderFact("psd_tools")]
    public void ASixteenBitFilesMaskIsWrittenAtSixteenBits()
    {
        var first = new PSDFile(PSDTestWriter.Sample(depth: 16, compression: 1).Data(), Limits);
        string path = Path.Combine(Path.GetTempPath(), $"glimmer-psd-{Guid.NewGuid():N}.psd");
        try
        {
            File.WriteAllBytes(path, PSDWriter.Data(first));
            var read = OutsideReader.Read("dump_psd_with_psd_tools.py", path);
            var sky = read.GetProperty("layers")[2];
            Assert.Equal("[1,1,3,3]", sky.GetProperty("rect").ToString().Replace(" ", ""));
            var mask = sky.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == -2);
            byte[] expected = [0x00, 0x00, 0xC8, 0xC8];
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expected)), mask.GetProperty("dataDepth").GetProperty("sha").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheWriterRefusesWhatItsReaderWouldRefuse()
    {
        PSDFile Make(int width = 4, int height = 4, int depth = 8, bool large = false, PSDFile.ColorMode mode = PSDFile.ColorMode.Rgb, int? planeBytes = null, int layers = 1)
        {
            int size = planeBytes ?? width * height * (depth / 8);
            var plane = new byte[Math.Max(0, Math.Min(size, 1 << 20))];
            var layer = new PSDFile.Layer("L", new PSDFile.Rect(0, 0, Math.Min(height, 64), Math.Min(width, 64)), "norm", 255, isClipped: false, isVisible: true,
                                          PSDFile.GroupMark.None, null, [], ImmutableDictionary<int, ReadOnlyMemory<byte>>.Empty, bytesPerSample: depth / 8);
            return new PSDFile(new PSDFile.FileHeader(isLarge: large, channels: 3, width: width, height: height, depth: depth, mode: mode),
                               Enumerable.Repeat(layer, layers), [plane, plane, plane], null);
        }
        _ = PSDWriter.Data(Make());
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(width: 30_001)));
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(width: 0)));
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(depth: 32)));
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(large: true)));
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(mode: PSDFile.ColorMode.Cmyk)));
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(planeBytes: 3)));       // a flattened picture of the wrong size
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(Make(layers: 8_001)));
        var bad = Make();
        bad = bad with { Layers = [bad.Layers[0] with { Planes = ImmutableDictionary<int, ReadOnlyMemory<byte>>.Empty.Add(0, new byte[5]) }] };   // a plane that doesn't fit its rectangle
        Assert.Throws<PSDFile.Problem>(() => PSDWriter.Data(bad));
    }

    /// <summary>Every written file goes back through the hostile-file checks: cut anywhere, any byte changed, no crash.</summary>
    [Fact]
    public void AWrittenFileSurvivesTheSameAbuseAsAnyOther()
    {
        var first = new PSDFile(PSDTestWriter.Sample(depth: 8, compression: 0).Data(), Limits);
        var good = PSDWriter.Data(first);
        for (int length = 0; length < good.Length; length++)
        {
            try { _ = new PSDFile(good.AsMemory(0, length), Limits); } catch (PSDFile.Problem) { }
        }
        for (int index = 0; index < good.Length; index++)
        {
            foreach (byte value in new byte[] { 0x00, 0xFF })
            {
                if (good[index] == value) continue;
                var bad = (byte[])good.Clone();
                bad[index] = value;
                PSDFile file;
                try { file = new PSDFile(bad, Limits); } catch (PSDFile.Problem) { continue; }
                Assert.True(file.Layers.Sum(l => l.Planes.Values.Sum(p => (long)p.Length)) <= Limits.MaxDecodedBytes);
            }
        }
    }
}

#endif
