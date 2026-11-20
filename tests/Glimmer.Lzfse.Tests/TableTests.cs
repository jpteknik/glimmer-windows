namespace Glimmer.Lzfse.Tests;

/// <summary>The tables the codec computes instead of spelling out, checked against what the reference spells out.</summary>
public class TableTests
{
    [Fact]
    public void LzvnOpcodeTable_IsTheReferenceJumpTable()
    {
        // opc_tbl from lzvn_decode_base.c, one letter per opcode, extracted mechanically from the source:
        // d sml_d, M med_d, D lrg_d, p pre_d, m sml_m, G lrg_m, l sml_l, L lrg_l, n nop, e eos, u udef.
        const string reference =
            "ddddddeDddddddnDddddddnDdddddduDdddddduDdddddduDdddddduDdddddduD" +
            "ddddddpDddddddpDddddddpDddddddpDddddddpDddddddpDuuuuuuuuuuuuuuuu" +
            "ddddddpDddddddpDddddddpDddddddpDMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMM" +
            "ddddddpDddddddpDuuuuuuuuuuuuuuuuLlllllllllllllllGmmmmmmmmmmmmmmm";
        const string letters = "dMDpmGlLneu"; // indexed by the kinds SmlD = 0 ... Udef = 10

        string ours = string.Concat(LzvnDecoder.OpcodeKinds.Select(kind => letters[kind]));

        Assert.Equal(reference, ours);
    }

    [Fact]
    public void ValueToSymbol_PicksTheSymbolWhoseRangeHoldsTheValue()
    {
        // Each symbol stands for [base, base + 2^extra_bits); the ranges tile 0 .. max without gaps, so this is the
        // mapping lzfse_encode_tables.h spells out.
        AssertTiles(LzfseConstants.LBaseValue, LzfseConstants.LExtraBits, LzfseConstants.EncodeMaxLValue,
            LzfseConstants.LBaseFromValue);
        AssertTiles(LzfseConstants.MBaseValue, LzfseConstants.MExtraBits, LzfseConstants.EncodeMaxMValue,
            LzfseConstants.MBaseFromValue);
        AssertTiles(LzfseConstants.DBaseValue, LzfseConstants.DExtraBits, LzfseConstants.EncodeMaxDValue,
            LzfseConstants.DBaseFromValue);
    }

    private static void AssertTiles(ReadOnlySpan<int> baseValue, ReadOnlySpan<byte> extraBits, int max,
        Func<int, byte> symbolOf)
    {
        Assert.Equal(max + 1, baseValue[^1] + (1 << extraBits[^1]));
        for (int value = 0; value <= max; value++)
        {
            int s = symbolOf(value);
            if (value < baseValue[s] || value >= baseValue[s] + (1 << extraBits[s]))
                Assert.Fail($"value {value} maps to symbol {s}, whose range is [{baseValue[s]}, {baseValue[s] + (1 << extraBits[s])})");
        }
    }

    [Fact]
    public void NormalizeFreq_GivesEveryOccurringSymbolAStateAndUsesAllStates()
    {
        var random = new Random(5);
        foreach ((int nstates, int nsymbols) in new[] { (64, 20), (256, 64), (1024, 256) })
        {
            for (int trial = 0; trial < 500; trial++)
            {
                // Counts as a block has them (at most 40000 literals), from even to very skewed.
                var occurrences = new uint[nsymbols];
                int used = random.Next(1, nsymbols + 1);
                int largest = (int)Math.Pow(10, random.Next(1, 5));
                for (int i = 0; i < used; i++)
                    occurrences[random.Next(nsymbols)] += (uint)random.Next(1, Math.Max(2, largest / used));
                var freq = new ushort[nsymbols];

                Fse.NormalizeFreq(nstates, nsymbols, occurrences, freq);

                Assert.Equal(nstates, freq.Sum(f => f));
                for (int i = 0; i < nsymbols; i++) Assert.Equal(occurrences[i] > 0, freq[i] > 0);
            }
        }
    }

    [Fact]
    public void EncoderAndDecoderTables_AgreeOnEveryState()
    {
        // Encode a random symbol sequence with the encoder table and read it back with the decoder table.
        var random = new Random(11);
        const int nstates = 1024, nsymbols = 256;
        var occurrences = new uint[nsymbols];
        for (int i = 0; i < 2000; i++) occurrences[random.Next(40)]++;
        var freq = new ushort[nsymbols];
        Fse.NormalizeFreq(nstates, nsymbols, occurrences, freq);
        var encoder = new FseEncoderEntry[nsymbols];
        var decoder = new int[nstates];
        Fse.InitEncoderTable(nstates, nsymbols, freq, encoder);
        Assert.True(Fse.InitDecoderTable(nstates, nsymbols, freq, decoder));
        int[] symbols = Enumerable.Range(0, 4000).Select(_ => random.Next(40)).Where(s => freq[s] > 0).ToArray();

        // Encode backwards (as the block encoder does), flushing whole bytes as it goes. The payload starts 8 bytes
        // in: the reader keeps up to 63 bits buffered, so near the start of a payload it reads ahead into what
        // precedes it (in a stream, the block header), as the reference decoder does.
        var buffer = new byte[symbols.Length * 2 + 24];
        int pos = 8, state = 0;
        FseOutStream @out = default;
        for (int i = symbols.Length - 1; i >= 0; i--)
        {
            Fse.Encode(ref state, encoder, ref @out, symbols[i]);
            Fse.OutFlush(ref @out, buffer, ref pos);
        }
        Fse.OutFinish(ref @out, buffer, ref pos);

        FseInStream @in = default;
        int read = pos;
        Assert.True(Fse.InCheckedInit(ref @in, @out.AccumNbits, buffer, ref read, 0));
        int decodeState = state;
        foreach (int expected in symbols)
        {
            Assert.True(Fse.InCheckedFlush(ref @in, buffer, ref read, 0));
            Assert.Equal(expected, Fse.Decode(ref decodeState, decoder, ref @in));
        }
    }
}
