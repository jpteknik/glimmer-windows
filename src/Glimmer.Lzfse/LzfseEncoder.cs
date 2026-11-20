// Ported from lzfse_encode_base.c and lzfse_encode.c of the LZFSE reference implementation,
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

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Glimmer.Lzfse.LzfseConstants;

namespace Glimmer.Lzfse;

/// <summary>
/// An lzfse match is a sequence of bytes in the source buffer that exactly matches an earlier (but possibly
/// overlapping) sequence of bytes in the same buffer.
/// </summary>
internal struct LzfseMatch
{
    internal int Pos;     // Offset of the first byte in the match.
    internal int Ref;     // First byte of the source -- the earlier location in the buffer with the same contents.
    internal uint Length; // Length of the match.
}

/// <summary>Encoder state object (lzfse_encoder_state), reused by the encodes on one thread.</summary>
internal sealed class LzfseEncoderState
{
    [ThreadStatic] private static LzfseEncoderState? t_current;

    internal static LzfseEncoderState ForCurrentThread() => t_current ??= new LzfseEncoderState();

    //  History table used to search for matches. Each line of the table represents a set of candidate match
    //  locations, each of which begins with four bytes with the same hash. The table contains not only the positions,
    //  but also the first four bytes at each position, so that false-positive matches are eliminated without pulling
    //  in any additional data. Line h is entries [h * EncodeHashWidth, (h + 1) * EncodeHashWidth); entry 0 is the most
    //  recent. The LZVN encoder uses the same two arrays as its table, as the reference shares the scratch buffer.
    internal readonly int[] HistoryPos = new int[EncodeHashValues * EncodeHashWidth];
    internal readonly uint[] HistoryValue = new uint[EncodeHashValues * EncodeHashWidth];

    //  Lengths of found literals, lengths of found matches, distances of found matches.
    internal readonly uint[] LValues = new uint[MatchesPerBlock];
    internal readonly uint[] MValues = new uint[MatchesPerBlock];
    internal readonly uint[] DValues = new uint[MatchesPerBlock];

    //  Concatenated literal bytes.
    internal readonly byte[] Literals = new byte[LiteralsPerBlock];

    //  Length of the source buffer in bytes.
    internal int SrcEnd;
    //  Offset of the first byte of the next literal to encode in the source buffer.
    internal int SrcLiteral;
    //  Offset of the byte currently being checked for a match.
    internal int SrcEncodeI;
    //  The last byte offset to consider for a match.
    internal int SrcEncodeEnd;

    //  The destination buffer, the next byte to write, and the end of the space the encoder may use.
    internal byte[] Dst = [];
    internal int DstPos;
    internal int DstEnd;

    //  Pending match; will be emitted unless a better match is found.
    internal LzfseMatch Pending;
    //  The number of matches written so far.
    internal uint NMatches;
    //  The number of literals written so far.
    internal uint NLiterals;
}

internal static class LzfseEncoder
{
    /// <summary>Extra bytes at the end of the work buffer, so that 8-byte stores near the end stay inside it.</summary>
    private const int DstSlack = 16;

    /// <summary>The largest input <see cref="EncodeBuffer"/> accepts (its output can be 12 bytes longer).</summary>
    internal static int MaxInputLength => Array.MaxLength - 12 - DstSlack;

    /// <summary>
    /// lzfse_encode_buffer with an output buffer of <c>src.Length + 12</c> bytes: room for the input stored
    /// uncompressed, which is where the reference falls back to whenever compression does not fit.
    /// </summary>
    internal static byte[] EncodeBuffer(ReadOnlySpan<byte> src)
    {
        int srcSize = src.Length;
        int dstSize = srcSize + 12;
        LzfseEncoderState state = LzfseEncoderState.ForCurrentThread();

        // If input is really really small, go directly to uncompressed buffer (because LZVN will refuse to encode
        // it, and we will report a failure)
        if (srcSize < LzvnEncodeMinSrcSize) return EncodeUncompressed(src);

        byte[] work = ArrayPool<byte>.Shared.Rent(dstSize + DstSlack);
        try
        {
            // If input is too small, try encoding with LZVN
            if (srcSize < EncodeLzvnThreshold)
            {
                // need header + end-of-stream marker
                const int extraSize = 4 + LzvnBlockHeaderSize;
                if (dstSize <= extraSize) return EncodeUncompressed(src); // DST is really too small, give up

                int sz = LzvnEncoder.EncodeBuffer(work.AsSpan(LzvnBlockHeaderSize, dstSize - extraSize), src,
                    state.HistoryPos, state.HistoryValue);
                if (sz == 0 || sz >= srcSize)
                    return EncodeUncompressed(src); // failed, or no compression, fall back to uncompressed block

                // If we could encode, setup header and end-of-stream marker (we left room for them, no need to test)
                BinaryPrimitives.WriteUInt32LittleEndian(work.AsSpan(0, 4), CompressedLzvnBlockMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(work.AsSpan(4, 4), (uint)srcSize);
                BinaryPrimitives.WriteUInt32LittleEndian(work.AsSpan(8, 4), (uint)sz);
                BinaryPrimitives.WriteUInt32LittleEndian(work.AsSpan(LzvnBlockHeaderSize + sz, 4),
                    EndOfStreamBlockMagic);
                return work.AsSpan(0, sz + extraSize).ToArray();
            }

            // Try encoding with LZFSE. (Inputs of 4 GB or more would be processed in chunks; a span cannot be that
            // long.)
            EncodeInit(state);
            state.Dst = work;
            state.DstPos = 0;
            state.DstEnd = dstSize;
            state.SrcEncodeI = 0;
            state.SrcEnd = srcSize;
            bool ok = EncodeBase(state, src) && EncodeFinish(state, src);
            if (!ok) return EncodeUncompressed(src);
            return work.AsSpan(0, state.DstPos).ToArray();
        }
        finally
        {
            state.Dst = []; // the thread's state must not keep an array that is back in the pool
            ArrayPool<byte>.Shared.Return(work);
        }
    }

    /// <summary>
    /// The input as one uncompressed block: what the reference writes when compression failed for some reason and the
    /// data fits into the output buffer uncompressed.
    /// </summary>
    internal static byte[] EncodeUncompressed(ReadOnlySpan<byte> src)
    {
        var dst = new byte[src.Length + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(0, 4), UncompressedBlockMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(4, 4), (uint)src.Length);
        src.CopyTo(dst.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(8 + src.Length, 4), EndOfStreamBlockMagic);
        return dst;
    }

    /// <summary>Get hash in range [0, EncodeHashValues-1] from 4 bytes in X.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashX(uint x) => (x * 2654435761U) >> (32 - EncodeHashBits); // Knuth multiplicative hash

    // MARK: - Header encoding

    /// <summary>Encode an entry value in a freq table. Return bits, and sets nbits to the number of bits to serialize.</summary>
    private static uint EncodeV1FreqValue(int value, out int nbits)
    {
        // Fixed Huffman code, bits are read from LSB. Note that we rely on the position of the first '0' bit providing
        // the number of bits.
        switch (value)
        {
            case 0: nbits = 2; return 0;  //    0.0
            case 1: nbits = 2; return 2;  //    1.0
            case 2: nbits = 3; return 1;  //   0.01
            case 3: nbits = 3; return 5;  //   1.01
            case 4: nbits = 5; return 3;  // 00.011
            case 5: nbits = 5; return 11; // 01.011
            case 6: nbits = 5; return 19; // 10.011
            case 7: nbits = 5; return 27; // 11.011
        }
        if (value < 24)
        {
            nbits = 8;                                  // 4+4
            return 7 + ((uint)(value - 8) << 4);         // xxxx.0111
        }
        // 24..1047
        nbits = 14;                                     // 4+10
        return ((uint)(value - 24) << 4) + 15;          // xxxxxxxxxx.1111
    }

    /// <summary>
    /// Encode all tables of a block header into the v2 header at <paramref name="header"/>: the compressed freq tables,
    /// and packed_fields set to {0, 0, header_size}.
    /// </summary>
    /// <returns>Size of the v2 header.</returns>
    private static int EncodeV1FreqTable(Span<byte> header, ReadOnlySpan<ushort> freq)
    {
        uint accum = 0;
        int accumNbits = 0;
        int dst = V2HeaderFixedSize;
        for (int i = 0; i < FreqTableEntries; i++)
        {
            // Encode one value to accum
            uint bits = EncodeV1FreqValue(freq[i], out int nbits);
            accum |= bits << accumNbits;
            accumNbits += nbits;

            // Store bytes from accum to output buffer
            while (accumNbits >= 8)
            {
                header[dst++] = (byte)(accum & 0xff);
                accum >>= 8;
                accumNbits -= 8;
            }
        }
        // Store final byte if needed
        if (accumNbits > 0) header[dst++] = (byte)(accum & 0xff);

        // Return final size of out
        int headerSize = dst;
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(8, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), (ulong)(uint)headerSize);
        return headerSize;
    }

    /// <summary>Return value with all 0 except nbits&lt;=32 unsigned bits from v at bit offset offset.</summary>
    private static ulong SetField(uint v, int offset) => (ulong)v << offset;

    // MARK: - Encoder back end

    /// <summary>
    /// Encode matches stored in the state into a compressed block.
    /// </summary>
    /// <returns>true on success; false, with the initial state restored, if the output buffer is full.</returns>
    private static bool EncodeMatches(LzfseEncoderState s)
    {
        if (s.NLiterals == 0 && s.NMatches == 0) return true; // nothing to store, OK

        Span<uint> lOcc = stackalloc uint[EncodeLSymbols];
        Span<uint> mOcc = stackalloc uint[EncodeMSymbols];
        Span<uint> dOcc = stackalloc uint[EncodeDSymbols];
        Span<uint> literalOcc = stackalloc uint[EncodeLiteralSymbols];
        Span<FseEncoderEntry> lEncoder = stackalloc FseEncoderEntry[EncodeLSymbols];
        Span<FseEncoderEntry> mEncoder = stackalloc FseEncoderEntry[EncodeMSymbols];
        Span<FseEncoderEntry> dEncoder = stackalloc FseEncoderEntry[EncodeDSymbols];
        Span<FseEncoderEntry> literalEncoder = stackalloc FseEncoderEntry[EncodeLiteralSymbols];
        Span<ushort> freq = stackalloc ushort[FreqTableEntries];
        Span<ushort> lFreq = freq.Slice(LFreqOffset, EncodeLSymbols);
        Span<ushort> mFreq = freq.Slice(MFreqOffset, EncodeMSymbols);
        Span<ushort> dFreq = freq.Slice(DFreqOffset, EncodeDSymbols);
        Span<ushort> literalFreq = freq.Slice(LiteralFreqOffset, EncodeLiteralSymbols);
        bool ok = true;
        uint[] lValues = s.LValues, mValues = s.MValues, dValues = s.DValues;
        byte[] literals = s.Literals;
        Span<byte> dst = s.Dst;

        // Keep initial state to be able to restore it if DST full
        int dst0 = s.DstPos;
        uint nLiterals0 = s.NLiterals;

        // Add 0x00 literals until n_literals multiple of 4, since we encode 4 interleaved literal streams.
        while ((s.NLiterals & 3) != 0) literals[s.NLiterals++] = 0;

        // Encode previous distance
        uint dPrev = 0;
        for (uint i = 0; i < s.NMatches; i++)
        {
            uint d = dValues[i];
            if (d == dPrev) dValues[i] = 0;
            else dPrev = d;
        }

        // Update occurrence tables in all 4 streams (L,M,D,literals)
        uint lSum = 0;
        uint mSum = 0;
        for (uint i = 0; i < s.NMatches; i++)
        {
            uint l = lValues[i];
            lSum += l;
            lOcc[LBaseFromValue((int)l)]++;
        }
        for (uint i = 0; i < s.NMatches; i++)
        {
            uint m = mValues[i];
            mSum += m;
            mOcc[MBaseFromValue((int)m)]++;
        }
        for (uint i = 0; i < s.NMatches; i++) dOcc[DBaseFromValue((int)dValues[i])]++;
        for (uint i = 0; i < s.NLiterals; i++) literalOcc[literals[i]]++;

        int header2 = s.DstPos;
        uint nRawBytes = 0, nLiteralPayloadBytes = 0, nLmdPayloadBytes = 0;
        int literalBits = 0, lmdBits = 0;
        int literalState0 = 0, literalState1 = 0, literalState2 = 0, literalState3 = 0;
        int lState = 0, mState = 0, dState = 0;

        // Make sure we have enough room for a _full_ V2 header
        if (s.DstPos + V2HeaderMaxSize > s.DstEnd)
        {
            ok = false;
            goto END;
        }

        // Setup header V1
        nRawBytes = mSum + lSum;

        // Normalize occurrence tables to freq tables
        Fse.NormalizeFreq(EncodeLStates, EncodeLSymbols, lOcc, lFreq);
        Fse.NormalizeFreq(EncodeMStates, EncodeMSymbols, mOcc, mFreq);
        Fse.NormalizeFreq(EncodeDStates, EncodeDSymbols, dOcc, dFreq);
        Fse.NormalizeFreq(EncodeLiteralStates, EncodeLiteralSymbols, literalOcc, literalFreq);

        // Compress freq tables to V2 header, and get actual size of V2 header
        s.DstPos += EncodeV1FreqTable(dst.Slice(header2, V2HeaderMaxSize), freq);

        // Initialize encoder tables from freq tables
        Fse.InitEncoderTable(EncodeLStates, EncodeLSymbols, lFreq, lEncoder);
        Fse.InitEncoderTable(EncodeMStates, EncodeMSymbols, mFreq, mEncoder);
        Fse.InitEncoderTable(EncodeDStates, EncodeDSymbols, dFreq, dEncoder);
        Fse.InitEncoderTable(EncodeLiteralStates, EncodeLiteralSymbols, literalFreq, literalEncoder);

        // Encode literals
        {
            FseOutStream @out = default;
            int state0 = 0, state1 = 0, state2 = 0, state3 = 0;

            int buf = s.DstPos;
            uint i = s.NLiterals; // I multiple of 4
            // We encode starting from the last literal so we can decode starting from the first
            while (i > 0)
            {
                if (buf + 16 > s.DstEnd)
                {
                    ok = false;
                    goto END;
                } // out full
                i -= 4;
                Fse.Encode(ref state3, literalEncoder, ref @out, literals[i + 3]); // 10b
                Fse.Encode(ref state2, literalEncoder, ref @out, literals[i + 2]); // 10b
                Fse.Encode(ref state1, literalEncoder, ref @out, literals[i + 1]); // 10b
                Fse.Encode(ref state0, literalEncoder, ref @out, literals[i + 0]); // 10b
                Fse.OutFlush(ref @out, dst, ref buf);
            }
            Fse.OutFinish(ref @out, dst, ref buf);

            // Update header with final encoder state
            literalBits = @out.AccumNbits; // [-7, 0]
            nLiteralPayloadBytes = (uint)(buf - s.DstPos);
            literalState0 = state0;
            literalState1 = state1;
            literalState2 = state2;
            literalState3 = state3;

            // Update state
            s.DstPos = buf;
        } // literals

        // Encode L,M,D
        {
            FseOutStream @out = default;
            int l = 0, m = 0, d = 0;

            int buf = s.DstPos;
            uint i = s.NMatches;

            // Add 8 padding bytes to the L,M,D payload
            if (buf + 8 > s.DstEnd)
            {
                ok = false;
                goto END;
            } // out full
            BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(buf, 8), 0);
            buf += 8;

            // We encode starting from the last match so we can decode starting from the first
            while (i > 0)
            {
                if (buf + 16 > s.DstEnd)
                {
                    ok = false;
                    goto END;
                } // out full
                i -= 1;

                // D requires 23b max
                int dValue = (int)dValues[i];
                int dSymbol = DBaseFromValue(dValue);
                int dNbits = DExtraBits[dSymbol];
                int dBits = dValue - DBaseValue[dSymbol];
                Fse.OutPush(ref @out, dNbits, (ulong)dBits);
                Fse.Encode(ref d, dEncoder, ref @out, dSymbol);

                // M requires 17b max
                int mValue = (int)mValues[i];
                int mSymbol = MBaseFromValue(mValue);
                int mNbits = MExtraBits[mSymbol];
                int mBits = mValue - MBaseValue[mSymbol];
                Fse.OutPush(ref @out, mNbits, (ulong)mBits);
                Fse.Encode(ref m, mEncoder, ref @out, mSymbol);

                // L requires 14b max
                int lValue = (int)lValues[i];
                int lSymbol = LBaseFromValue(lValue);
                int lNbits = LExtraBits[lSymbol];
                int lBits = lValue - LBaseValue[lSymbol];
                Fse.OutPush(ref @out, lNbits, (ulong)lBits);
                Fse.Encode(ref l, lEncoder, ref @out, lSymbol);
                Fse.OutFlush(ref @out, dst, ref buf);
            }
            Fse.OutFinish(ref @out, dst, ref buf);

            // Update header with final encoder state
            nLmdPayloadBytes = (uint)(buf - s.DstPos);
            lmdBits = @out.AccumNbits; // [-7, 0]
            lState = l;
            mState = m;
            dState = d;

            // Update state
            s.DstPos = buf;
        } // L,M,D

        {
            // Encode state info in V2 header (we previously encoded the tables, now we set the other fields)
            Span<byte> header = dst.Slice(header2, V2HeaderFixedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), CompressedV2BlockMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), nRawBytes);
            ulong packed0 = SetField(s.NLiterals, 0) | SetField(nLiteralPayloadBytes, 20)
                | SetField(s.NMatches, 40) | SetField((uint)(7 + literalBits), 60);
            ulong packed1 = SetField((uint)literalState0, 0) | SetField((uint)literalState1, 10)
                | SetField((uint)literalState2, 20) | SetField((uint)literalState3, 30)
                | SetField(nLmdPayloadBytes, 40) | SetField((uint)(7 + lmdBits), 60);
            ulong packed2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(24, 8)) // header_size already stored
                | SetField((uint)lState, 32) | SetField((uint)mState, 42) | SetField((uint)dState, 52);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(8, 8), packed0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16, 8), packed1);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), packed2);
        }

        // Final state update, here we had enough space in DST, and are not going to revert state
        s.NLiterals = 0;
        s.NMatches = 0;

    END:
        if (!ok)
        {
            // Revert state, DST was full

            // Revert the d_prev encoding
            uint dPrevRevert = 0;
            for (uint i = 0; i < s.NMatches; i++)
            {
                uint d = dValues[i];
                if (d == 0) dValues[i] = dPrevRevert;
                else dPrevRevert = d;
            }

            // Revert literal count
            s.NLiterals = nLiterals0;

            // Revert DST
            s.DstPos = dst0;

            return false; // DST full
        }

        return true;
    }

    /// <summary>Push a L,M,D match into the state.</summary>
    /// <returns>false if the match can't be pushed, meaning one of the buffers is full. In that case the state is not
    /// modified.</returns>
    private static bool PushLmd(LzfseEncoderState s, ReadOnlySpan<byte> src, uint L, uint M, uint D)
    {
        // Check if we have enough space to push the match (we add some margin to copy literals faster here, and round
        // final count later)
        if (s.NMatches + 1 + 8 > MatchesPerBlock) return false; // state full
        if (s.NLiterals + L + 16 > LiteralsPerBlock) return false; // state full

        // Store match
        uint n = s.NMatches++;
        s.LValues[n] = L;
        s.MValues[n] = M;
        s.DValues[n] = D;

        // Store literals
        src.Slice(s.SrcLiteral, (int)L).CopyTo(s.Literals.AsSpan((int)s.NLiterals, (int)L));
        s.NLiterals += L;

        // Update state
        s.SrcLiteral += (int)(L + M);

        return true;
    }

    /// <summary>Split the match into one or more L,M,D parts, and push to the state.</summary>
    /// <returns>false if the match can't be pushed, meaning one of the buffers is full. In that case the state is not
    /// modified.</returns>
    private static bool PushMatch(LzfseEncoderState s, ReadOnlySpan<byte> src, in LzfseMatch match)
    {
        // Save the initial n_matches, n_literals, src_literal
        uint nMatches0 = s.NMatches;
        uint nLiterals0 = s.NLiterals;
        int srcLiterals0 = s.SrcLiteral;

        // L,M,D
        uint L = (uint)(match.Pos - s.SrcLiteral); // literal count
        uint M = match.Length;                      // match length
        uint D = (uint)(match.Pos - match.Ref);     // match distance
        bool ok = true;

        // Split L if too large
        while (L > EncodeMaxLValue)
        {
            // take D=1 because most frequent, but not actually used
            if (!PushLmd(s, src, EncodeMaxLValue, 0, 1))
            {
                ok = false;
                goto END;
            }
            L -= EncodeMaxLValue;
        }

        // Split if M too large
        while (M > EncodeMaxMValue)
        {
            if (!PushLmd(s, src, L, EncodeMaxMValue, D))
            {
                ok = false;
                goto END;
            }
            L = 0;
            M -= EncodeMaxMValue;
        }

        // L,M in range
        if (L > 0 || M > 0)
        {
            if (!PushLmd(s, src, L, M, D)) ok = false;
        }

    END:
        if (!ok)
        {
            // Revert state
            s.NMatches = nMatches0;
            s.NLiterals = nLiterals0;
            s.SrcLiteral = srcLiterals0;

            return false; // state tables full
        }

        return true; // OK
    }

    /// <summary>Backend: add the match to the state. Encode block if necessary, when state is full.</summary>
    /// <returns>false if the match can't be added, meaning one of the buffers is full. In that case the state is not
    /// modified.</returns>
    private static bool BackendMatch(LzfseEncoderState s, ReadOnlySpan<byte> src, in LzfseMatch match)
    {
        // Try to push the match in state
        if (PushMatch(s, src, match)) return true; // OK, match added to state

        // Here state tables are full, try to emit block
        if (!EncodeMatches(s)) return false; // DST full, match not added

        // Here block has been emitted, re-try to push the match in state
        return PushMatch(s, src, match);
    }

    /// <summary>Backend: add L literals to the state. Encode block if necessary, when state is full.</summary>
    private static bool BackendLiterals(LzfseEncoderState s, ReadOnlySpan<byte> src, int L)
    {
        // Create a fake match with M=0, D=1
        LzfseMatch match;
        int pos = s.SrcLiteral + L;
        match.Pos = pos;
        match.Ref = pos - 1;
        match.Length = 0;
        return BackendMatch(s, src, match);
    }

    /// <summary>Backend: flush final block, and emit end of stream.</summary>
    /// <returns>false if either the final block, or the end-of-stream can't be added.</returns>
    private static bool BackendEndOfStream(LzfseEncoderState s)
    {
        // Final match triggers write, otherwise emit blocks when we have enough matches stored
        if (!EncodeMatches(s)) return false; // DST full

        // Emit end-of-stream block
        if (s.DstPos + 4 > s.DstEnd) return false; // DST full
        BinaryPrimitives.WriteUInt32LittleEndian(s.Dst.AsSpan(s.DstPos, 4), EndOfStreamBlockMagic);
        s.DstPos += 4;

        return true; // OK
    }

    // MARK: - Encoder state management

    /// <summary>
    /// Initialize state: hash table with all invalid pos, and value 0; pending match to NO_MATCH; src_literal to 0.
    /// </summary>
    internal static void EncodeInit(LzfseEncoderState s)
    {
        Array.Fill(s.HistoryPos, -4 * EncodeMaxDValue); // invalid pos
        Array.Clear(s.HistoryValue);
        s.Pending = default;
        s.SrcLiteral = 0;
        s.NMatches = 0;
        s.NLiterals = 0;
    }

    // MARK: - Encoder front end

    internal static bool EncodeBase(LzfseEncoderState s, ReadOnlySpan<byte> src)
    {
        int[] historyPos = s.HistoryPos;
        uint[] historyValue = s.HistoryValue;

        // 8 byte padding at end of buffer
        s.SrcEncodeEnd = s.SrcEnd - 8;
        for (; s.SrcEncodeI < s.SrcEncodeEnd; s.SrcEncodeI++)
        {
            int pos = s.SrcEncodeI; // pos >= 0

            // Load 4 byte value and get hash line
            uint x = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
            int line = (int)HashX(x) * EncodeHashWidth;
            int hPos0 = historyPos[line], hPos1 = historyPos[line + 1];
            int hPos2 = historyPos[line + 2], hPos3 = historyPos[line + 3];
            uint hValue0 = historyValue[line], hValue1 = historyValue[line + 1];
            uint hValue2 = historyValue[line + 2], hValue3 = historyValue[line + 3];

            // Do not look for a match if we are still covered by a previous match
            if (pos < s.SrcLiteral) goto END_POS;

            // Search best incoming match
            LzfseMatch incoming = default;
            incoming.Pos = pos;

            // Check for matches. We consider matches of length >= 4 only.
            CheckCandidate(src, s.SrcEnd, pos, x, hPos0, hValue0, ref incoming);
            CheckCandidate(src, s.SrcEnd, pos, x, hPos1, hValue1, ref incoming);
            CheckCandidate(src, s.SrcEnd, pos, x, hPos2, hValue2, ref incoming);
            CheckCandidate(src, s.SrcEnd, pos, x, hPos3, hValue3, ref incoming);

            // No incoming match?
            if (incoming.Length == 0)
            {
                // We may still want to emit some literals here, to not lag too far behind the current search point,
                // and avoid ending up with a literal block not fitting in the state.
                int nLiterals = pos - s.SrcLiteral;
                // The threshold here should be larger than a couple of MAX_L_VALUE, and much smaller than
                // LITERALS_PER_BLOCK
                if (nLiterals > 8 * EncodeMaxLValue)
                {
                    // Here, we need to consume some literals. Emit pending match if there is one
                    if (s.Pending.Length > 0)
                    {
                        if (!BackendMatch(s, src, s.Pending)) return false;
                        s.Pending = default;
                    }
                    else
                    {
                        // No pending match, emit a full EncodeMaxLValue block of literals
                        if (!BackendLiterals(s, src, EncodeMaxLValue)) return false;
                    }
                }
                goto END_POS; // no incoming match
            }

            // Limit match length (it may still be expanded backwards, but this is bounded by the limit on literals we
            // tested before)
            if (incoming.Length > EncodeMaxMatchLength) incoming.Length = EncodeMaxMatchLength;

            // Expand backwards (since this is expensive, we do this for the best match only)
            while (incoming.Pos > s.SrcLiteral && incoming.Ref > 0 && src[incoming.Ref - 1] == src[incoming.Pos - 1])
            {
                incoming.Pos--;
                incoming.Ref--;
            }
            incoming.Length += (uint)(pos - incoming.Pos); // update length after expansion

            // Match filtering heuristic (from LZVN). INCOMING is always defined here.

            // Incoming is 'good', emit incoming
            if (incoming.Length >= EncodeGoodMatch)
            {
                if (!BackendMatch(s, src, incoming)) return false;
                s.Pending = default;
                goto END_POS;
            }

            // No pending, keep incoming
            if (s.Pending.Length == 0)
            {
                s.Pending = incoming;
                goto END_POS;
            }

            // No overlap, emit pending, keep incoming
            if (s.Pending.Pos + s.Pending.Length <= incoming.Pos)
            {
                if (!BackendMatch(s, src, s.Pending)) return false;
                s.Pending = incoming;
                goto END_POS;
            }

            // Overlap: emit longest
            if (incoming.Length > s.Pending.Length)
            {
                if (!BackendMatch(s, src, incoming)) return false;
            }
            else
            {
                if (!BackendMatch(s, src, s.Pending)) return false;
            }
            s.Pending = default;

        END_POS:
            // We are done with this src_encode_i. Update state now (s.Pending has already been updated): the line
            // gets the current position in front (component 0 is the most recent).
            historyPos[line] = pos;
            historyPos[line + 1] = hPos0;
            historyPos[line + 2] = hPos1;
            historyPos[line + 3] = hPos2;
            historyValue[line] = x;
            historyValue[line + 1] = hValue0;
            historyValue[line + 2] = hValue1;
            historyValue[line + 3] = hValue2;
        }

        return true;
    }

    /// <summary>One candidate of a history line: if it starts with the same 4 bytes, measure the match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckCandidate(ReadOnlySpan<byte> src, int srcEnd, int pos, uint x, int @ref, uint value,
        ref LzfseMatch incoming)
    {
        if (value != x) return; // no 4 byte match
        if (@ref + EncodeMaxDValue < pos) return; // too far

        uint length = 4;
        uint maxLength = (uint)(srcEnd - pos - 8); // ensure we don't hit the end of SRC
        while (length < maxLength)
        {
            ulong d = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(@ref + (int)length, 8))
                ^ BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(pos + (int)length, 8));
            if (d == 0)
            {
                length += 8;
                continue;
            }

            length += (uint)(BitOperations.TrailingZeroCount(d) >> 3);
            break;
        }
        if (length > incoming.Length)
        {
            incoming.Length = length;
            incoming.Ref = @ref;
        } // keep if longer
    }

    internal static bool EncodeFinish(LzfseEncoderState s, ReadOnlySpan<byte> src)
    {
        // Emit pending match
        if (s.Pending.Length > 0)
        {
            if (!BackendMatch(s, src, s.Pending)) return false;
            s.Pending = default;
        }

        // Emit final literals if any
        int L = s.SrcEnd - s.SrcLiteral;
        if (L > 0)
        {
            if (!BackendLiterals(s, src, L)) return false;
        }

        // Emit all matches, and end-of-stream block
        return BackendEndOfStream(s);
    }
}
