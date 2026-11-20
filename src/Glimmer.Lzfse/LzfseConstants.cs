// Ported from lzfse_internal.h, lzfse_tunables.h and lzfse_encode_tables.h of the LZFSE reference implementation,
// https://github.com/lzfse/lzfse (commit e634ca58b4821d9f3d560cdc6df5dec02ffc93fd).
//
// Copyright (c) 2015-2016, Apple Inc. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//
// 1.  Redistributions of source code must retain the above copyright notice,
//     this list of conditions and the following disclaimer.
//
// 2.  Redistributions in binary form must reproduce the above copyright notice,
//     this list of conditions and the following disclaimer in the documentation
//     and/or other materials provided with the distribution.
//
// 3.  Neither the name of the copyright holder(s) nor the names of any contributors
//     may be used to endorse or promote products derived from this software without
//     specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
// OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT
// SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED
// TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR
// BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
// IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY
// OF SUCH DAMAGE.

namespace Glimmer.Lzfse;

/// <summary>Format constants and the L, M, D value tables shared by the LZFSE encoder and decoder.</summary>
/// <remarks>
/// Unlike the tunable parameters, you probably should not modify the values defined here. Doing so will either break
/// the compressor, or result in a compressed data format that is incompatible.
/// </remarks>
internal static class LzfseConstants
{
    // MARK: - Tunables (lzfse_tunables.h)

    //  Parameters controlling details of the LZ-style match search. These values may be modified to fine tune
    //  compression ratio vs. encoding speed, while keeping the compressed format compatible with LZFSE. Note that
    //  modifying them will also change the amount of work space required by the encoder. The values here are those
    //  used in the compression library on iOS and OS X.

    /// <summary>
    /// Number of bits for hash function to produce. Should be in the range [10, 16]. Larger values reduce the number of
    /// false-positive found during the match search, and expand the history table, which may allow additional matches
    /// to be found, generally improving the achieved compression ratio.
    /// </summary>
    internal const int EncodeHashBits = 14;

    /// <summary>Number of positions to store for each line in the history table. May be either 4 or 8.</summary>
    internal const int EncodeHashWidth = 4;

    /// <summary>
    /// Match length in bytes to cause immediate emission. Generally speaking, LZFSE maintains multiple candidate
    /// matches and waits to decide which match to emit until more information is available. When a match exceeds this
    /// threshold, it is emitted immediately.
    /// </summary>
    internal const int EncodeGoodMatch = 40;

    /// <summary>
    /// When the source buffer is very small, LZFSE doesn't compress as well as some simpler algorithms. To maintain
    /// reasonable compression for these cases, we transition to use LZVN instead if the size of the source buffer is
    /// below this threshold.
    /// </summary>
    internal const int EncodeLzvnThreshold = 4096;

    // MARK: - Format constants (lzfse_internal.h)

    //  Throughout LZFSE we refer to "L", "M" and "D"; these will always appear as a triplet, and represent a "usual"
    //  LZ-style literal and match pair. "L" is the number of literal bytes, "M" is the number of match bytes, and "D"
    //  is the match "distance"; the distance in bytes between the current pointer and the start of the match.
    internal const int EncodeHashValues = 1 << EncodeHashBits;
    internal const int EncodeLSymbols = 20;
    internal const int EncodeMSymbols = 20;
    internal const int EncodeDSymbols = 64;
    internal const int EncodeLiteralSymbols = 256;
    internal const int EncodeLStates = 64;
    internal const int EncodeMStates = 64;
    internal const int EncodeDStates = 256;
    internal const int EncodeLiteralStates = 1024;
    internal const int MatchesPerBlock = 10000;
    internal const int LiteralsPerBlock = 4 * MatchesPerBlock;

    /// <summary>Total number of entries in the four frequency tables of a block header (L, M, D, literal).</summary>
    internal const int FreqTableEntries = EncodeLSymbols + EncodeMSymbols + EncodeDSymbols + EncodeLiteralSymbols;

    //  Offsets of the four tables inside the concatenated frequency array (same order as in the block header).
    internal const int LFreqOffset = 0;
    internal const int MFreqOffset = LFreqOffset + EncodeLSymbols;
    internal const int DFreqOffset = MFreqOffset + EncodeMSymbols;
    internal const int LiteralFreqOffset = DFreqOffset + EncodeDSymbols;

    //  Largest encodable L (literal length), M (match length) and D (match distance) values.
    internal const int EncodeMaxLValue = 315;
    internal const int EncodeMaxMValue = 2359;
    internal const int EncodeMaxDValue = 262139;

    /// <summary>
    /// We need to limit forward match length to make sure it won't split into a too large number of LMD. The limit
    /// itself is quite large, so it doesn't really impact compression ratio. (lzfse_encode_base.c)
    /// </summary>
    internal const int EncodeMaxMatchLength = 100 * EncodeMaxMValue;

    // MARK: - Block header objects

    internal const uint NoBlockMagic = 0x00000000;             // 0    (invalid)
    internal const uint EndOfStreamBlockMagic = 0x24787662;    // bvx$ (end of stream)
    internal const uint UncompressedBlockMagic = 0x2d787662;   // bvx- (raw data)
    internal const uint CompressedV1BlockMagic = 0x31787662;   // bvx1 (lzfse compressed, uncompressed tables)
    internal const uint CompressedV2BlockMagic = 0x32787662;   // bvx2 (lzfse compressed, compressed tables)
    internal const uint CompressedLzvnBlockMagic = 0x6e787662; // bvxn (lzvn compressed)

    /// <summary><c>sizeof(uncompressed_block_header)</c>: magic, n_raw_bytes.</summary>
    internal const int UncompressedBlockHeaderSize = 8;

    /// <summary><c>sizeof(lzvn_compressed_block_header)</c>: magic, n_raw_bytes, n_payload_bytes.</summary>
    internal const int LzvnBlockHeaderSize = 12;

    /// <summary>
    /// <c>sizeof(lzfse_compressed_block_header_v1)</c> as stored in a stream: 770 bytes of fields plus 2 bytes of
    /// tail padding (the C struct is 4-byte aligned).
    /// </summary>
    internal const int V1HeaderSize = 772;

    //  Field offsets of lzfse_compressed_block_header_v1 (little-endian, as laid out by the C compiler).
    internal const int V1NRawBytesOffset = 4;
    internal const int V1NPayloadBytesOffset = 8;
    internal const int V1NLiteralsOffset = 12;
    internal const int V1NMatchesOffset = 16;
    internal const int V1NLiteralPayloadBytesOffset = 20;
    internal const int V1NLmdPayloadBytesOffset = 24;
    internal const int V1LiteralBitsOffset = 28;
    internal const int V1LiteralStateOffset = 32;
    internal const int V1LmdBitsOffset = 40;
    internal const int V1LStateOffset = 44;
    internal const int V1MStateOffset = 46;
    internal const int V1DStateOffset = 48;
    internal const int V1FreqOffset = 50;

    /// <summary><c>offsetof(lzfse_compressed_block_header_v2, freq)</c>: magic, n_raw_bytes, packed_fields[3].</summary>
    internal const int V2HeaderFixedSize = 32;

    /// <summary><c>sizeof(lzfse_compressed_block_header_v2)</c>: the fixed part plus all freq values on 16 bits.</summary>
    internal const int V2HeaderMaxSize = V2HeaderFixedSize + 2 * FreqTableEntries;

    // MARK: - LZVN encode/decode interfaces

    /// <summary>Minimum source buffer size for compression. Smaller buffers will not be compressed.</summary>
    internal const int LzvnEncodeMinSrcSize = 8;

    /// <summary>Minimum destination buffer size for compression. No compression will take place if smaller.</summary>
    internal const int LzvnEncodeMinDstSize = 8;

    // MARK: - L, M, D encoding constants for LZFSE

    //  The L, M, D data streams are all encoded as a "base" value, which is FSE-encoded, and an "extra bits" value,
    //  which is the difference between value and base, and is simply represented as a raw bit value (because it is the
    //  low-order bits of a larger number, not much entropy can be extracted from these bits by more complex encoding
    //  schemes). The following tables represent the number of low-order bits to encode separately and the base values
    //  for each of L, M, and D.

    internal static ReadOnlySpan<byte> LExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8,
    ];

    internal static ReadOnlySpan<int> LBaseValue =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60,
    ];

    internal static ReadOnlySpan<byte> MExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11,
    ];

    internal static ReadOnlySpan<int> MBaseValue =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312,
    ];

    internal static ReadOnlySpan<byte> DExtraBits =>
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
        4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
        8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11,
        12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
    ];

    internal static ReadOnlySpan<int> DBaseValue =>
    [
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16,
        20, 24, 28, 36, 44, 52, 60, 76, 92, 108,
        124, 156, 188, 220, 252, 316, 380, 444, 508, 636,
        764, 892, 1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580,
        4092, 5116, 6140, 7164, 8188, 10236, 12284, 14332, 16380, 20476,
        24572, 28668, 32764, 40956, 49148, 57340, 65532, 81916, 98300, 114684,
        131068, 163836, 196604, 229372,
    ];

    // MARK: - Value-to-symbol tables (lzfse_encode_tables.h)

    //  The reference spells these tables out; here they are computed from the base values above, which defines the
    //  same mapping: the symbol of a value is the one whose range [base, base + 2^extra_bits) contains it. (The tests
    //  check that property for every encodable value.)

    private static readonly byte[] LSymbolOfValue = BuildSymbolTable(LBaseValue, EncodeMaxLValue + 1);
    private static readonly byte[] MSymbolOfValue = BuildSymbolTable(MBaseValue, EncodeMaxMValue + 1);

    //  D uses the reference's compact form: 4 ranges of 64 buckets, with the bucket width growing with the range.
    private static readonly byte[] DSymbolOfBucket = BuildDSymbolTable();

    internal static byte LBaseFromValue(int value) => LSymbolOfValue[value];

    internal static byte MBaseFromValue(int value) => MSymbolOfValue[value];

    internal static byte DBaseFromValue(int value)
    {
        int index;
        if (value >= 0 && value < 60) index = value;
        else if (value >= 60 && value < 1020) index = ((value - 60) >> 4) + 64;
        else if (value >= 1020 && value < 16380) index = ((value - 1020) >> 8) + 128;
        else if (value >= 16380 && value < 262140) index = ((value - 16380) >> 12) + 192;
        else index = 0;
        return DSymbolOfBucket[index & 255];
    }

    private static byte[] BuildSymbolTable(ReadOnlySpan<int> baseValue, int count)
    {
        var table = new byte[count];
        int symbol = 0;
        for (int value = 0; value < count; value++)
        {
            while (symbol + 1 < baseValue.Length && baseValue[symbol + 1] <= value) symbol++;
            table[value] = (byte)symbol;
        }
        return table;
    }

    private static byte[] BuildDSymbolTable()
    {
        ReadOnlySpan<int> baseValue = DBaseValue;
        var table = new byte[256];
        for (int index = 0; index < 256; index++)
        {
            // The smallest value of each bucket. The last four buckets of the last range lie past the largest
            // encodable distance; the reference maps them to symbol 0.
            int value = index switch
            {
                < 64 => index,
                < 128 => 60 + ((index - 64) << 4),
                < 192 => 1020 + ((index - 128) << 8),
                _ => 16380 + ((index - 192) << 12),
            };
            if (value > EncodeMaxDValue) continue;
            int symbol = 0;
            while (symbol + 1 < baseValue.Length && baseValue[symbol + 1] <= value) symbol++;
            table[index] = (byte)symbol;
        }
        return table;
    }
}
