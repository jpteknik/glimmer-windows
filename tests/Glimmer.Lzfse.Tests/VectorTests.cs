namespace Glimmer.Lzfse.Tests;

/// <summary>
/// Streams made by other LZFSE implementations (Vectors/SOURCES.txt). The expected outputs come from those
/// implementations, never from this codec: Apple's reference decoder, a digest published with the vector, or the
/// input the stream was made from.
/// </summary>
public class VectorTests
{
    /// <summary>Vector name, SHA-256 of the decoded data, decoded length.</summary>
    public static TheoryData<string, string, int> Streams => new()
    {
        { "ref-photo-tile", "40b0b8d88c579b344a9ce415d1157ee0cc6bc97f7e173df29dcd63c24c397947", 524288 },
        { "ref-soft-edge-tile", "fdb06dd72be9e54f41abe102e3a72f5c860aaf6a30dadae2bd996232ee7a8371", 524288 },
        { "ref-uniform-tile", "8cba75266d62a0adfc2797e994e1d25dae023727c08f5c199feb97e28375745e", 524288 },
        { "ref-transparent-tile", "07854d2fef297a06ba81685e660c332de36d5d18d546927d30daad6d7fda1541", 524288 },
        { "ref-text-100000", "51b6892f78fd6798e6059464252cd5f3417ab93816545bcf790cc1fb2b10b113", 100000 },
        { "ref-text-4096", "c3da35344f462e033bc91c8efc020ef93388a09760681561f5d7e6976b2d42df", 4096 },
        { "ref-text-4095", "f14bee4c1694fd056315598ab9bf0e82897728d5ad6de3e4618d690bc70b26f7", 4095 },
        { "ref-text-1000", "1143ce36f3b54d7892b6ac1cc1ecc0207c6b4475c1fc6c42aa05d92e0cf03838", 1000 },
        { "ref-period3-3000", "ae269f2350b4851ca21672290ebce6842f8763c25d3b772e9408f3490a229917", 3000 },
        { "ref-random-100", "c51802026baa34bbd235bea4eaf09c52b1ff33f272bfa744fe6afe2d92dff694", 100 },
        { "ref-text-7", "d79ce93816c4ba1f8832a28224f82205d76fd589587e33e092b874b856bfb24b", 7 },
        { "ref-empty", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 0 },
        { "ref-mix", "f0bde010bad1007afdf2ef6b2fc8d0ba2a2b6a6761845b5d5e7c866affc0293d", 21140 },
        { "ref-lzvn-boundaries", "8fc9bf99647175b5a677ba4ad476b0636aafef91d9d24e6cbbead148002685c7", 3641 },
        { "ref-lzfse-boundaries", "5ecfdbaa81d0b956a89932838e5df98e0e7073a8f4454276375df7f8b1322139", 86850 },
        { "conv-soft-edge-tile-bvx1", "fdb06dd72be9e54f41abe102e3a72f5c860aaf6a30dadae2bd996232ee7a8371", 524288 },
        { "lzfse-rust-raw", "1c11d03c41fad2c21df047769c0048c5afc7ab4cdaa783076780a90e8d6c9a36", 16 },
        { "lzfse-rust-vx1", "bd561b3b45536e67c5dfaaf67c6034fff986973c275841944d69a608f117cddd", 4096 },
        { "lzfse-rust-vx2", "bd561b3b45536e67c5dfaaf67c6034fff986973c275841944d69a608f117cddd", 4096 },
        { "lzfse-rust-vxn", "f2936f43d4af5ddbc77b7ea550fd2ad8ac0aeebfd48923ff278fb4c75c92f82f", 1024 },
        { "lzfse-rust-compound", "e708a31cff7e8fd91cec3f87c27ebbc2d8f5d726a774aede87ad92352675513c", 9232 },
        { "lzfse-rust-null", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 0 },
        { "lzfse-rust-html", "5912445a6d50df1079f022d7e01fa615f5d128d53bad88acbf4f49e62a7ea759", 102400 },
        { "lzfse-rust-alice29", "7467306ee0feed4971260f3c87421154a05be571d944e9cb021a5713700c38f0", 152089 },
        { "dissect-basic", "d9f5aeb06abebb3be3f38adec9a2e3b94228d52193be923eb4e24c9b56ee0930", 300 },
        { "dissect-large", "73d3dd96ca2e2f0144a117019256d770ee7c6febeaee09b24956c723ae22b529", 14047 },
        { "hand-lzvn-every-opcode", "c7ae16bba8dcafff6b6b9c19f893db3894343c7e14320c4c67805affdf52142a", 405 },
        { "hand-lzvn-after-stored-block", "334524710bb698b158d80784a4415dea74d32aec72ca1c112f1e9a7a057047da", 82 },
        { "hand-mixed-blocks-lzvn-back-reference", "57288a218de0577a03eaffce6af4b87e6e09286c3132eb6ad7fadbad94168298", 13274 },
        { "hand-zero-bit-block", "713a082bca778058fa655d9d51ec75beac001b40a0a84810556d93157462e5a3", 160000 },
    };

    /// <summary>Vectors that Apple's reference encoder produced (checked by re-encoding with it).</summary>
    public static TheoryData<string> ReferenceEncoderStreams => new()
    {
        "ref-photo-tile", "ref-soft-edge-tile", "ref-uniform-tile", "ref-transparent-tile", "ref-text-100000",
        "ref-text-4096", "ref-text-4095", "ref-text-1000", "ref-period3-3000", "ref-random-100", "ref-text-7",
        "ref-empty", "ref-mix", "ref-lzvn-boundaries", "ref-lzfse-boundaries",
        "lzfse-rust-raw", "lzfse-rust-vx2", "lzfse-rust-vxn", "lzfse-rust-html",
        "lzfse-rust-alice29", "dissect-basic", "dissect-large",
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void Decode_ReproducesTheOtherImplementationsOutput(string name, string sha256, int length)
    {
        byte[] decoded = Lzfse.Decode(TestData.Vector(name));

        Assert.Equal(length, decoded.Length);
        Assert.Equal(sha256, TestData.Sha256(decoded));
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void DecodeWithExpectedLength_AcceptsOnlyTheExactLength(string name, string sha256, int length)
    {
        byte[] stream = TestData.Vector(name);

        Assert.Equal(sha256, TestData.Sha256(Lzfse.Decode(stream, length)));
        Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream, length + 1));
        if (length > 0) Assert.Throws<InvalidDataException>(() => Lzfse.Decode(stream, length - 1));
    }

    [Theory]
    [MemberData(nameof(ReferenceEncoderStreams))]
    public void Encode_ReproducesTheReferenceEncoderByteForByte(string name)
    {
        byte[] stream = TestData.Vector(name);

        Assert.Equal(stream, Lzfse.Encode(Lzfse.Decode(stream)));
    }

    [Fact]
    public void Vectors_CoverEveryBlockKind()
    {
        // The decoder tests above are only as good as the block kinds the vectors contain.
        var kinds = Streams.Select(row => (string)row[0])
            .SelectMany(name => TestData.BlockMagics(TestData.Vector(name)))
            .ToHashSet();

        Assert.Equal("bvx$ bvx- bvx1 bvx2 bvxn", string.Join(' ', kinds.Order(StringComparer.Ordinal)));
        Assert.Equal("bvx- bvx1 bvx2 bvxn bvx$",
            string.Join(' ', TestData.BlockMagics(TestData.Vector("lzfse-rust-compound"))));
        Assert.Equal("bvx1 bvx1 bvx$",
            string.Join(' ', TestData.BlockMagics(TestData.Vector("conv-soft-edge-tile-bvx1"))));
    }

    [Fact]
    public void PhotoTile_IsSeveralBlocksWithMatchesAcrossThem()
    {
        // A real tile does not fit one LZFSE block (at most 10000 matches and 40000 literals), so its later blocks
        // copy from earlier ones; decoding each block on its own would fail.
        byte[] stream = TestData.Vector("ref-photo-tile");
        var blocks = TestData.Blocks(stream);
        Assert.Equal("bvx2 bvx2 bvx2 bvx2 bvx2 bvx2 bvx$", string.Join(' ', blocks.Select(b => b.Magic)));

        // The second block on its own refers to data before its start.
        byte[] second = [.. stream.AsSpan(blocks[1].Offset, blocks[1].Length), .. TestData.EndOfStream];
        var error = Assert.Throws<InvalidDataException>(() => Lzfse.Decode(second));
        Assert.Contains("match distance", error.Message);

        byte[] tile = Lzfse.Decode(stream, TestData.TileByteCount);

        Assert.Equal(stream.Length, Lzfse.Encode(tile).Length);
        Assert.InRange((double)stream.Length / tile.Length, 0.20, 0.21); // as compressed by the reference encoder
    }

    [Fact]
    public void Bvx1AndBvx2Headers_OfTheSameBlocksDecodeAlike()
    {
        Assert.Equal(Lzfse.Decode(TestData.Vector("ref-soft-edge-tile")),
            Lzfse.Decode(TestData.Vector("conv-soft-edge-tile-bvx1")));
    }
}
