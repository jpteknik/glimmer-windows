using System.Buffers.Binary;

namespace Glimmer.Lzfse.Tests;

/// <summary>
/// Streams built from the vectors for format features no vector shows on its own. What each must decode to was
/// checked with Apple's reference decoder when the tests were written.
/// </summary>
public class ConstructedStreamTests
{
    [Fact]
    public void BlockWithOmittedFrequencyTables_ReusesThePreviousBlocksTables()
    {
        // A bvx2 header may leave out the frequency tables (header_size 32). The reference decoder then writes no
        // table entries, so the block is decoded with the previous block's tables. Here the block is repeated with
        // its tables omitted; both copies decode to the same data.
        byte[] stream = TestData.Vector("ref-text-100000");
        byte[] text = Lzfse.Decode(stream);
        var block = TestData.Blocks(stream)[0];
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(24));
        Assert.True(headerSize > 32);

        byte[] copy = [.. stream.AsSpan(0, 32), .. stream.AsSpan(headerSize, block.Length - headerSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(24), 32);
        byte[] twice = [.. stream.AsSpan(0, block.Length), .. copy, .. TestData.EndOfStream];

        byte[] expected = [.. text, .. text];
        Assert.Equal(expected, Lzfse.Decode(twice));
    }

    [Fact]
    public void FirstBlockWithOmittedFrequencyTables_IsRejected()
    {
        // With no previous block the tables are empty: every value decodes as 0, so the first match has no distance.
        byte[] stream = TestData.Vector("ref-text-100000");
        var block = TestData.Blocks(stream)[0];
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(24));
        byte[] alone = [.. stream.AsSpan(0, 32), .. stream.AsSpan(headerSize, block.Length - headerSize),
            .. TestData.EndOfStream];
        BinaryPrimitives.WriteUInt32LittleEndian(alone.AsSpan(24), 32);

        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(alone));
    }

    [Fact]
    public void WholeStreamsBackToBack_DecodeToTheirConcatenation()
    {
        // Every block kind, in one stream; later blocks' positions are shifted by all the output before them.
        string[] names = ["ref-text-1000", "ref-random-100", "ref-text-4096", "conv-soft-edge-tile-bvx1",
            "lzfse-rust-compound", "ref-photo-tile", "ref-empty", "ref-text-7"];
        byte[][] streams = names.Select(TestData.Vector).ToArray();
        byte[] expected = streams.SelectMany(s => Lzfse.Decode(s)).ToArray();

        byte[] decoded = Lzfse.Decode(TestData.Concatenate(streams), expected.Length);

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void BytesAfterTheEndOfStreamBlock_AreIgnored()
    {
        // As by the reference decoder, which stops at the end-of-stream block.
        byte[] stream = TestData.Vector("ref-text-4096");
        byte[] withTail = [.. stream, .. "bvx2 and anything else"u8];

        Assert.Equal(Lzfse.Decode(stream), Lzfse.Decode(withTail));
    }

    [Fact]
    public void RepeatedZeroBitBlock_ExpandsOverTwoHundredfold()
    {
        // 7 blocks of 783 bytes, each 160000 bytes of 'A' read from zero bits: what a decompression bomb looks like.
        byte[] stream = ZeroBitBlocks(7);

        byte[] decoded = Lzfse.Decode(stream);

        Assert.Equal(7 * 160000, decoded.Length);
        Assert.True(decoded.All(b => b == 'A'));
        Assert.True(decoded.Length / stream.Length > 200);
    }

    internal static byte[] ZeroBitBlocks(int count)
    {
        byte[] one = TestData.Vector("hand-zero-bit-block");
        return TestData.Concatenate(Enumerable.Repeat(one, count).ToArray());
    }
}
