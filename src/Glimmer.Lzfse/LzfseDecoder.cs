// Ported from lzfse_decode_base.c and lzfse_decode.c of the LZFSE reference implementation,
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

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using static Glimmer.Lzfse.LzfseConstants;

namespace Glimmer.Lzfse;

/// <summary>
/// Decodes a complete LZFSE stream held in memory: the reference's <c>lzfse_decode_buffer</c>, with its output buffer
/// managed here instead of by the caller.
/// </summary>
/// <remarks>
/// <para>The reference decoder is resumable (it returns "destination full" and continues where it left off when given
/// more room). Here the whole input is always present and the output buffer grows as needed, so that machinery is
/// gone; every check that guards memory or rejects a malformed stream is kept, in the same order.</para>
/// <para>Differences from the reference, all of which only reject streams that no encoder produces:</para>
/// <list type="bullet">
/// <item>A triple may only consume literals that the block decoded (the reference allows reading up to 64 bytes of
/// scratch past them).</item>
/// <item>An LZVN match that reuses the previous distance when there is none (distance 0) is an error (the reference
/// copies the destination onto itself).</item>
/// <item>Where the reference would report "destination full" for a block that can never complete (an LZVN block that
/// stops before its end), this is an error rather than a request for more room.</item>
/// </list>
/// <para>Every access is bounds-checked. Anything that would read or write out of range, or produce more output than
/// allowed, is reported as <see cref="InvalidDataException"/>.</para>
/// </remarks>
internal static class LzfseDecoder
{
    /// <summary>
    /// The largest output produced when the caller does not say how long it is. LZFSE can expand a few bytes into
    /// megabytes, so a limit is needed to keep a hostile stream from exhausting memory.
    /// </summary>
    internal const int DefaultMaxOutputLength = 1 << 30;

    /// <summary>Decodes <paramref name="src"/>.</summary>
    /// <param name="expectedLength">The exact decoded length, or -1 when unknown.</param>
    /// <param name="maxLength">With an unknown length: the most output allowed.</param>
    internal static byte[] Decode(ReadOnlySpan<byte> src, int expectedLength, int maxLength = DefaultMaxOutputLength)
    {
        DecodeOutput output = expectedLength >= 0
            ? DecodeOutput.Exact(expectedLength)
            : DecodeOutput.Growable(InitialCapacity(src, maxLength), maxLength);
        try
        {
            DecodeStream(src, output, LzfseDecoderScratch.ForCurrentThread().Reset());
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException)
        {
            // Every condition is checked explicitly before memory is touched; this only turns a missed one into the
            // documented exception instead of an unrelated one.
            throw new InvalidDataException("LZFSE stream is corrupt.", e);
        }

        if (expectedLength >= 0 && output.Length != expectedLength)
            throw new InvalidDataException(
                $"LZFSE stream decodes to {output.Length} bytes, expected {expectedLength}.");
        return output.ToArray();
    }

    private static void DecodeStream(ReadOnlySpan<byte> src, DecodeOutput output, LzfseDecoderScratch scratch)
    {
        int pos = 0;
        while (true)
        {
            // We need at least 4 bytes of magic number to identify next block
            if (src.Length - pos < 4) throw Truncated();
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
            switch (magic)
            {
                case EndOfStreamBlockMagic:
                    return; // done (anything after the end-of-stream block is ignored, as by the reference)
                case UncompressedBlockMagic:
                    pos = DecodeUncompressedBlock(src, pos, output);
                    break;
                case CompressedLzvnBlockMagic:
                    pos = DecodeLzvnBlock(src, pos, output);
                    break;
                case CompressedV1BlockMagic:
                case CompressedV2BlockMagic:
                    pos = DecodeLzfseBlock(src, pos, magic, output, scratch);
                    break;
                default:
                    // Here we have an invalid magic number
                    throw pos == 0
                        ? new InvalidDataException("Not an LZFSE stream.")
                        : Corrupt("unknown block type");
            }
        }
    }

    // MARK: - Uncompressed and LZVN blocks

    private static int DecodeUncompressedBlock(ReadOnlySpan<byte> src, int pos, DecodeOutput output)
    {
        if (src.Length - pos < UncompressedBlockHeaderSize) throw Truncated();
        uint nRawBytes = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
        pos += UncompressedBlockHeaderSize;
        if (nRawBytes > (uint)(src.Length - pos)) throw Truncated();
        int n = (int)nRawBytes;
        output.Reserve(n);
        src.Slice(pos, n).CopyTo(output.Buffer.AsSpan(output.Length, n));
        output.Length += n;
        return pos + n;
    }

    private static int DecodeLzvnBlock(ReadOnlySpan<byte> src, int pos, DecodeOutput output)
    {
        if (src.Length - pos < LzvnBlockHeaderSize) throw Truncated();
        uint nRawBytes = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
        uint nPayloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 8, 4));
        pos += LzvnBlockHeaderSize;
        if (nPayloadBytes > (uint)(src.Length - pos)) throw Truncated();
        // A block with nothing to produce never reaches its end-of-stream opcode, which the reference treats as an
        // error.
        if (nRawBytes == 0) throw Corrupt("empty LZVN block");
        if (nRawBytes > (uint)(output.Limit - output.Length)) throw output.TooLong();
        LzvnDecoder.DecodeBlock(src.Slice(pos, (int)nPayloadBytes), output, (int)nRawBytes);
        return pos + (int)nPayloadBytes;
    }

    // MARK: - LZFSE blocks

    /// <summary>The fields of lzfse_compressed_block_header_v1 except the frequency tables.</summary>
    private struct BlockHeader
    {
        internal uint NLiterals;
        internal uint NMatches;
        internal uint NLiteralPayloadBytes;
        internal uint NLmdPayloadBytes;
        internal int LiteralBits;
        internal int LiteralState0, LiteralState1, LiteralState2, LiteralState3;
        internal int LmdBits;
        internal int LState, MState, DState;
    }

    /// <summary>Decode an entry value from next bits of stream. Return value, and set nbits to the number of bits to
    /// consume (starting with LSB).</summary>
    private static int DecodeV1FreqValue(uint bits, out int nbits)
    {
        ReadOnlySpan<sbyte> lzfseFreqNbitsTable =
        [
            2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
            2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
        ];
        ReadOnlySpan<sbyte> lzfseFreqValueTable =
        [
            0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1,
            0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1,
        ];

        uint b = bits & 31; // lower 5 bits
        int n = lzfseFreqNbitsTable[(int)b];
        nbits = n;

        // Special cases for > 5 bits encoding
        if (n == 8) return 8 + (int)((bits >> 4) & 0xf);
        if (n == 14) return 24 + (int)((bits >> 4) & 0x3ff);

        // <= 5 bits encoding from table
        return lzfseFreqValueTable[(int)b];
    }

    /// <summary>Extracts up to 32 bits from a 64-bit field beginning at offset, and zero-extends them.</summary>
    private static uint GetField(ulong v, int offset, int nbits) =>
        nbits == 32 ? (uint)(v >> offset) : (uint)((v >> offset) & ((1UL << nbits) - 1));

    /// <summary>
    /// Decode all fields from a lzfse_compressed_block_header_v2 to the v1 form. <paramref name="header"/> is the
    /// whole stored header (header_size bytes).
    /// </summary>
    /// <returns>false on failure.</returns>
    private static bool DecodeV2Header(ReadOnlySpan<byte> header, ref BlockHeader h, Span<ushort> freq)
    {
        ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8, 8));
        ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16, 8));
        ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24, 8));

        // Literal state
        h.NLiterals = GetField(v0, 0, 20);
        h.NLiteralPayloadBytes = GetField(v0, 20, 20);
        h.LiteralBits = (int)GetField(v0, 60, 3) - 7;
        h.LiteralState0 = (int)GetField(v1, 0, 10);
        h.LiteralState1 = (int)GetField(v1, 10, 10);
        h.LiteralState2 = (int)GetField(v1, 20, 10);
        h.LiteralState3 = (int)GetField(v1, 30, 10);

        // L,M,D state
        h.NMatches = GetField(v0, 40, 20);
        h.NLmdPayloadBytes = GetField(v1, 40, 20);
        h.LmdBits = (int)GetField(v1, 60, 3) - 7;
        h.LState = (int)GetField(v2, 32, 10);
        h.MState = (int)GetField(v2, 42, 10);
        h.DState = (int)GetField(v2, 52, 10);

        // Freq tables
        freq.Clear();
        int src = V2HeaderFixedSize;
        int srcEnd = (int)GetField(v2, 0, 32); // first byte after header (== header.Length)
        uint accum = 0;
        int accumNbits = 0;

        // No freq tables?
        if (srcEnd == src) return true; // OK, freq tables were omitted
        if (srcEnd < src) return false;

        for (int i = 0; i < FreqTableEntries; i++)
        {
            // Refill accum, one byte at a time, until we reach end of header, or accum is full
            while (src < srcEnd && accumNbits + 8 <= 32)
            {
                accum |= (uint)header[src] << accumNbits;
                accumNbits += 8;
                src++;
            }

            // Decode and store value
            freq[i] = (ushort)DecodeV1FreqValue(accum, out int nbits);

            if (nbits > accumNbits) return false; // failed

            // Consume nbits bits
            accum >>= nbits;
            accumNbits -= nbits;
        }

        // we need to end up exactly at the end of header, with less than 8 bits in accumulator
        return accumNbits < 8 && src == srcEnd;
    }

    /// <summary>Read a stored lzfse_compressed_block_header_v1.</summary>
    private static void ReadV1Header(ReadOnlySpan<byte> header, ref BlockHeader h, Span<ushort> freq)
    {
        h.NLiterals = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(V1NLiteralsOffset, 4));
        h.NMatches = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(V1NMatchesOffset, 4));
        h.NLiteralPayloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(V1NLiteralPayloadBytesOffset, 4));
        h.NLmdPayloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(V1NLmdPayloadBytesOffset, 4));
        h.LiteralBits = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(V1LiteralBitsOffset, 4));
        h.LiteralState0 = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1LiteralStateOffset, 2));
        h.LiteralState1 = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1LiteralStateOffset + 2, 2));
        h.LiteralState2 = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1LiteralStateOffset + 4, 2));
        h.LiteralState3 = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1LiteralStateOffset + 6, 2));
        h.LmdBits = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(V1LmdBitsOffset, 4));
        h.LState = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1LStateOffset, 2));
        h.MState = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1MStateOffset, 2));
        h.DState = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1DStateOffset, 2));
        for (int i = 0; i < FreqTableEntries; i++)
            freq[i] = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(V1FreqOffset + 2 * i, 2));
    }

    /// <summary>Sanity checks on the values of a block header (lzfse_check_block_header_v1).</summary>
    private static bool CheckBlockHeader(in BlockHeader h, ReadOnlySpan<ushort> freq) =>
        h.NLiterals <= LiteralsPerBlock
        && h.NMatches <= MatchesPerBlock
        && h.LiteralState0 < EncodeLiteralStates
        && h.LiteralState1 < EncodeLiteralStates
        && h.LiteralState2 < EncodeLiteralStates
        && h.LiteralState3 < EncodeLiteralStates
        && h.LState < EncodeLStates
        && h.MState < EncodeMStates
        && h.DState < EncodeDStates
        && Fse.CheckFreq(freq.Slice(LFreqOffset, EncodeLSymbols), EncodeLStates)
        && Fse.CheckFreq(freq.Slice(MFreqOffset, EncodeMSymbols), EncodeMStates)
        && Fse.CheckFreq(freq.Slice(DFreqOffset, EncodeDSymbols), EncodeDStates)
        && Fse.CheckFreq(freq.Slice(LiteralFreqOffset, EncodeLiteralSymbols), EncodeLiteralStates);

    private static int DecodeLzfseBlock(ReadOnlySpan<byte> src, int pos, uint magic, DecodeOutput output,
        LzfseDecoderScratch scratch)
    {
        BlockHeader h = default;
        Span<ushort> freq = scratch.Freq;
        int headerSize;

        // Decode compressed headers
        if (magic == CompressedV2BlockMagic)
        {
            // Check we have the fixed part of the structure
            if (src.Length - pos < V2HeaderFixedSize) throw Truncated();

            // Get size, and check we have the entire structure
            uint size = (uint)BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(pos + 24, 8));
            if (size > (uint)(src.Length - pos)) throw Truncated();
            headerSize = (int)size;
            if (headerSize < V2HeaderFixedSize || !DecodeV2Header(src.Slice(pos, headerSize), ref h, freq))
                throw Corrupt("invalid block header");
        }
        else
        {
            if (src.Length - pos < V1HeaderSize) throw Truncated();
            ReadV1Header(src.Slice(pos, V1HeaderSize), ref h, freq);
            headerSize = V1HeaderSize;
        }

        // We require the header + entire encoded block to be present in SRC.
        long blockEnd = (long)pos + headerSize + h.NLiteralPayloadBytes + h.NLmdPayloadBytes;
        if (blockEnd > src.Length) throw Truncated();

        // Sanity checks
        if (!CheckBlockHeader(in h, freq)) throw Corrupt("invalid block header");

        // Setup state for compressed V1 block from header. As in the reference, the tables live for the whole stream:
        // each block writes the states its frequencies cover and leaves the rest as the previous block left them. So a
        // block whose v2 header omits the frequency tables reuses the previous block's tables.
        int[] literalDecoder = scratch.LiteralDecoder;
        FseValueDecoderEntry[] lDecoder = scratch.LDecoder, mDecoder = scratch.MDecoder, dDecoder = scratch.DDecoder;
        Fse.InitDecoderTable(EncodeLiteralStates, EncodeLiteralSymbols,
            freq.Slice(LiteralFreqOffset, EncodeLiteralSymbols), literalDecoder);
        Fse.InitValueDecoderTable(EncodeLStates, EncodeLSymbols, freq.Slice(LFreqOffset, EncodeLSymbols),
            LExtraBits, LBaseValue, lDecoder);
        Fse.InitValueDecoderTable(EncodeMStates, EncodeMSymbols, freq.Slice(MFreqOffset, EncodeMSymbols),
            MExtraBits, MBaseValue, mDecoder);
        Fse.InitValueDecoderTable(EncodeDStates, EncodeDSymbols, freq.Slice(DFreqOffset, EncodeDSymbols),
            DExtraBits, DBaseValue, dDecoder);

        // Decode literals. The stream is read backwards from the end of the literal payload; like the reference, the
        // reader may look at bytes before the payload (it prefetches whole bytes), but never before the input.
        byte[] literals = scratch.Literals;
        int literalPayloadEnd = pos + headerSize + (int)h.NLiteralPayloadBytes;
        int literalCount = ((int)h.NLiterals + 3) & ~3; // decoded 4 at a time
        {
            FseInStream @in = default;
            int buf = literalPayloadEnd;
            if (!Fse.InCheckedInit(ref @in, h.LiteralBits, src, ref buf, 0)) throw Corrupt("invalid literal stream");

            int state0 = h.LiteralState0, state1 = h.LiteralState1, state2 = h.LiteralState2, state3 = h.LiteralState3;
            for (int i = 0; i < literalCount; i += 4) // n_literals is multiple of 4
            {
                if (!Fse.InCheckedFlush(ref @in, src, ref buf, 0)) throw Corrupt("invalid literal stream");
                literals[i + 0] = (byte)Fse.Decode(ref state0, literalDecoder, ref @in); // 10b max
                literals[i + 1] = (byte)Fse.Decode(ref state1, literalDecoder, ref @in); // 10b max
                literals[i + 2] = (byte)Fse.Decode(ref state2, literalDecoder, ref @in); // 10b max
                literals[i + 3] = (byte)Fse.Decode(ref state3, literalDecoder, ref @in); // 10b max
            }
        }

        // Initialize the L,M,D decode stream (read bits backwards from the end of the payload) and decode the block.
        {
            FseInStream @in = default;
            int lmdPayloadStart = literalPayloadEnd;
            int buf = lmdPayloadStart + (int)h.NLmdPayloadBytes;
            if (!Fse.InCheckedInit(ref @in, h.LmdBits, src, ref buf, lmdPayloadStart))
                throw Corrupt("invalid match stream");
            DecodeLmd(src, ref @in, buf, in h, literals, literalCount, lDecoder, mDecoder, dDecoder, output);
        }

        return (int)blockEnd; // to next block
    }

    private static void DecodeLmd(ReadOnlySpan<byte> src, ref FseInStream @in, int buf, in BlockHeader h,
        byte[] literals, int literalCount, FseValueDecoderEntry[] lDecoder, FseValueDecoderEntry[] mDecoder,
        FseValueDecoderEntry[] dDecoder, DecodeOutput output)
    {
        int lState = h.LState, mState = h.MState, dState = h.DState;
        int lit = 0;
        byte[] dst = output.Buffer;
        int d = output.Length;
        //  Initialize D to an illegal value so we can't erroneously use an uninitialized "previous" value.
        int D = -1;

        for (uint symbols = h.NMatches; symbols > 0; symbols--)
        {
            //  Decode the next L, M, D symbol from the input stream.
            if (!Fse.InCheckedFlush(ref @in, src, ref buf, 0)) throw Corrupt("invalid match stream");
            int L = Fse.ValueDecode(ref lState, lDecoder, ref @in);
            if (L > literalCount - lit) throw Corrupt("literal overflow");
            int M = Fse.ValueDecode(ref mState, mDecoder, ref @in);
            int newD = Fse.ValueDecode(ref dState, dDecoder, ref @in);
            if (newD != 0) D = newD;

            //  Error if D is out of range, so that we avoid passing through uninitialized data or accessing memory out
            //  of the destination buffer.
            if ((uint)D > (uint)(d + L)) throw Corrupt("invalid match distance");

            int n = L + M;
            if (n > dst.Length - d)
            {
                output.Length = d;
                output.Reserve(n);
                dst = output.Buffer;
            }

            if (dst.Length - d >= n + 16)
            {
                //  Plenty of space remaining: copy the literal and match with 8-byte operations that may write up to
                //  15 bytes past their end (later output overwrites them). The literal buffer has 64 bytes of slack.
                if (L <= 16)
                {
                    Copy8(dst, d, literals, lit);
                    Copy8(dst, d + 8, literals, lit + 8);
                }
                else
                {
                    literals.AsSpan(lit, L).CopyTo(dst.AsSpan(d, L));
                }
                d += L;
                lit += L;
                //  For the match, we have two paths; a fast copy by 8 bytes if the match distance is large enough to
                //  allow it, and a more careful path that accounts for the possible overlap between source and
                //  destination if the distance is small.
                if (D >= 8 && M <= 16)
                {
                    Copy8(dst, d, dst, d - D);
                    Copy8(dst, d + 8, dst, d - D + 8);
                }
                else
                {
                    CopyMatch(dst, d, D, M);
                }
                d += M;
            }
            else
            {
                //  Close to the end of the buffer: exact copies only.
                literals.AsSpan(lit, L).CopyTo(dst.AsSpan(d, L));
                d += L;
                lit += L;
                CopyMatch(dst, d, D, M);
                d += M;
            }
        }

        output.Length = d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy8(byte[] dst, int to, byte[] src, int from) =>
        BinaryPrimitives.WriteUInt64LittleEndian(dst.AsSpan(to, 8),
            BinaryPrimitives.ReadUInt64LittleEndian(src.AsSpan(from, 8)));

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <paramref name="distance"/> bytes back to <paramref name="d"/>, with
    /// the semantics of a forward byte-by-byte copy: when the two overlap, the leading bytes repeat.
    /// </summary>
    internal static void CopyMatch(byte[] dst, int d, int distance, int length)
    {
        // Callers reject a distance of 0 already; checking here too means the copy below always terminates.
        if (distance <= 0) throw new InvalidDataException("LZFSE stream is corrupt (invalid match distance).");
        int from = d - distance;
        if (distance >= length)
        {
            dst.AsSpan(from, length).CopyTo(dst.AsSpan(d, length));
        }
        else if (distance == 1)
        {
            dst.AsSpan(d, length).Fill(dst[from]);
        }
        else
        {
            // The output repeats with period `distance`: copy one period, then keep doubling what has been copied.
            dst.AsSpan(from, distance).CopyTo(dst.AsSpan(d, distance));
            int done = distance;
            while (done < length)
            {
                int n = Math.Min(done, length - done);
                dst.AsSpan(d, n).CopyTo(dst.AsSpan(d + done, n));
                done += n;
            }
        }
    }

    // MARK: - Output size

    /// <summary>
    /// The decoded length the block headers declare, as an allocation hint. It is never trusted beyond what the
    /// input could plausibly hold, so a hostile header cannot make the decoder reserve memory it never fills.
    /// </summary>
    private static int InitialCapacity(ReadOnlySpan<byte> src, int maxLength)
    {
        long total = 0;
        int pos = 0;
        while (src.Length - pos >= 4)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
            long blockEnd;
            uint nRaw;
            if (magic == UncompressedBlockMagic && src.Length - pos >= 8)
            {
                nRaw = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
                blockEnd = pos + 8L + nRaw;
            }
            else if (magic == CompressedLzvnBlockMagic && src.Length - pos >= 12)
            {
                nRaw = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
                blockEnd = pos + 12L + BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 8, 4));
            }
            else if (magic == CompressedV2BlockMagic && src.Length - pos >= V2HeaderFixedSize)
            {
                nRaw = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + 4, 4));
                ulong v0 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(pos + 8, 8));
                ulong v1 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(pos + 16, 8));
                ulong v2 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(pos + 24, 8));
                blockEnd = pos + (long)GetField(v2, 0, 32) + GetField(v0, 20, 20) + GetField(v1, 40, 20);
            }
            else if (magic == CompressedV1BlockMagic && src.Length - pos >= V1HeaderSize)
            {
                nRaw = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + V1NRawBytesOffset, 4));
                blockEnd = pos + (long)V1HeaderSize
                    + BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + V1NLiteralPayloadBytesOffset, 4))
                    + BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos + V1NLmdPayloadBytesOffset, 4));
            }
            else
            {
                break; // end of stream, or something the decoder will report
            }
            if (blockEnd > src.Length || blockEnd <= pos) break;
            total += nRaw;
            pos = (int)blockEnd;
        }

        long plausible = 64L * src.Length + (1 << 20);
        long capacity = Math.Min(Math.Min(total, plausible), maxLength);
        return (int)Math.Max(capacity, Math.Min(4096, maxLength));
    }

    private static InvalidDataException Truncated() => new("LZFSE stream is truncated.");

    private static InvalidDataException Corrupt(string what) => new($"LZFSE stream is corrupt ({what}).");
}

/// <summary>The decoder's output: a buffer that is either exactly the expected length or grows up to a limit.</summary>
internal sealed class DecodeOutput
{
    private readonly bool _exact;

    private DecodeOutput(byte[] buffer, int limit, bool exact)
    {
        Buffer = buffer;
        Limit = limit;
        _exact = exact;
    }

    /// <summary>The output so far is <c>Buffer[0..Length]</c>; bytes after it are scratch.</summary>
    internal byte[] Buffer { get; private set; }

    internal int Length { get; set; }

    /// <summary>The most bytes the stream may decode to.</summary>
    internal int Limit { get; }

    internal static DecodeOutput Exact(int length) => new(length == 0 ? [] : new byte[length], length, true);

    internal static DecodeOutput Growable(int initialCapacity, int limit) =>
        new(new byte[initialCapacity], limit, false);

    /// <summary>Makes room for <paramref name="count"/> more bytes after <see cref="Length"/>.</summary>
    /// <exception cref="InvalidDataException">The output would exceed <see cref="Limit"/>.</exception>
    internal void Reserve(int count)
    {
        long needed = (long)Length + count;
        if (needed <= Buffer.Length) return;
        if (needed > Limit) throw TooLong();
        long capacity = Math.Min(Math.Max(2L * Buffer.Length, needed), Limit);
        var grown = new byte[capacity];
        Buffer.AsSpan(0, Length).CopyTo(grown);
        Buffer = grown;
    }

    internal InvalidDataException TooLong() => _exact
        ? new InvalidDataException($"LZFSE stream decodes to more than {Limit} bytes, expected {Limit}.")
        : new InvalidDataException($"LZFSE stream decodes to more than {Limit} bytes.");

    internal byte[] ToArray() => Length == Buffer.Length ? Buffer : Buffer.AsSpan(0, Length).ToArray();
}

/// <summary>Tables and the literal buffer of the block decoder, one set per thread.</summary>
internal sealed class LzfseDecoderScratch
{
    [ThreadStatic] private static LzfseDecoderScratch? t_current;

    internal static LzfseDecoderScratch ForCurrentThread() => t_current ??= new LzfseDecoderScratch();

    /// <summary>
    /// Clears the decoder tables for a new stream, as the reference zeroes its whole decoder state: what a stream
    /// decodes to never depends on an earlier stream.
    /// </summary>
    internal LzfseDecoderScratch Reset()
    {
        Array.Clear(LiteralDecoder);
        Array.Clear(LDecoder);
        Array.Clear(MDecoder);
        Array.Clear(DDecoder);
        return this;
    }

    internal readonly ushort[] Freq = new ushort[LzfseConstants.FreqTableEntries];
    internal readonly int[] LiteralDecoder = new int[LzfseConstants.EncodeLiteralStates];
    internal readonly FseValueDecoderEntry[] LDecoder = new FseValueDecoderEntry[LzfseConstants.EncodeLStates];
    internal readonly FseValueDecoderEntry[] MDecoder = new FseValueDecoderEntry[LzfseConstants.EncodeMStates];
    internal readonly FseValueDecoderEntry[] DDecoder = new FseValueDecoderEntry[LzfseConstants.EncodeDStates];

    /// <summary>The literal stream for the block, plus padding to allow for faster copy operations.</summary>
    internal readonly byte[] Literals = new byte[LzfseConstants.LiteralsPerBlock + 64];
}
