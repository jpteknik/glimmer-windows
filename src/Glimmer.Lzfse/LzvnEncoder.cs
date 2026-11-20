// Ported from lzvn_encode_base.c and lzvn_encode_base.h of the LZFSE reference implementation,
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
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Glimmer.Lzfse;

/// <summary>LZVN low-level encoder, used by LZFSE for inputs below 4 KB.</summary>
/// <remarks>
/// The reference copies literals with 4- and 8-byte stores that may run past their end; those bytes are always
/// overwritten by what is written next, so here every copy is exact and the output is the same.
/// </remarks>
internal static class LzvnEncoder
{
    private const int HashBits = 14; // number of bits returned by the hash function [10, 16]
    private const int OffsetsPerHash = 4; // stored offsets stack for each hash value, MUST be 4
    private const int HashValues = 1 << HashBits; // number of entries in hash table
    private const int MaxDistance = 0xffff; // max match distance we can represent with LZVN encoding, MUST be 0xFFFF
    private const int MinMargin = 8; // min number of bytes required between current and end during encoding, >= 8
    private const int MaxLiteralBacklog = 400; // if the number of pending literals exceeds this size, emit a long
                                               // literal, MUST be >= 271

    /// <summary>Match.</summary>
    private struct MatchInfo
    {
        internal int MBegin; // beginning of match, current position
        internal int MEnd;   // end of match, this is where the next literal would begin if we emit the entire match
        internal int M;      // match length M: m_end - m_begin
        internal int D;      // match distance D
        internal int K;      // match gain: M - distance storage (L not included)
    }

    /// <summary>Base encoder state and I/O.</summary>
    private ref struct State
    {
        // Source buffer. Valid range: we can access Src[i] for SrcBegin <= i < SrcEnd.
        internal ReadOnlySpan<byte> Src;
        internal int SrcBegin;
        internal int SrcEnd;
        // Next byte to process in source buffer
        internal int SrcCurrent;
        // Next byte after the last byte to process in source buffer. We MUST have: SrcCurrentEnd + 8 <= SrcEnd.
        internal int SrcCurrentEnd;
        // Next byte to encode in source buffer, may be before or after SrcCurrent.
        internal int SrcLiteral;

        // Destination buffer; Dst is the next byte to write, the valid range is [0, DstEnd - 1].
        internal Span<byte> DstBuffer;
        internal int Dst;
        internal int DstEnd;

        // Pending match
        internal MatchInfo Pending;

        // Distance for last emitted match, or 0
        internal int DPrev;

        // Hash table used to find matches. Stores OffsetsPerHash 32-bit signed indices in the source buffer, and the
        // corresponding 4-byte values. The number of entries in the table is HashValues.
        internal int[] TableIndices;
        internal uint[] TableValues;
    }

    // MARK: - API entry point

    /// <summary>lzvn_encode_buffer: encode all of <paramref name="src"/> into <paramref name="dst"/>.</summary>
    /// <param name="tableIndices">Work space of at least HashValues * 4 entries.</param>
    /// <param name="tableValues">Work space of at least HashValues * 4 entries.</param>
    /// <returns>The encoded size, or 0 if the entire input could not be encoded into <paramref name="dst"/>.</returns>
    internal static int EncodeBuffer(Span<byte> dst, ReadOnlySpan<byte> src, int[] tableIndices, uint[] tableValues)
    {
        int dstUsed = EncodePartial(dst, src, out int srcUsed, tableIndices, tableValues);
        if (srcUsed != src.Length) return 0; // could not encode entire input stream = fail
        return dstUsed; // return encoded size
    }

    private static int EncodePartial(Span<byte> dst, ReadOnlySpan<byte> src, out int srcUsed, int[] tableIndices,
        uint[] tableValues)
    {
        // Min size checks to avoid accessing memory outside buffers.
        if (dst.Length < LzfseConstants.LzvnEncodeMinDstSize)
        {
            srcUsed = 0;
            return 0;
        }

        // Setup encoder state
        State state = default;
        state.Src = src;
        state.SrcBegin = 0;
        state.SrcEnd = src.Length;
        state.SrcLiteral = 0;
        state.SrcCurrent = 0;
        state.DstBuffer = dst;
        state.Dst = 0;
        state.DstEnd = dst.Length - 8; // reserve 8 bytes for end-of-stream
        state.TableIndices = tableIndices;
        state.TableValues = tableValues;

        // Do not encode if the input buffer is too small. We'll emit a literal instead.
        if (src.Length >= LzfseConstants.LzvnEncodeMinSrcSize)
        {
            state.SrcCurrentEnd = src.Length - MinMargin;
            InitTable(ref state);
            Encode(ref state);
        }

        // No need to test the return value: SrcLiteral will not be updated on failure, and we will fail later.
        EmitLiteral(ref state, state.SrcEnd - state.SrcLiteral);

        // Restore original size, so end-of-stream always succeeds, and emit it
        state.DstEnd = dst.Length;
        EmitEndOfStream(ref state);

        srcUsed = state.SrcLiteral;
        return state.Dst;
    }

    // MARK: - Coarse/fine copy, non overlapping buffers

    /// <summary>
    /// Emit (L,0,0) instructions (final literal). We read at most L bytes from p.
    /// </summary>
    /// <returns>The next output position, or -1 if the output is full (q1 is the first byte after the output).</returns>
    private static int EmitLiteral(ReadOnlySpan<byte> src, int p, Span<byte> dst, int q, int q1, int L)
    {
        while (L > 15)
        {
            int x = L < 271 ? L : 271;
            if (q + x + 10 >= q1) return -1;
            dst[q] = 0xE0;
            dst[q + 1] = (byte)(x - 16);
            q += 2;
            L -= x;
            src.Slice(p, x).CopyTo(dst.Slice(q, x));
            q += x;
            p += x;
        }
        if (L > 0)
        {
            if (q + L + 10 >= q1) return -1;
            dst[q++] = (byte)(0xE0 + L); // 1110LLLL
            src.Slice(p, L).CopyTo(dst.Slice(q, L));
            q += L;
        }
        return q;
    }

    /// <summary>
    /// Emit (L,M,D) instructions. M&gt;=3. p is the beginning of the literal; we read at most L bytes from it.
    /// </summary>
    /// <returns>The next output position, or -1 if the output is full (q1 is the first byte after the output).</returns>
    private static int Emit(ReadOnlySpan<byte> src, int p, Span<byte> dst, int q, int q1, int L, int M, int D,
        int DPrev)
    {
        int x;
        while (L > 15)
        {
            x = L < 271 ? L : 271;
            if (q + x + 10 >= q1) return -1;
            dst[q] = 0xE0;
            dst[q + 1] = (byte)(x - 16);
            q += 2;
            L -= x;
            src.Slice(p, x).CopyTo(dst.Slice(q, x));
            q += x;
            p += x;
        }
        if (L > 3)
        {
            if (q + L + 10 >= q1) return -1;
            dst[q++] = (byte)(0xE0 + L); // 1110LLLL
            src.Slice(p, L).CopyTo(dst.Slice(q, L));
            q += L;
            p += L;
            L = 0;
        }
        x = M <= 10 - 2 * L ? M : 10 - 2 * L; // x = min(10-2*L,M)
        M -= x;
        x -= 3; // M = (x+3) + M'    max value for x is 7-2*L

        // Here L<4 literals remaining, we read them at p.

        // Relaxed capacity test covering all cases
        if (q + 8 >= q1) return -1;

        if (D == DPrev)
        {
            if (L == 0)
                dst[q++] = (byte)(0xF0 + (x + 3)); // XM!
            else
                dst[q++] = (byte)((L << 6) + (x << 3) + 6); //  LLxxx110
        }
        else if (D < 2048 - 2 * 256)
        {
            // Short dist    D>>8 in 0..5
            dst[q++] = (byte)((D >> 8) + (L << 6) + (x << 3)); // LLxxxDDD
            dst[q++] = (byte)(D & 0xFF);
        }
        else if (D >= (1 << 14) || M == 0 || (x + 3) + M > 34)
        {
            // Long dist
            dst[q++] = (byte)((L << 6) + (x << 3) + 7);
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(q, 2), (ushort)D);
            q += 2;
        }
        else
        {
            // Medium distance
            x += M;
            M = 0;
            dst[q++] = (byte)(0xA0 + (x >> 2) + (L << 3));
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(q, 2), (ushort)((D << 2) | (x & 3)));
            q += 2;
        }
        src.Slice(p, L).CopyTo(dst.Slice(q, L));
        q += L;

        // Issue remaining match
        while (M > 15)
        {
            if (q + 2 >= q1) return -1;
            x = M < 271 ? M : 271;
            dst[q] = 0xF0;
            dst[q + 1] = (byte)(x - 16);
            q += 2;
            M -= x;
        }
        if (M > 0)
        {
            if (q + 1 >= q1) return -1;
            dst[q++] = (byte)(0xF0 + M); // M = 0..15
        }

        return q;
    }

    // MARK: - Hash and Matching

    /// <summary>Get hash in range [0, HashValues-1] from 3 bytes in i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash3i(uint i)
    {
        i &= 0xffffff; // truncate to 24-bit input (slightly increases compression ratio)
        uint h = (i * (1 + (1 << 6) + (1 << 12))) >> 12;
        return (int)(h & (HashValues - 1));
    }

    /// <summary>Return the number [0, 4] of zero bytes in x, starting from the least significant byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TrailingZeroBytes(uint x) => x == 0 ? 4 : BitOperations.TrailingZeroCount(x) >> 3;

    /// <summary>
    /// Return the number [0, 4] of matching chars between values at src+i and src+j, starting from the least
    /// significant byte. Assumes we can read 4 chars from each position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NMatch4(ReadOnlySpan<byte> src, int i, int j)
    {
        uint vi = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(i, 4));
        uint vj = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(j, 4));
        return TrailingZeroBytes(vi ^ vj);
    }

    /// <summary>
    /// Check if lBegin, mBegin, m0Begin (m0Begin &lt; mBegin) can be expanded to a match of length at least 3, given
    /// that <paramref name="n"/> bytes (n &lt;= 4) are already known to match.
    /// </summary>
    /// <returns>true and the match, if a match can be found.</returns>
    private static bool FindMatchN(ReadOnlySpan<byte> src, int srcBegin, int srcEnd, int lBegin, int m0Begin,
        int mBegin, int n, out MatchInfo match)
    {
        match = default;
        if (n < 3) return false; // no match

        int D = mBegin - m0Begin; // actual distance
        if (D <= 0 || D > MaxDistance) return false; // distance out of range

        // Expand forward
        int mEnd = mBegin + n;
        while (n == 4 && mEnd + 4 < srcEnd)
        {
            n = NMatch4(src, mEnd, mEnd - D);
            mEnd += n;
        }

        // Expand backwards over literal
        while (m0Begin > srcBegin && mBegin > lBegin && src[mBegin - 1] == src[m0Begin - 1])
        {
            m0Begin--;
            mBegin--;
        }

        // OK, we keep it, update MATCH
        int M = mEnd - mBegin; // match length
        match.MBegin = mBegin;
        match.MEnd = mEnd;
        match.K = M - (D < 0x600 ? 2 : 3);
        match.M = M;
        match.D = D;

        return true; // OK
    }

    /// <summary>As <see cref="FindMatchN"/>, measuring the first 4 bytes itself.</summary>
    private static bool FindMatch(ReadOnlySpan<byte> src, int srcBegin, int srcEnd, int lBegin, int m0Begin,
        int mBegin, out MatchInfo match) =>
        FindMatchN(src, srcBegin, srcEnd, lBegin, m0Begin, mBegin, NMatch4(src, mBegin, m0Begin), out match);

    // MARK: - Encoder Backend

    /// <summary>Emit a match and update state.</summary>
    /// <returns>false if there is no more space in the destination to emit the match.</returns>
    private static bool EmitMatch(ref State state, in MatchInfo match)
    {
        int L = match.MBegin - state.SrcLiteral; // literal count
        int dst = Emit(state.Src, state.SrcLiteral, state.DstBuffer, state.Dst, state.DstEnd, L, match.M, match.D,
            state.DPrev);
        // Check if DST is full
        if (dst < 0) return false; // FULL

        // Update state
        state.DPrev = match.D;
        state.Dst = dst;
        state.SrcLiteral = match.MEnd;
        return true;
    }

    /// <summary>Emit a n-bytes literal and update state.</summary>
    /// <returns>false if there is no more space in the destination to emit the literal.</returns>
    private static bool EmitLiteral(ref State state, int n)
    {
        int dst = EmitLiteral(state.Src, state.SrcLiteral, state.DstBuffer, state.Dst, state.DstEnd, n);
        // Check if DST is full
        if (dst < 0) return false; // FULL

        // Update state
        state.Dst = dst;
        state.SrcLiteral += n;
        return true;
    }

    /// <summary>Emit end-of-stream and update state.</summary>
    /// <returns>false if there is no more space in the destination to emit the instruction.</returns>
    private static bool EmitEndOfStream(ref State state)
    {
        // Do we have 8 byte in dst?
        if (state.DstEnd < state.Dst + 8) return false; // FULL

        // Insert end marker and update state
        BinaryPrimitives.WriteUInt64LittleEndian(state.DstBuffer.Slice(state.Dst, 8), 0x06); // end-of-stream command
        state.Dst += 8;
        return true;
    }

    // MARK: - Encoder Functions

    /// <summary>Initialize encoder table in state, uses current I/O parameters.</summary>
    private static void InitTable(ref State state)
    {
        int index = -MaxDistance; // max match distance
        if (index < state.SrcBegin) index = state.SrcBegin;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(state.Src.Slice(index, 4));

        Array.Fill(state.TableIndices, index, 0, HashValues * OffsetsPerHash); // fill entire table
        Array.Fill(state.TableValues, value, 0, HashValues * OffsetsPerHash);
    }

    /// <summary>Update best with candidate if better.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Update(ref MatchInfo best, in MatchInfo candidate)
    {
        if (candidate.K > best.K || (candidate.K == best.K && candidate.MEnd > best.MEnd + 1)) best = candidate;
    }

    /// <summary>Encode source to destination. The call ensures SrcLiteral is never left too far behind SrcCurrent.</summary>
    private static void Encode(ref State state)
    {
        ReadOnlySpan<byte> src = state.Src;
        int[] indices = state.TableIndices;
        uint[] values = state.TableValues;

        for (; state.SrcCurrent < state.SrcCurrentEnd; state.SrcCurrent++)
        {
            // Get 4 bytes at SrcCurrent
            uint vi = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(state.SrcCurrent, 4));

            // Compute new hash H at position I, and push value into position table
            int h = Hash3i(vi) * OffsetsPerHash; // index of first entry

            // Read table entries for H
            int e0 = indices[h], e1 = indices[h + 1], e2 = indices[h + 2], e3 = indices[h + 3];
            uint v0 = values[h], v1 = values[h + 1], v2 = values[h + 2], v3 = values[h + 3];

            // Do not check matches if still in previously emitted match
            if (state.SrcCurrent < state.SrcLiteral) goto after_emit;

            MatchInfo incoming = default;

            // Check candidates in order (closest first). The values stored in the table are 32-bit signed indices.
            CheckCandidate(ref state, ref incoming, e0, TrailingZeroBytes(v0 ^ vi));
            CheckCandidate(ref state, ref incoming, e1, TrailingZeroBytes(v1 ^ vi));
            CheckCandidate(ref state, ref incoming, e2, TrailingZeroBytes(v2 ^ vi));
            CheckCandidate(ref state, ref incoming, e3, TrailingZeroBytes(v3 ^ vi));

            // Check candidate at previous distance
            if (state.DPrev != 0)
            {
                if (FindMatch(src, state.SrcBegin, state.SrcEnd, state.SrcLiteral, state.SrcCurrent - state.DPrev,
                        state.SrcCurrent, out MatchInfo m1))
                {
                    m1.K = m1.M - 1; // fix K for D_prev
                    Update(ref incoming, m1);
                }
            }

            // Here we have the best candidate in incoming, may be NO_MATCH

            // If no incoming match, and literal backlog becomes too high, emit pending match, or literals if there is
            // no pending match
            if (incoming.M == 0)
            {
                // at this point, we always have current >= literal
                if (state.SrcCurrent - state.SrcLiteral >= MaxLiteralBacklog)
                {
                    if (state.Pending.M != 0)
                    {
                        if (!EmitMatch(ref state, state.Pending)) return;
                        state.Pending = default;
                    }
                    else
                    {
                        // emit long literal (271 is the longest literal size we allow)
                        if (!EmitLiteral(ref state, 271)) return;
                    }
                }
                goto after_emit;
            }

            if (state.Pending.M == 0)
            {
                // NOTE. Here, we can also emit incoming right away. It will make the encoder 1.5x faster, at a cost of
                // ~10% lower compression ratio.

                // No pending match, emit nothing, keep incoming
                state.Pending = incoming;
            }
            else
            {
                // Here we have both incoming and pending
                if (state.Pending.MEnd <= incoming.MBegin)
                {
                    // No overlap: emit pending, keep incoming
                    if (!EmitMatch(ref state, state.Pending)) return;
                    state.Pending = incoming;
                }
                else
                {
                    // If pending is better, emit pending and discard incoming. Otherwise, emit incoming and discard
                    // pending.
                    if (incoming.K > state.Pending.K) state.Pending = incoming;
                    if (!EmitMatch(ref state, state.Pending)) return;
                    state.Pending = default;
                }
            }

        after_emit:

            // We commit state changes only after we tried to emit instructions, so we can restart in the same state
            // in case dst was full and we quit the loop. The entry is rotated, so we replace the oldest.
            indices[h] = state.SrcCurrent;
            indices[h + 1] = e0;
            indices[h + 2] = e1;
            indices[h + 3] = e2;
            values[h] = vi;
            values[h + 1] = v0;
            values[h + 2] = v1;
            values[h + 3] = v2;
        } // i loop

        // Do not emit pending match here. We do it only at the end of stream.
    }

    /// <summary>Check candidate. Keep if better.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckCandidate(ref State state, ref MatchInfo incoming, int ik, int nk)
    {
        if (FindMatchN(state.Src, state.SrcBegin, state.SrcEnd, state.SrcLiteral, ik, state.SrcCurrent, nk,
                out MatchInfo m1))
        {
            Update(ref incoming, m1);
        }
    }
}
