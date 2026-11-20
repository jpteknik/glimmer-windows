// Ported from lzvn_decode_base.c and lzvn_decode_base.h of the LZFSE reference implementation,
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

/// <summary>LZVN low-level decoder, for the payload of a <c>bvxn</c> block.</summary>
internal static class LzvnDecoder
{
    // Opcode kinds; the reference dispatches on a 256-entry jump table with these labels.
    internal const byte SmlD = 0; // LLMMMDDD DDDDDDDD LITERAL: small distance
    internal const byte MedD = 1; // 101LLMMM DDDDDDMM DDDDDDDD LITERAL: medium distance
    internal const byte LrgD = 2; // LLMMM111 DDDDDDDD DDDDDDDD LITERAL: large distance
    internal const byte PreD = 3; // LLMMM110 LITERAL: previous distance
    internal const byte SmlM = 4; // 1111MMMM: small match, previous distance
    internal const byte LrgM = 5; // 11110000 MMMMMMMM: large match, previous distance
    internal const byte SmlL = 6; // 1110LLLL LITERAL: small literal
    internal const byte LrgL = 7; // 11100000 LLLLLLLL LITERAL: large literal
    internal const byte Nop = 8;
    internal const byte Eos = 9;  // end of stream, followed by 7 bytes of padding
    internal const byte Udef = 10;

    /// <summary>The opcode table (opc_tbl in the reference).</summary>
    internal static readonly byte[] OpcodeKinds = BuildOpcodeKinds();

    private static byte[] BuildOpcodeKinds()
    {
        var kinds = new byte[256];
        for (int opc = 0; opc < 256; opc++)
        {
            kinds[opc] = opc switch
            {
                240 => LrgM,
                > 240 => SmlM,
                224 => LrgL,
                > 224 => SmlL,
                >= 208 => Udef,
                >= 160 and < 192 => MedD,
                >= 112 and < 128 => Udef,
                _ => (opc & 7) switch
                {
                    7 => LrgD,
                    6 => opc switch
                    {
                        6 => Eos,
                        14 or 22 => Nop,
                        < 64 => Udef,
                        _ => PreD,
                    },
                    _ => SmlD,
                },
            };
        }
        return kinds;
    }

    /// <summary>
    /// Decodes one LZVN block. <paramref name="src"/> is exactly its payload (n_payload_bytes), and it must produce
    /// exactly <paramref name="nRawBytes"/> bytes, ending with the end-of-stream opcode at the end of the payload.
    /// Matches may reach back into earlier blocks' output.
    /// </summary>
    /// <exception cref="InvalidDataException">The block is invalid or truncated.</exception>
    internal static void DecodeBlock(ReadOnlySpan<byte> src, DecodeOutput output, int nRawBytes)
    {
        int blockEnd = output.Length + nRawBytes; // the caller checked this is within the output limit
        byte[] dst = output.Buffer;
        int d = output.Length;
        int s = 0;
        int D = 0; // distance for last emitted match, or 0

        while (true)
        {
            // Every valid stream is terminated with an eos opcode, so there is always one more opcode to read.
            if (s >= src.Length) throw Truncated();
            int opc = src[s];
            int srcLen = src.Length - s;
            int L, M, opcLen;

            switch (OpcodeKinds[opc])
            {
                //  These four opcodes (sml_d, med_d, lrg_d, and pre_d) encode both a literal and a match. Each only
                //  works out the opcode length, the literal length, the match length and the distance. The checks
                //  ensure the source can hold the full opcode, the literal, and at least one byte of the next opcode.
                case SmlD:
                    opcLen = 2;
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    if (srcLen <= opcLen + L) throw Truncated();
                    D = ((opc & 7) << 8) | src[s + 1];
                    break;

                case MedD:
                    opcLen = 3;
                    L = (opc >> 3) & 3;
                    if (srcLen <= opcLen + L) throw Truncated();
                    int opc23 = src[s + 1] | (src[s + 2] << 8);
                    M = (((opc & 7) << 2) | (opc23 & 3)) + 3;
                    D = opc23 >> 2;
                    break;

                case LrgD:
                    opcLen = 3;
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    if (srcLen <= opcLen + L) throw Truncated();
                    D = src[s + 1] | (src[s + 2] << 8);
                    break;

                case PreD:
                    opcLen = 1;
                    L = opc >> 6;
                    M = ((opc >> 3) & 7) + 3;
                    if (srcLen <= opcLen + L) throw Truncated();
                    break;

                //  Opcodes representing only a match (no literal). The match distance is carried over from the
                //  previous opcode.
                case SmlM:
                    if (srcLen <= 1) throw Truncated();
                    M = opc & 15;
                    s += 1;
                    CopyMatch(ref dst, ref d, D, M, blockEnd, output);
                    continue;

                case LrgM:
                    //  Matches smaller than 16 bytes can be represented by sml_m, so there is an implicit bias of 16 on
                    //  the match length; the representable values are [16,271].
                    if (srcLen <= 2) throw Truncated();
                    M = src[s + 1] + 16;
                    s += 2;
                    CopyMatch(ref dst, ref d, D, M, blockEnd, output);
                    continue;

                //  Opcodes representing only a literal (no match). D must be preserved between opcodes.
                case SmlL:
                    opcLen = 1;
                    L = opc & 15;
                    if (srcLen <= opcLen + L) throw Truncated();
                    s += opcLen;
                    CopyLiteral(src, ref s, ref dst, ref d, L, blockEnd, output);
                    continue;

                case LrgL:
                    opcLen = 2;
                    if (srcLen <= 2) throw Truncated();
                    L = src[s + 1] + 16;
                    if (srcLen <= opcLen + L) throw Truncated();
                    s += opcLen;
                    CopyLiteral(src, ref s, ref dst, ref d, L, blockEnd, output);
                    continue;

                case Nop:
                    if (srcLen <= 1) throw Truncated();
                    s += 1;
                    continue;

                case Eos:
                    if (srcLen < 8) throw Truncated(); // here we don't need an extra byte for next op code
                    s += 8;
                    // The block must end exactly here, having produced exactly its declared size.
                    if (s != src.Length || d != blockEnd) throw Corrupt("block does not end where its header says");
                    output.Length = d;
                    return;

                default: // udef
                    throw Corrupt("undefined opcode");
            }

            //  Common implementation of writing data for opcodes that have both a literal and a match.
            s += opcLen;
            CopyLiteral(src, ref s, ref dst, ref d, L, blockEnd, output);
            //  Matches must not reference bytes that precede the start of the output, nor can the match distance be
            //  zero.
            if (D > d || D == 0) throw Corrupt("invalid match distance");
            CopyMatch(ref dst, ref d, D, M, blockEnd, output);
        }
    }

    private static void CopyLiteral(ReadOnlySpan<byte> src, ref int s, ref byte[] dst, ref int d, int length,
        int blockEnd, DecodeOutput output)
    {
        Reserve(ref dst, d, length, blockEnd, output);
        src.Slice(s, length).CopyTo(dst.AsSpan(d, length));
        s += length;
        d += length;
    }

    private static void CopyMatch(ref byte[] dst, ref int d, int distance, int length, int blockEnd,
        DecodeOutput output)
    {
        // sml_m and lrg_m reuse the previous distance, which is 0 at the start of a block. (The reference does not
        // check this and copies the destination onto itself.)
        if (distance == 0) throw Corrupt("match without a previous distance");
        Reserve(ref dst, d, length, blockEnd, output);
        LzfseDecoder.CopyMatch(dst, d, distance, length);
        d += length;
    }

    private static void Reserve(ref byte[] dst, int d, int count, int blockEnd, DecodeOutput output)
    {
        // Writing past the block's declared size means the block cannot end where it says it does.
        if (count > blockEnd - d) throw Corrupt("block writes past its declared size");
        if (count > dst.Length - d)
        {
            output.Length = d;
            output.Reserve(count);
            dst = output.Buffer;
        }
    }

    private static InvalidDataException Truncated() => new("LZFSE stream is truncated (LZVN block).");

    private static InvalidDataException Corrupt(string what) => new($"LZFSE stream is corrupt (LZVN block: {what}).");
}
