using System.Buffers.Binary;

namespace Glimmer.Lzfse.Tests;

/// <summary>
/// Damaged and hostile input: the decoder either decodes it or throws <see cref="InvalidDataException"/> from one of
/// its checks; it never throws anything else, runs away, or allocates beyond what it was allowed.
/// </summary>
public class CorruptStreamTests
{
    /// <summary>Small vectors that together hold every block kind and every LZVN opcode form.</summary>
    public static TheoryData<string> SmallVectors => new()
    {
        "lzfse-rust-compound", "lzfse-rust-vx1", "lzfse-rust-vx2", "lzfse-rust-vxn", "lzfse-rust-raw", "dissect-large",
        "hand-lzvn-every-opcode", "hand-mixed-blocks-lzvn-back-reference", "hand-lzvn-after-stored-block",
        "ref-text-1000", "ref-period3-3000", "ref-mix",
    };

    /// <summary>Decodes; returns 1 if the stream was rejected. Anything but a check's own exception fails the test.</summary>
    private static int DecodeOrReject(byte[] stream, int expectedLength)
    {
        try
        {
            _ = expectedLength < 0 ? Lzfse.Decode(stream) : Lzfse.Decode(stream, expectedLength);
            return 0;
        }
        catch (InvalidDataException e)
        {
            // No inner exception: an explicit check rejected the stream, not the out-of-range safety net.
            Assert.Null(e.InnerException);
            return 1;
        }
    }

    [Theory]
    [MemberData(nameof(SmallVectors))]
    public void EverySingleBitFlip_DecodesOrIsRejectedByACheck(string name)
    {
        byte[] stream = TestData.Vector(name);
        int length = Lzfse.Decode(stream).Length;
        int rejected = 0;
        for (int i = 0; i < stream.Length; i++)
        for (int bit = 0; bit < 8; bit++)
        {
            stream[i] ^= (byte)(1 << bit);
            rejected += DecodeOrReject(stream, -1);
            DecodeOrReject(stream, length);
            stream[i] ^= (byte)(1 << bit);
        }

        Assert.InRange(rejected, 1, stream.Length * 8);
    }

    [Theory]
    [MemberData(nameof(SmallVectors))]
    public void EveryTruncation_IsRejected(string name)
    {
        byte[] stream = TestData.Vector(name);
        for (int n = 0; n < stream.Length; n++)
            Assert.Equal(1, DecodeOrReject(stream[..n], -1));
    }

    [Fact]
    public void RandomBytes_AreRejected()
    {
        int rejected = 0;
        string[] magics = ["", "bvx2", "bvx1", "bvxn", "bvx-"];
        for (int i = 0; i < 2000; i++)
        {
            byte[] garbage = TestData.RandomBytes(i % 1500, (ulong)i + 1);
            byte[] stream = [.. System.Text.Encoding.ASCII.GetBytes(magics[i % magics.Length]), .. garbage];
            rejected += DecodeOrReject(stream, -1);
        }

        Assert.Equal(2000, rejected);
    }

    [Fact]
    public void EmptyOrForeignInput_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Lzfse.Decode([]));
        Assert.Equal("Not an LZFSE stream.",
            Assert.Throws<InvalidDataException>(() => Lzfse.Decode("GLTL\u0001\0\0\0"u8)).Message);
        byte[] storedThenUnknown = [.. TestData.Vector("ref-text-7")[..^4], .. "bvx9"u8];
        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(storedThenUnknown));
    }

    [Fact]
    public void NegativeExpectedLength_IsAnArgumentError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Lzfse.Decode(TestData.Vector("ref-empty"), -1));
    }

    // MARK: - Headers

    [Fact]
    public void HeaderSizeSmallerThanTheHeader_IsRejected()
    {
        byte[] stream = TestData.Vector("ref-text-4096");
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(24), 31);

        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream));
    }

    [Theory]
    [InlineData(LzfseConstants.V1LStateOffset, 64)]
    [InlineData(LzfseConstants.V1MStateOffset, 64)]
    [InlineData(LzfseConstants.V1DStateOffset, 256)]
    [InlineData(LzfseConstants.V1LiteralStateOffset, 1024)]
    [InlineData(LzfseConstants.V1LiteralStateOffset + 6, 1024)]
    public void InitialStateOutsideItsTable_IsRejected(int offset, int state)
    {
        byte[] stream = TestData.Vector("lzfse-rust-vx1");
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(offset), (ushort)state);

        Assert.Contains("block header", Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream)).Message);
    }

    [Theory]
    [InlineData(LzfseConstants.LFreqOffset)]
    [InlineData(LzfseConstants.MFreqOffset)]
    [InlineData(LzfseConstants.DFreqOffset)]
    [InlineData(LzfseConstants.LiteralFreqOffset)]
    public void FrequenciesAddingUpToMoreStatesThanTheTableHas_AreRejected(int table)
    {
        byte[] stream = TestData.Vector("lzfse-rust-vx1");
        var freq = stream.AsSpan(LzfseConstants.V1FreqOffset + 2 * table, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(freq, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(freq) + 1));

        Assert.Contains("block header", Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream)).Message);
    }

    [Fact]
    public void FirstMatchReusingAPreviousDistance_IsRejected()
    {
        // lzfse_rust's null.vx2: every value decodes as 0, so the first match takes the "previous" distance before
        // there is one. The reference decoder rejects it too.
        var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(TestData.Vector("lzfse-rust-null-vx2")));
        Assert.Contains("match distance", error.Message);
    }

    [Fact]
    public void MatchesUsingMoreLiteralsThanTheBlockHas_AreRejected()
    {
        // The reference decoder accepts this stream: it reads the missing literals from its zeroed scratch buffer.
        var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(TestData.Vector("fuzz-literal-overflow")));
        Assert.Contains("literal overflow", error.Message);
    }

    // MARK: - LZVN blocks (each stream was also given to the reference decoder; see the comments)

    private static byte[] Lzvn(int rawBytes, params byte[] opcodes)
    {
        byte[] payload = [.. opcodes, 6, 0, 0, 0, 0, 0, 0, 0];
        var header = new byte[12];
        "bvxn"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), rawBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
        return [.. header, .. payload, .. TestData.EndOfStream];
    }

    [Fact]
    public void LzvnLiteralThenMatch_Decodes()
    {
        // sml_l "a", then lrg_d with distance 1 and length 3. Reference: "aaaa".
        Assert.Equal("aaaa"u8.ToArray(), Lzfse.Decode(Lzvn(4, 0xE1, 0x61, 0x07, 0x01, 0x00)));
    }

    [Fact]
    public void LzvnMatchBeforeTheStartOfTheOutput_IsRejected()
    {
        // Distance 5 after one byte. The reference never finishes: it keeps asking for a bigger output buffer.
        var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(Lzvn(4, 0xE1, 0x61, 0x07, 0x05, 0x00)));
        Assert.Contains("invalid match distance", error.Message);
    }

    [Fact]
    public void LzvnMatchWithoutAPreviousDistance_IsRejected()
    {
        // sml_m reuses the previous distance, and there is none. The reference copies whatever its output buffer held
        // (it returned "a\x07" + "3:" from uninitialized memory when this test was written).
        var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(Lzvn(4, 0xE1, 0x61, 0xF3)));
        Assert.Contains("previous distance", error.Message);
    }

    [Fact]
    public void MatchCopyWithDistanceZero_IsRejectedInsteadOfLoopingForever()
    {
        // A match repeats the bytes `distance` back; with distance 0 there is nothing to repeat, and the copy's
        // doubling loop would never advance. The block decoders reject such matches first; the copy checks again, so
        // a missed check cannot become a hang.
        Assert.Throws<InvalidDataException>(() => LzfseDecoder.CopyMatch(new byte[32], 8, 0, 4));
    }

    [Fact]
    public void LzvnUndefinedOpcode_IsRejected()
    {
        // Reference: never finishes, as above.
        Assert.Contains("undefined opcode",
            Assert.Throws<InvalidDataException>(() => Lzfse.Decode(Lzvn(1, 0xE1, 0x61, 0x1E))).Message);
    }

    [Theory]
    [InlineData(5, new byte[] { 0xE1, 0x61 })]        // ends before its declared size (reference: rejects)
    [InlineData(1, new byte[] { 0xE2, 0x61, 0x62 })]  // writes past its declared size (reference: rejects)
    [InlineData(0, new byte[0])]                      // no raw bytes (reference: rejects)
    public void LzvnBlockNotProducingItsDeclaredSize_IsRejected(int rawBytes, byte[] opcodes)
    {
        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(Lzvn(rawBytes, opcodes)));
    }

    [Fact]
    public void LzvnPayloadContinuingAfterEndOfStream_IsRejected()
    {
        byte[] stream = Lzvn(1, 0xE1, 0x61);
        byte[] longer = [.. stream[..^4], 0, .. TestData.EndOfStream];
        BinaryPrimitives.WriteInt32LittleEndian(longer.AsSpan(8), BinaryPrimitives.ReadInt32LittleEndian(longer.AsSpan(8)) + 1);

        Assert.Equal("a"u8.ToArray(), Lzfse.Decode(stream));
        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(longer)); // reference: rejects
    }

    // MARK: - Sizes and memory

    [Fact]
    public void StoredBlockLongerThanTheInput_IsRejectedWithoutReservingIt()
    {
        byte[] stream = [.. "bvx-"u8, 0xFF, 0xFF, 0xFF, 0x7F, .. TestData.RandomBytes(100, 1), .. TestData.EndOfStream];

        long allocated = AllocatedBy(() => Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream)));

        Assert.InRange(allocated, 0, 4 << 20);
    }

    [Fact]
    public void BlockHeadersClaimingAGigabyte_DoNotMakeTheDecoderReserveIt()
    {
        // An LZVN block that says it makes 1e9 bytes but holds garbage, and a valid stream whose LZFSE block header
        // says the same (that field is informational: the reference does not use it for LZFSE blocks).
        byte[] lzvn = Lzvn(1_000_000_000, 0xE1, 0x61, 0x1E);
        byte[] bvx2 = TestData.Vector("ref-text-4096");
        BinaryPrimitives.WriteInt32LittleEndian(bvx2.AsSpan(4), 1_000_000_000);

        long allocated = AllocatedBy(() =>
        {
            Assert.Throws<InvalidDataException>(() => Lzfse.Decode(lzvn));
            Assert.Equal(4096, Lzfse.Decode(bvx2).Length);
        });

        Assert.InRange(allocated, 0, 8 << 20);
    }

    [Fact]
    public void WithAnExpectedLength_TheOutputNeverGrowsBeyondIt()
    {
        byte[] bomb = ConstructedStreamTests.ZeroBitBlocks(7); // 1,120,000 bytes
        Lzfse.Decode(TestData.Vector("ref-text-1000")); // the thread's work tables, allocated once

        long allocated = AllocatedBy(() =>
        {
            var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(bomb, TestData.TileByteCount));
            Assert.Contains("more than 524288 bytes", error.Message);
        });

        Assert.InRange(allocated, TestData.TileByteCount, TestData.TileByteCount + (64 << 10));
    }

    [Fact]
    public void WithoutAnExpectedLength_TheOutputStopsAtTheLimit()
    {
        byte[] bomb = ConstructedStreamTests.ZeroBitBlocks(7);

        var error = Assert.Throws<InvalidDataException>(() => LzfseDecoder.Decode(bomb, -1, maxLength: 1_000_000));

        Assert.Contains("more than 1000000 bytes", error.Message);
        Assert.Equal(1 << 30, Lzfse.MaxDecodedLength);
    }

    private static long AllocatedBy(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
