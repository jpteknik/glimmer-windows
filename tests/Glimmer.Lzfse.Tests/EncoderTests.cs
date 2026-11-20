namespace Glimmer.Lzfse.Tests;

/// <summary>
/// The encoder. That its output is what Apple's reference encoder writes, byte for byte, is shown by
/// <see cref="VectorTests.Encode_ReproducesTheReferenceEncoderByteForByte"/>; these tests cover round trips over
/// many sizes and kinds of data, the choice of block kind, and use from many threads at once.
/// </summary>
public class EncoderTests
{
    private static void AssertRoundTrips(byte[] data)
    {
        byte[] encoded = Lzfse.Encode(data);

        Assert.InRange(encoded.Length, 12, data.Length + 12);
        Assert.Equal(data, Lzfse.Decode(encoded, data.Length));
    }

    [Fact]
    public void EverySizeUpTo300Bytes_RoundTrips()
    {
        for (int n = 0; n <= 300; n++)
        {
            AssertRoundTrips(TestData.RandomBytes(n, (ulong)n));
            AssertRoundTrips(TestData.Text(n, (ulong)n));
            AssertRoundTrips(TestData.Repeated(n, 1 + n % 9, (ulong)n));
            AssertRoundTrips(new byte[n]);
        }
    }

    [Theory]
    [InlineData(4095)]   // largest input encoded with LZVN
    [InlineData(4096)]   // smallest input encoded with LZFSE
    [InlineData(40000)]  // literals per LZFSE block
    [InlineData(65536)]  // largest LZVN distance, and beyond
    [InlineData(262144)] // beyond the largest LZFSE distance (262139)
    [InlineData(1_500_000)]
    public void SizesAroundTheFormatsLimits_RoundTrip(int n)
    {
        AssertRoundTrips(TestData.RandomBytes(n, 7));
        AssertRoundTrips(TestData.Text(n, 7));
        AssertRoundTrips(TestData.Mixed(n, 7));
        AssertRoundTrips(TestData.Repeated(n, 3, 7));
        AssertRoundTrips(TestData.Repeated(n, 1000, 7));
    }

    [Fact]
    public void TilesOfFloat16Pixels_RoundTripAndShrink()
    {
        byte[] photoLike = TestData.PhotoLikeTile(3);
        byte[] uniform = Lzfse.Decode(TestData.Vector("ref-uniform-tile"));

        AssertRoundTrips(photoLike);
        AssertRoundTrips(uniform);
        Assert.InRange(Lzfse.Encode(photoLike).Length, 1, photoLike.Length * 3 / 4);
        Assert.InRange(Lzfse.Encode(uniform).Length, 1, 1000);
    }

    [Theory]
    [InlineData(0, "bvx-")]     // nothing to compress
    [InlineData(7, "bvx-")]     // below LZVN's 8-byte minimum
    [InlineData(1000, "bvxn")]  // small and compressible: LZVN
    [InlineData(4095, "bvxn")]
    [InlineData(4096, "bvx2")]  // from 4 KB: LZFSE
    [InlineData(100000, "bvx2")]
    public void BlockKind_IsTheOneTheReferenceChooses(int n, string magic)
    {
        Assert.Equal($"{magic} bvx$",
            string.Join(' ', TestData.BlockMagics(Lzfse.Encode(TestData.Text(n, 1))).Distinct()));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100000)]
    public void IncompressibleData_IsStoredWith12BytesOfOverhead(int n)
    {
        byte[] encoded = Lzfse.Encode(TestData.RandomBytes(n, 99));

        Assert.Equal("bvx- bvx$", string.Join(' ', TestData.BlockMagics(encoded)));
        Assert.Equal(n + 12, encoded.Length);
    }

    [Fact]
    public void Encode_DependsOnlyOnItsInput()
    {
        // Each thread reuses its work tables; nothing of one input may leak into the next.
        byte[] a = TestData.Mixed(300000, 1);
        byte[] b = TestData.Text(5000, 2);
        byte[] first = Lzfse.Encode(a);
        Lzfse.Encode(b);
        Lzfse.Encode(TestData.RandomBytes(3000, 3));

        Assert.Equal(first, Lzfse.Encode(a));
    }

    [Fact]
    public void ManyThreadsAtOnce_GetTheSameResultsAsOne()
    {
        string[] names = ["ref-photo-tile", "ref-soft-edge-tile", "ref-text-1000", "lzfse-rust-compound",
            "hand-lzvn-every-opcode", "conv-soft-edge-tile-bvx1", "ref-uniform-tile"];
        byte[][] streams = names.Select(TestData.Vector).ToArray();
        byte[][] decoded = streams.Select(s => Lzfse.Decode(s)).ToArray();
        byte[][] encoded = decoded.Select(d => Lzfse.Encode(d)).ToArray();
        int failures = 0;

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            int k = i % names.Length;
            if (!Lzfse.Decode(streams[k]).AsSpan().SequenceEqual(decoded[k])) Interlocked.Increment(ref failures);
            if (!Lzfse.Encode(decoded[k]).AsSpan().SequenceEqual(encoded[k])) Interlocked.Increment(ref failures);
        });

        Assert.Equal(0, failures);
    }
}
