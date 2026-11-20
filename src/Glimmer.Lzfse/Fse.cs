// Ported from lzfse_fse.h and lzfse_fse.c of the LZFSE reference implementation,
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

// Finite state entropy coding (FSE)
// This is an implementation of the tANS algorithm described by Jarek Duda, we use the more descriptive name "Finite
// State Entropy". The streams are the 64-bit variant (FSE_IOSTREAM_64), the one the reference uses on 64-bit machines;
// the bit layout of the compressed data does not depend on it.

/// <summary>Entry for one symbol in the encoder table.</summary>
internal struct FseEncoderEntry
{
    internal short S0;     // First state requiring a K-bit shift
    internal short K;      // States S >= S0 are shifted K bits. States S < S0 are shifted K-1 bits
    internal short Delta0; // Relative increment used to compute next state if S >= S0
    internal short Delta1; // Relative increment used to compute next state if S < S0
}

/// <summary>Entry for one state in the value decoder table.</summary>
internal struct FseValueDecoderEntry
{
    internal byte TotalBits; // state bits + extra value bits = shift for next decode
    internal byte ValueBits; // extra value bits
    internal short Delta;    // state base (delta)
    internal int VBase;      // value base
}

/// <summary>Output stream, 64-bit accumulator.</summary>
internal struct FseOutStream
{
    internal ulong Accum;   // Output bits
    internal int AccumNbits; // Number of valid bits in ACCUM, other bits are 0
}

/// <summary>Input stream, 64-bit accumulator. Bits are read backwards, from the end of the payload.</summary>
internal struct FseInStream
{
    internal ulong Accum;   // Input bits
    internal int AccumNbits; // Number of valid bits in ACCUM, other bits are 0
}

internal static class Fse
{
    // MARK: - Bit utils

    /// <summary>Mask the <paramref name="nbits"/> lsb of <paramref name="x"/>. 0 &lt;= nbits &lt;= 64.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong MaskLsb64(ulong x, int nbits) => nbits >= 64 ? x : x & ((1UL << nbits) - 1);

    // MARK: - Output stream

    /// <summary>
    /// Write full bytes from the accumulator to the output buffer, ensuring AccumNbits is in [0, 7]. Writes 8 bytes at
    /// <paramref name="pos"/> (the caller guarantees room) and advances it by the number of complete bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OutFlush(ref FseOutStream s, Span<byte> buf, ref int pos)
    {
        int nbits = s.AccumNbits & -8; // number of bits written, multiple of 8

        // Write 8 bytes of current accumulator
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(pos, 8), s.Accum);
        pos += nbits >> 3; // bytes

        // Update state
        s.Accum = nbits >= 64 ? 0 : s.Accum >> nbits; // remove nbits
        s.AccumNbits -= nbits;
    }

    /// <summary>
    /// Write the last bytes from the accumulator to the output buffer, ensuring AccumNbits is in [-7, 0]. Bits are
    /// padded with 0 if needed. Writes 8 bytes at <paramref name="pos"/> (the caller guarantees room).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OutFinish(ref FseOutStream s, Span<byte> buf, ref int pos)
    {
        int nbits = (s.AccumNbits + 7) & -8; // number of bits written, multiple of 8

        // Write 8 bytes of current accumulator
        BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(pos, 8), s.Accum);
        pos += nbits >> 3; // bytes

        // Update state
        s.Accum = 0; // remove nbits
        s.AccumNbits -= nbits;
    }

    /// <summary>
    /// Accumulate <paramref name="n"/> bits <paramref name="b"/> to output stream <paramref name="s"/>. We must have
    /// 0 &lt;= b &lt; 2^n, and n + AccumNbits &lt;= 64. The caller must flush before the accumulator overflows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OutPush(ref FseOutStream s, int n, ulong b)
    {
        s.Accum |= b << s.AccumNbits;
        s.AccumNbits += n;
    }

    // MARK: - Input stream

    /// <summary>
    /// Initialize the fse input stream so that the accumulator holds between 56 and 63 bits. We never want to have 64
    /// bits in the stream, because that allows us to avoid a special case in <see cref="InPull"/>, while not requiring
    /// any additional flush operations. This is why we have the special case for n == 0 (in which case we want to load
    /// only 7 bytes instead of 8).
    /// </summary>
    /// <param name="n">The final accum_nbits of the encoder, in [-7, 0] for a valid stream.</param>
    /// <param name="buf">The buffer the stream is read from.</param>
    /// <param name="pos">One past the last byte of the stream; moved back over the bytes read.</param>
    /// <param name="bufStart">The first byte that may be read.</param>
    /// <returns>false if out of range or the incoming input is wrong.</returns>
    internal static bool InCheckedInit(ref FseInStream s, int n, ReadOnlySpan<byte> buf, ref int pos, int bufStart)
    {
        if (n != 0)
        {
            if (pos - bufStart < 8) return false; // out of range
            pos -= 8;
            s.Accum = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8));
            long nbits = (long)n + 64;
            if (nbits < 56 || nbits >= 64) return false; // the incoming input is wrong
            s.AccumNbits = (int)nbits;
        }
        else
        {
            if (pos - bufStart < 7) return false; // out of range
            pos -= 7;
            s.Accum = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4))
                | (ulong)BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(pos + 4, 2)) << 32
                | (ulong)buf[pos + 6] << 48;
            s.AccumNbits = 56;
        }

        // the incoming input is wrong (encoder should have zeroed the upper bits)
        return (s.Accum >> s.AccumNbits) == 0;
    }

    /// <summary>
    /// Read in new bytes from the buffer to ensure that we have a full complement of bits in the stream object (again,
    /// between 56 and 63 bits), checking that the new value of <paramref name="pos"/> remains &gt;= bufStart.
    /// </summary>
    /// <returns>false on failure.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool InCheckedFlush(ref FseInStream s, ReadOnlySpan<byte> buf, ref int pos, int bufStart)
    {
        //  Get number of bits to add to bring us into the desired range.
        int nbits = (63 - s.AccumNbits) & -8;
        //  Convert bits to bytes and decrement buffer address, then load new data.
        int newPos = pos - (nbits >> 3);
        if (newPos < bufStart) return false; // out of range
        pos = newPos;
        if (nbits != 0)
        {
            // Only the first nbits/8 bytes are consumed; the read stays inside the payload because the stream
            // started at least 7 bytes before its end.
            ulong incoming = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(newPos, 8));
            s.Accum = (s.Accum << nbits) | (incoming & ((1UL << nbits) - 1));
            s.AccumNbits += nbits;
        }
        return true;
    }

    /// <summary>Pull <paramref name="n"/> bits out of the fse stream object.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong InPull(ref FseInStream s, int n)
    {
        s.AccumNbits -= n;
        ulong result = s.Accum >> s.AccumNbits;
        s.Accum &= (1UL << s.AccumNbits) - 1;
        return result;
    }

    // MARK: - Encode/Decode

    /// <summary>
    /// Encode <paramref name="symbol"/> using the encoder table, and update <paramref name="state"/> and
    /// <paramref name="out"/>. The caller must ensure we have enough bits available in the output accumulator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode(ref int state, ReadOnlySpan<FseEncoderEntry> encoderTable, ref FseOutStream @out,
        int symbol)
    {
        int s = state;
        FseEncoderEntry e = encoderTable[symbol];
        int s0 = e.S0;
        int k = e.K;

        // Number of bits to write
        bool hi = s >= s0;
        int nbits = hi ? k : k - 1;
        int delta = hi ? e.Delta0 : e.Delta1;

        // Write lower NBITS of state
        OutPush(ref @out, nbits, MaskLsb64((ulong)s, nbits));

        // Update state with remaining bits and delta (fse_state is 16 bits wide)
        state = (ushort)(delta + (s >> nbits));
    }

    /// <summary>
    /// Decode and return a symbol using the decoder table, and update <paramref name="state"/> and
    /// <paramref name="in"/>. The caller must ensure we have enough bits available in the input accumulator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Decode(ref int state, int[] decoderTable, ref FseInStream @in)
    {
        int e = decoderTable[state];

        // Update state from K bits of input + DELTA
        state = (ushort)((e >> 16) + (int)InPull(ref @in, e & 0xff));

        // Return the symbol for this state
        return (e >> 8) & 0xff;
    }

    /// <summary>
    /// Decode and return a value using the value decoder table, and update <paramref name="state"/> and
    /// <paramref name="in"/>. The caller must ensure we have enough bits available in the input accumulator.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ValueDecode(ref int state, FseValueDecoderEntry[] valueDecoderTable, ref FseInStream @in)
    {
        FseValueDecoderEntry entry = valueDecoderTable[state];
        uint stateAndValueBits = (uint)InPull(ref @in, entry.TotalBits);
        state = (ushort)(entry.Delta + (int)(stateAndValueBits >> entry.ValueBits));
        return entry.VBase + (int)(stateAndValueBits & ((1u << entry.ValueBits) - 1));
    }

    // MARK: - Tables

    // IMPORTANT: To properly decode an FSE encoded stream, both encoder/decoder tables shall be initialized with the
    // same parameters, including the FREQ[NSYMBOL] array.

    /// <summary>Sanity check on frequency table, verify sum of <paramref name="freq"/> is &lt;= number of states.</summary>
    internal static bool CheckFreq(ReadOnlySpan<ushort> freq, int numberOfStates)
    {
        int sumOfFreq = 0;
        foreach (ushort f in freq) sumOfFreq += f;
        return sumOfFreq <= numberOfStates;
    }

    /// <summary>
    /// Initialize encoder table <paramref name="t"/>[nsymbols]. <paramref name="nstates"/> = sum freq[i] is the number
    /// of states (a power of 2); freq[nsymbols] is a normalized histogram of symbol frequencies, with freq[i] &gt;= 0.
    /// Some symbols may have a 0 frequency. In that case they should not be present in the data.
    /// </summary>
    internal static void InitEncoderTable(int nstates, int nsymbols, ReadOnlySpan<ushort> freq,
        Span<FseEncoderEntry> t)
    {
        int offset = 0; // current offset
        int nClz = BitOperations.LeadingZeroCount((uint)nstates);
        for (int i = 0; i < nsymbols; i++)
        {
            int f = freq[i];
            if (f == 0) continue; // skip this symbol, no occurrences
            int k = BitOperations.LeadingZeroCount((uint)f) - nClz; // shift needed to ensure N <= (F<<K) < 2*N
            t[i].S0 = (short)((f << k) - nstates);
            t[i].K = (short)k;
            t[i].Delta0 = (short)(offset - f + (nstates >> k));
            // Unused when k == 0 (every state then takes the K-bit branch).
            t[i].Delta1 = k > 0 ? (short)(offset - f + (nstates >> (k - 1))) : (short)(offset - f);
            offset += f;
        }
    }

    /// <summary>
    /// Initialize decoder table <paramref name="t"/>[nstates]. Entries are packed as in the reference: bits 0-7 the
    /// number of bits to read, bits 8-15 the symbol, bits 16-31 the signed delta. States not covered by
    /// <paramref name="freq"/> are left untouched.
    /// </summary>
    /// <returns>false if the frequencies add up to more than <paramref name="nstates"/>.</returns>
    internal static bool InitDecoderTable(int nstates, int nsymbols, ReadOnlySpan<ushort> freq, Span<int> t)
    {
        int nClz = BitOperations.LeadingZeroCount((uint)nstates);
        int sumOfFreq = 0;
        int ti = 0;
        for (int i = 0; i < nsymbols; i++)
        {
            int f = freq[i];
            if (f == 0) continue; // skip this symbol, no occurrences

            sumOfFreq += f;
            if (sumOfFreq > nstates) return false;

            int k = BitOperations.LeadingZeroCount((uint)f) - nClz; // shift needed to ensure N <= (F<<K) < 2*N
            int j0 = ((2 * nstates) >> k) - f;

            // Initialize all states S reached by this symbol: OFFSET <= S < OFFSET + F
            for (int j = 0; j < f; j++)
            {
                int ek, delta;
                if (j < j0)
                {
                    ek = k;
                    delta = ((f + j) << k) - nstates;
                }
                else
                {
                    ek = k - 1;
                    delta = (j - j0) << (k - 1);
                }
                t[ti++] = (ek & 0xff) | (i << 8) | ((int)(short)delta << 16);
            }
        }
        return true;
    }

    /// <summary>
    /// Initialize value decoder table <paramref name="t"/>[nstates]. <paramref name="symbolVbits"/> and
    /// <paramref name="symbolVbase"/> are the number of value bits to read and the base value for each symbol. States
    /// not covered by <paramref name="freq"/> are left untouched.
    /// </summary>
    /// <returns>false if the frequencies add up to more than <paramref name="nstates"/>.</returns>
    internal static bool InitValueDecoderTable(int nstates, int nsymbols, ReadOnlySpan<ushort> freq,
        ReadOnlySpan<byte> symbolVbits, ReadOnlySpan<int> symbolVbase, Span<FseValueDecoderEntry> t)
    {
        int nClz = BitOperations.LeadingZeroCount((uint)nstates);
        int sumOfFreq = 0;
        int ti = 0;
        for (int i = 0; i < nsymbols; i++)
        {
            int f = freq[i];
            if (f == 0) continue; // skip this symbol, no occurrences

            sumOfFreq += f;
            if (sumOfFreq > nstates) return false;

            int k = BitOperations.LeadingZeroCount((uint)f) - nClz; // shift needed to ensure N <= (F<<K) < 2*N
            int j0 = ((2 * nstates) >> k) - f;

            FseValueDecoderEntry ei = default;
            ei.ValueBits = symbolVbits[i];
            ei.VBase = symbolVbase[i];

            // Initialize all states S reached by this symbol: OFFSET <= S < OFFSET + F
            for (int j = 0; j < f; j++)
            {
                FseValueDecoderEntry e = ei;
                if (j < j0)
                {
                    e.TotalBits = (byte)(k + e.ValueBits);
                    e.Delta = (short)(((f + j) << k) - nstates);
                }
                else
                {
                    e.TotalBits = (byte)(k - 1 + e.ValueBits);
                    e.Delta = (short)((j - j0) << (k - 1));
                }
                t[ti++] = e;
            }
        }
        return true;
    }

    /// <summary>Remove states from symbols until the correct number of states is used.</summary>
    private static void AdjustFreqs(Span<ushort> freq, int overrun, int nsymbols)
    {
        for (int shift = 3; overrun != 0; shift--)
        {
            for (int sym = 0; sym < nsymbols; sym++)
            {
                if (freq[sym] > 1)
                {
                    int n = (freq[sym] - 1) >> shift;
                    if (n > overrun) n = overrun;
                    freq[sym] -= (ushort)n;
                    overrun -= n;
                    if (overrun == 0) break;
                }
            }
        }
    }

    /// <summary>Normalize a table <paramref name="t"/>[nsymbols] of occurrences to <paramref name="freq"/>[nsymbols].</summary>
    internal static void NormalizeFreq(int nstates, int nsymbols, ReadOnlySpan<uint> t, Span<ushort> freq)
    {
        uint sCount = 0;
        int remaining = nstates; // must be signed; this may become < 0
        int maxFreq = 0;
        int maxFreqSym = 0;
        int shift = BitOperations.LeadingZeroCount((uint)nstates) - 1;
        uint highprecStep;

        // Compute the total number of symbol occurrences
        for (int i = 0; i < nsymbols; i++) sCount += t[i];

        if (sCount == 0)
            highprecStep = 0; // no symbols used
        else
            highprecStep = (1u << 31) / sCount;

        for (int i = 0; i < nsymbols; i++)
        {
            // Rescale the occurrence count to get the normalized frequency. Round up if the fractional part is >= 0.5;
            // otherwise round down. For efficiency, we do this calculation using integer arithmetic.
            int f = (int)((((t[i] * highprecStep) >> shift) + 1) >> 1);

            // If a symbol was used, it must be given a nonzero normalized frequency.
            if (f == 0 && t[i] != 0) f = 1;

            freq[i] = (ushort)f;
            remaining -= f;

            // Remember the maximum frequency and which symbol had it.
            if (f > maxFreq)
            {
                maxFreq = f;
                maxFreqSym = i;
            }
        }

        // If there remain states to be assigned, then just assign them to the most frequent symbol. Alternatively, if
        // we assigned more states than were actually available, then either remove states from the most frequent
        // symbol (for minor overruns) or use the slower adjustment algorithm (for major overruns).
        if (-remaining < (maxFreq >> 2))
            freq[maxFreqSym] = (ushort)(freq[maxFreqSym] + remaining);
        else
            AdjustFreqs(freq, -remaining, nsymbols);
    }
}
