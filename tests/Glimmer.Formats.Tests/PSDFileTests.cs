// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/PSDFileTests.swift
#if GLIMMER_PSD
using Glimmer.Formats;

namespace Glimmer.Formats.Tests;

public class PSDFileTests
{
    internal static readonly PSDFile.Limits Limits = new(maxCanvasPixels: 1_000_000, maxDecodedBytes: 4_000_000);

    internal static long DecodedBytes(PSDFile file) =>
        file.Layers.Sum(l => l.Planes.Values.Sum(p => (long)p.Length) + (l.Mask?.Pixels?.Length ?? 0));

    [Theory]
    [InlineData(8, false, 0)]
    [InlineData(8, false, 1)]
    [InlineData(8, false, 2)]
    [InlineData(8, false, 3)]
    [InlineData(16, false, 0)]
    [InlineData(16, false, 3)]
    [InlineData(8, true, 1)]
    [InlineData(16, true, 2)]
    public void ReadsWhatWasWritten(int depth, bool large, ushort compression)
    {
        var writer = PSDTestWriter.Sample(depth: depth, large: large, compression: compression);
        var file = new PSDFile(writer.Data(), Limits);
        Assert.Equal(new PSDFile.FileHeader(isLarge: large, channels: 3, width: 4, height: 3, depth: depth, mode: PSDFile.ColorMode.Rgb), file.Header);
        Assert.Equal(["Background copy", "</Layer group>", "Blå himmel", "Hidden", "Group 1"], file.Layers.Select(l => l.Name));
        Assert.Equal([PSDFile.GroupMark.None, PSDFile.GroupMark.End, PSDFile.GroupMark.None, PSDFile.GroupMark.None, PSDFile.GroupMark.Open], file.Layers.Select(l => l.Group));
        var inner = file.Layers[2];
        Assert.True(inner.BlendKey == "mul " && inner.Opacity == 128 && inner.IsVisible && !inner.IsClipped);
        Assert.Equal(new PSDFile.Rect(1, 1, 3, 3), inner.Rect);
        int bytes = depth / 8;
        Assert.Equal(bytes, inner.BytesPerSample);
        Assert.Equal([-1, 0, 1, 2], inner.Planes.Keys.Order());
        Assert.Equal(writer.Samples([1, 2, 3, 4], bytes), inner.Planes[0].ToArray());
        Assert.Equal(writer.Samples([255, 255, 0, 255], bytes), inner.Planes[-1].ToArray());
        Assert.Equal(new PSDFile.Rect(1, 1, 2, 3), inner.Mask?.Rect);
        Assert.Equal((byte)255, inner.Mask?.DefaultColor);
        Assert.Equal(new byte[] { 0, 200 }, inner.Mask?.Pixels?.ToArray());
        var hidden = file.Layers[3];
        Assert.True(!hidden.IsVisible && hidden.IsClipped && hidden.ExtraKeys.Contains("TySh"));
        Assert.Equal(writer.Samples(writer.Layers[0].Planes[2].Samples, bytes), file.Layers[0].Planes[2].ToArray());
        Assert.Equal("pass", file.Layers[4].BlendKey);
        Assert.Equal(3, file.Composite.Length);
        Assert.Equal(writer.Samples(writer.Composite[1], bytes), file.Composite[1].ToArray());
    }

    /// <summary>New in the port: docs/UPSTREAM-NOTES.md, "PSD reader", item 2. The longer mask record holds a second
    /// rectangle (the painted mask once more, its plane channel -3) and then the mask parameters; read in that order, the
    /// -3 plane fits its rectangle and the density is the parameters' byte. The Mac reads the parameters first.</summary>
    [Theory]
    [InlineData(8, 0)]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    public void AMaskRecordWithASecondRectangleAndParametersIsReadInTheOrderFilesHoldIt(int depth, ushort compression)
    {
        ushort[] Plane() => new ushort[12];
        var layer = new PSDTestWriter.Layer(name: "Masked", rect: new PSDFile.Rect(0, 0, 3, 4), planes: [(0, Plane()), (1, Plane()), (2, Plane())], compression: compression)
        {
            Mask = (new PSDFile.Rect(1, 1, 2, 3), 255, [0, 200]),
            RealMask = (new PSDFile.Rect(0, 0, 3, 4), 0, [.. Enumerable.Range(0, 12).Select(i => (byte)(i * 20))]),
            MaskParameters = [1, 77],   // the painted mask's density, 77
        };
        var writer = new PSDTestWriter { Depth = depth, Width = 4, Height = 3, Layers = [layer], Composite = [Plane(), Plane(), Plane()] };
        var file = new PSDFile(writer.Data(), Limits);
        var mask = file.Layers[0].Mask!;
        Assert.Equal(new PSDFile.Rect(1, 1, 2, 3), mask.Rect);
        Assert.Equal(new byte[] { 0, 200 }, mask.Pixels?.ToArray());
        Assert.Equal(new PSDFile.Rect(0, 0, 3, 4), mask.RealRect);
        Assert.Equal((byte)77, mask.Density);
        // The parameters alone, in the short record: still the density.
        layer.RealMask = null;
        Assert.Equal((byte)77, new PSDFile(writer.Data(), Limits).Layers[0].Mask?.Density);
    }

    /// <summary>New in the port: docs/UPSTREAM-NOTES.md, "PSD reader", item 1. Masks are stored at the document's depth
    /// and come down to the one byte a pixel a mask holds: 16-bit levels to the nearest of 256 steps (so v × 257 is v
    /// again), 32-bit ones clamped to 0…1 first.</summary>
    [Fact]
    public void MasksDeeperThanEightBitsBecomeOneByteAPixel()
    {
        byte[] sixteen = [0x00, 0x00, 0xFF, 0xFF, 0xC8, 0xC8, 0x00, 0x80, 0x00, 0x81, 0x7F, 0xFF];
        Assert.Equal(new byte[] { 0, 255, 200, 0, 1, 127 }, PSDFile.MaskBytes(sixteen, 2).ToArray());
        var floats = new byte[6 * 4];
        float[] values = [0, 1, 0.5f, float.NaN, -1, 2];
        for (int i = 0; i < values.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(floats.AsSpan(i * 4), values[i]);
        Assert.Equal(new byte[] { 0, 255, 128, 0, 0, 255 }, PSDFile.MaskBytes(floats, 4).ToArray());
        Assert.Equal(new byte[] { 9, 8 }, PSDFile.MaskBytes(new byte[] { 9, 8 }, 1).ToArray());
    }

    [Fact]
    public void ReadsTheProfileAndARunLengthComposite()
    {
        var writer = PSDTestWriter.Sample();
        writer.IccProfile = "not really a profile"u8.ToArray();
        writer.CompositeCompression = 1;
        var file = new PSDFile(writer.Data(), Limits);
        Assert.Equal("not really a profile"u8.ToArray(), file.IccProfile?.ToArray());
        Assert.Equal(3, file.Composite.Length);
        Assert.Equal(writer.Samples(writer.Composite[0], 1), file.Composite[0].ToArray());
    }

    [Fact]
    public void TheHeaderIsCheckedBeforeAnythingElse()
    {
        var good = PSDTestWriter.Sample().Data();
        byte[] With(int offset, byte[] bytes) { var d = (byte[])good.Clone(); bytes.CopyTo(d, offset); return d; }
        Assert.Equal("This isn't a Photoshop file.", Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(0, "8BPX"u8.ToArray()), Limits)).Message);
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(4, [0, 9]), Limits));              // version 9
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(12, [0, 0]), Limits));             // no channels
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(12, [0, 57]), Limits));            // 57 channels
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(14, [0, 0, 0x75, 0x31]), Limits)); // 30,001 high in a PSD
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(22, [0, 12]), Limits));            // 12 bits
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(With(24, [0, 5]), Limits));             // colour mode 5
        Assert.Throws<PSDFile.Problem>(() => new PSDFile("8BPS"u8.ToArray(), Limits));
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(Array.Empty<byte>(), Limits));
        // A canvas within the format's limits and beyond this computer's is refused from the header alone.
        var huge = With(14, [0, 0, 0x75, 0x30]);
        new byte[] { 0, 0, 0x75, 0x30 }.CopyTo(huge, 18);
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(huge, Limits));
    }

    /// <summary>Cut at every length, the reader throws or returns: it never crashes and never reads past the end.</summary>
    [Fact]
    public void AFileCutAnywhereIsRefusedOrReadWithoutACrash()
    {
        foreach (var writer in new[] { PSDTestWriter.Sample(), PSDTestWriter.Sample(depth: 16, compression: 3), PSDTestWriter.Sample(large: true, compression: 1) })
        {
            var good = writer.Data();
            int read = 0;
            for (int length = 0; length < good.Length; length++)
            {
                try { _ = new PSDFile(good.AsMemory(0, length), Limits); read += 1; }
                catch (PSDFile.Problem) { }
            }
            // Only a cut inside the flattened image at the very end may still give the layers.
            Assert.True(read <= 3 * 4 * 3 * (writer.Depth / 8) + 2, $"{read} of {good.Length} cuts were read");
        }
    }

    /// <summary>Every byte of the file changed, one at a time, to several values: no crash, and nothing beyond the budget.</summary>
    [Fact]
    public void AFileWithAnyByteChangedIsRefusedOrReadWithoutACrash()
    {
        foreach (var writer in new[] { PSDTestWriter.Sample(compression: 1), PSDTestWriter.Sample(depth: 16, compression: 2), PSDTestWriter.Sample(large: true, compression: 3) })
        {
            var good = writer.Data();
            for (int index = 0; index < good.Length; index++)
            {
                foreach (byte value in new byte[] { 0x00, 0x7F, 0x80, 0xFF })
                {
                    if (good[index] == value) continue;
                    var bad = (byte[])good.Clone();
                    bad[index] = value;
                    PSDFile file;
                    try { file = new PSDFile(bad, Limits); } catch (PSDFile.Problem) { continue; }
                    Assert.True(DecodedBytes(file) <= Limits.MaxDecodedBytes);
                }
            }
        }
    }

    [Fact]
    public void AFileThatLiesAboutItsSizesIsRefused()
    {
        // A layer said to be enormous, in a tiny file.
        var writer = PSDTestWriter.Sample();
        writer.Layers[0].Rect = new PSDFile.Rect(0, 0, 900_000, 900_000);
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(writer.Data(), Limits));
        // More layers than Glimmer reads, by count alone.
        var many = PSDTestWriter.Sample().Data();
        const int countAt = 26 + 4 + 4 + 4 + 4;
        many[countAt] = 0x7F; many[countAt + 1] = 0xFF;
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(many, Limits));
        // A plane that is larger than the whole budget.
        Assert.Throws<PSDFile.Problem>(() => new PSDFile(PSDTestWriter.Sample().Data(), new PSDFile.Limits(maxCanvasPixels: 1_000_000, maxDecodedBytes: 8)));
    }

    [Fact]
    public void CompressedDataNeverGivesMoreThanItsPlaneHolds()
    {
        // A run-length row that would produce more than the row holds.
        Assert.Throws<PSDFile.Problem>(() => PSDFile.UnpackBits([0x81, 7], new byte[4]));   // 128 copies into 4
        Assert.Throws<PSDFile.Problem>(() => PSDFile.UnpackBits([3, 1, 2], new byte[4]));   // says 4 literals, has 2
        Assert.Throws<PSDFile.Problem>(() => PSDFile.UnpackBits([0, 1], new byte[4]));      // too short
        // A zlib stream that expands to far more than the plane: stopped at the plane's size, and refused.
        var bomb = PSDTestWriter.Zlib(new byte[1_000_000]);
        Assert.Throws<PSDFile.Problem>(() => PSDFile.Inflate(bomb, 16));
        Assert.Throws<PSDFile.Problem>(() => PSDFile.Inflate(PSDTestWriter.Zlib(new byte[8]), 16));   // too little
        Assert.Equal(Enumerable.Repeat((byte)7, 16), PSDFile.Inflate(PSDTestWriter.Zlib(Enumerable.Repeat((byte)7, 16).ToArray()), 16));
    }

    // New, not in the Swift suite: the first assertion of PSDImportTests.aFileWithNeitherReadableLayersNorAPictureIsRefusedNotShownBlank
    // (in PSDImportTests.cs whole), taken further for the reader alone: both answers, and a file cut inside the resources.
    [Fact]
    public void TheVersionInfoResourceSaysWhetherThereIsAFlattenedPicture()
    {
        var w = new PSDTestWriter
        {
            Layers = [new PSDTestWriter.Layer(name: "Photo", rect: new PSDFile.Rect(0, 0, 2, 2), planes: [(0, [1, 2, 3, 4]), (1, [1, 2, 3, 4]), (2, [1, 2, 3, 4])])],
            Composite = [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]],
        };
        var plain = w.Data();
        Assert.Null(PSDFile.HasFlattenedPicture(plain));   // the file doesn't say
        byte[] Saying(byte flag)
        {
            var data = new List<byte>(plain);
            data[25] = 4;   // the header's colour mode: CMYK
            data.RemoveRange(30, 4);
            data.InsertRange(30, [0, 0, 0, 18]);   // (these files have no colour data and no resources)
            // Resource 1057 ("version info"): an empty name, six bytes; the version (1), then the flag.
            data.InsertRange(34, [.. "8BIM"u8.ToArray(), 0x04, 0x21, 0, 0, 0, 0, 0, 6, 0, 0, 0, 1, flag, 0]);
            return [.. data];
        }
        Assert.False(PSDFile.HasFlattenedPicture(Saying(0)));
        Assert.True(PSDFile.HasFlattenedPicture(Saying(1)));
        Assert.Null(PSDFile.HasFlattenedPicture(Saying(0).AsMemory(0, 40)));   // cut inside the resources
    }
}
#endif
