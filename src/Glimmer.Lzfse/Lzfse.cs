namespace Glimmer.Lzfse;

/// <summary>
/// LZFSE, the compression format of Apple's Compression framework. The Mac version of Glimmer stores every tile of a
/// .glimmer document as LZFSE-compressed Float16 pixels (<c>NSData.compressed(using: .lzfse)</c>), so reading and
/// writing documents that a Mac can open needs a compatible codec.
/// </summary>
/// <remarks>
/// <para>The stream is a sequence of blocks, each starting with a four-byte magic: <c>bvx2</c> (LZFSE, compressed
/// headers), <c>bvx1</c> (LZFSE, uncompressed headers), <c>bvxn</c> (LZVN), <c>bvx-</c> (stored) and <c>bvx$</c> (end
/// of stream). The codec is a port of Apple's reference implementation, https://github.com/lzfse/lzfse
/// (BSD-3-Clause); the encoder produces the same bytes as the reference's <c>lzfse_encode_buffer</c>.</para>
/// <para>All methods are thread-safe. Each thread keeps its own work tables, so many tiles can be coded in
/// parallel.</para>
/// </remarks>
public static class Lzfse
{
    internal const uint EndOfStreamMagic = LzfseConstants.EndOfStreamBlockMagic;     // "bvx$"
    internal const uint StoredMagic = LzfseConstants.UncompressedBlockMagic;         // "bvx-"
    internal const uint LzvnMagic = LzfseConstants.CompressedLzvnBlockMagic;         // "bvxn"
    internal const uint V1Magic = LzfseConstants.CompressedV1BlockMagic;             // "bvx1"
    internal const uint V2Magic = LzfseConstants.CompressedV2BlockMagic;             // "bvx2"

    /// <summary>
    /// The most bytes <see cref="Decode(ReadOnlySpan{byte})"/> produces. LZFSE can expand a few bytes into megabytes,
    /// so without a known length a limit keeps a damaged or hostile stream from exhausting memory.
    /// </summary>
    internal const int MaxDecodedLength = LzfseDecoder.DefaultMaxOutputLength;

    /// <summary>Decompresses a complete LZFSE stream (anything after its end-of-stream block is ignored).</summary>
    /// <exception cref="InvalidDataException">
    /// The data is not a valid LZFSE stream, or decodes to more than 1 GiB.
    /// </exception>
    public static byte[] Decode(ReadOnlySpan<byte> compressed) => LzfseDecoder.Decode(compressed, -1);

    /// <summary>
    /// Decompresses a complete LZFSE stream whose decoded size is known, as the tile format's is (256 × 256 × 8 bytes).
    /// Never produces or allocates more than <paramref name="expectedLength"/> bytes of output.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedLength"/> is negative.</exception>
    /// <exception cref="InvalidDataException">The data is not valid or does not decode to exactly that many bytes.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> compressed, int expectedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        return LzfseDecoder.Decode(compressed, expectedLength);
    }

    /// <summary>
    /// Compresses <paramref name="data"/> into an LZFSE stream that Apple's decoder accepts: LZVN below 4 KB, LZFSE
    /// above, and stored when neither makes it smaller. The result is never more than 12 bytes longer than the input.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The input is too large to fit in an array once encoded.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length > LzfseEncoder.MaxInputLength)
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "The data is too large to compress.");
        return LzfseEncoder.EncodeBuffer(data);
    }
}
