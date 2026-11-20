// New: tests for DngWhiteBalance (no Swift original; on the Mac Apple's decoder reads a DNG's white balance). The DNGs are
// headers the tests write themselves, for a "camera" whose colour planes are linear sRGB, so the white each file holds is
// known: the files are made from a chosen white, and reading them must give it back.
#if GLIMMER_RAW
using System.Buffers.Binary;
using System.Text;

namespace Glimmer.Formats.Tests;

public class DngWhiteBalanceTests
{
    /// <summary>XYZ to linear sRGB (IEC 61966-2-1): a camera whose planes are linear sRGB has this as its ColorMatrix.</summary>
    internal static readonly double[] SrgbCamera =
    [
        3.2404542, -1.5371385, -0.4985314,
        -0.9692660, 1.8760108, 0.0415560,
        0.0556434, -0.2040259, 1.0572252,
    ];

    internal static double[] Xyz(double x, double y) => [x / y, 1, (1 - x - y) / y];

    private static (double X, double Y) XyOfUv((double U, double V) uv)
    {
        double d = 2 * uv.U - 8 * uv.V + 4;
        return (3 * uv.U / d, 2 * uv.V / d);
    }

    internal static double[] Times(double[] m, double[] v)
    {
        int rows = m.Length / v.Length;
        var r = new double[rows];
        for (int i = 0; i < rows; i++)
            for (int k = 0; k < v.Length; k++) r[i] += m[i * v.Length + k] * v[k];
        return r;
    }

    /// <summary>A DNG made from a white: its as-shot neutral is what the camera records for the white.</summary>
    private static byte[] Dng(double[] matrix, double x, double y, ushort illuminant = 21, bool little = true, params DngHeader.Tag[] more) =>
        DngHeader.Build(little,
        [
            DngHeader.Byte(50706, 1, 4, 0, 0),
            DngHeader.SRational(50721, matrix),
            DngHeader.Short(50778, illuminant),
            DngHeader.Rational(50728, Times(matrix, Xyz(x, y))),
            .. more,
        ]);

    [Fact]
    public void TheNeutralOfAKnownLightGivesItsTemperature()
    {
        // CIE standard illuminant A: a Planckian radiator at 2856 K, so its tint is 0.
        var a = DngWhiteBalance.AsShot(Dng(SrgbCamera, 0.44757, 0.40745));
        Assert.True(a is { } wa && Math.Abs(wa.Temperature - 2856) < 15 && Math.Abs(wa.Tint) < 1.5, $"A: {a}");
        // D65: 6504 K, 0.0032 off the locus towards green (a tint of about +10, as RawDevelopmentTests says).
        var d65 = DngWhiteBalance.AsShot(Dng(SrgbCamera, 0.31271, 0.32902));
        Assert.True(d65 is { } wd && Math.Abs(wd.Temperature - 6504) < 60 && Math.Abs(wd.Tint - 9.6) < 1.5, $"D65: {d65}");
        // Any white on or off the locus comes back.
        foreach (double temperature in new[] { 2_500.0, 3_200, 4_300, 5_500, 7_500, 12_000 })
        {
            foreach (double tint in new[] { 0.0, 15, -20 })
            {
                var (x, y) = XyOfUv(RawColor.White(temperature, tint));
                var back = DngWhiteBalance.AsShot(Dng(SrgbCamera, x, y));
                Assert.True(back is { } b && Math.Abs(b.Temperature - temperature) / temperature < 0.003 && Math.Abs(b.Tint - tint) < 0.2,
                            $"{temperature} K tint {tint}: {back}");
            }
        }
    }

    /// <summary>Two calibrations: the matrix for a white is the two blended by inverse temperature, and the white is the
    /// one whose own blend maps it to the neutral. Without the second matrix the same neutral says something else, so the
    /// test can tell.</summary>
    [Fact]
    public void TwoCalibrationsAreInterpolatedByInverseTemperature()
    {
        double[] underA = [.. Times3(Diagonal(1.25, 1.0, 0.7), SrgbCamera)];
        double[] underD65 = [.. Times3([0.92, 0.05, 0.0, 0.03, 1.0, 0.02, 0.0, -0.04, 1.12], SrgbCamera)];
        foreach (double truth in new[] { 3_000.0, 4_000, 5_200 })
        {
            // The DNG SDK's nominal temperatures: 2850 K for standard light A, 6500 K for D65.
            double g = (1 / truth - 1 / 6500.0) / (1 / 2850.0 - 1 / 6500.0);
            var blend = underA.Zip(underD65, (p, q) => g * p + (1 - g) * q).ToArray();
            var (x, y) = XyOfUv(RawColor.White(truth, 0));
            var neutral = Times(blend, Xyz(x, y));
            var file = DngHeader.Build(true,
            [
                DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, underD65), DngHeader.SRational(50722, underA),
                DngHeader.Short(50778, 21), DngHeader.Short(50779, 17), DngHeader.Rational(50728, neutral),
            ]);
            var back = DngWhiteBalance.AsShot(file);
            Assert.True(back is { } b && Math.Abs(b.Temperature - truth) / truth < 0.003 && Math.Abs(b.Tint) < 0.3, $"{truth} K: {back}");
            // The negative control: one matrix alone reads the same neutral as another white.
            var single = DngHeader.Build(true,
            [
                DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, underD65), DngHeader.Short(50778, 21), DngHeader.Rational(50728, neutral),
            ]);
            var wrong = DngWhiteBalance.AsShot(single);
            Assert.True(wrong is { } w && Math.Abs(w.Temperature - truth) / truth > 0.02, $"{truth} K through D65's matrix alone: {wrong}");
        }
    }

    [Fact]
    public void AWhiteGivenAsXyIsTakenAsItIs()
    {
        var (x, y) = XyOfUv(RawColor.White(3_200, 4));
        var file = DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, SrgbCamera), DngHeader.Rational(50729, [x, y])]);
        var back = DngWhiteBalance.AsShot(file);
        Assert.True(back is { } b && Math.Abs(b.Temperature - 3_200) < 10 && Math.Abs(b.Tint - 4) < 0.2, $"{back}");
    }

    /// <summary>AnalogBalance and CameraCalibration are part of the mapping; CameraCalibration only when its signature is
    /// the profile's.</summary>
    [Fact]
    public void CalibrationAndAnalogBalanceArePartOfTheMapping()
    {
        var (x, y) = XyOfUv(RawColor.White(4_500, 0));
        double[] calibration = Diagonal(1.3, 1.0, 0.8);
        double[] analog = [0.9, 1.0, 1.2];
        // What the camera records: AnalogBalance × CameraCalibration × ColorMatrix applied to the white.
        var recorded = Times(Times3(Diagonal(analog[0], analog[1], analog[2]), Times3(calibration, SrgbCamera)), Xyz(x, y));
        DngHeader.Tag[] Tags(params DngHeader.Tag[] signatures) =>
        [
            DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, SrgbCamera), DngHeader.SRational(50723, calibration),
            DngHeader.Rational(50727, analog), DngHeader.Rational(50728, recorded), .. signatures,
        ];
        foreach (var signatures in new DngHeader.Tag[][] { [], [DngHeader.Ascii(50931, "com.example.camera"), DngHeader.Ascii(50932, "com.example.camera")] })
        {
            var back = DngWhiteBalance.AsShot(DngHeader.Build(true, Tags(signatures)));
            Assert.True(back is { } b && Math.Abs(b.Temperature - 4_500) < 15 && Math.Abs(b.Tint) < 0.3, $"{signatures.Length} signatures: {back}");
        }
        // A calibration made for another profile is not applied, so the same neutral reads differently.
        var other = DngWhiteBalance.AsShot(DngHeader.Build(true, Tags(DngHeader.Ascii(50931, "com.example.camera"), DngHeader.Ascii(50932, "com.example.other"))));
        Assert.True(other is { } o && Math.Abs(o.Temperature - 4_500) > 100, $"{other}");
    }

    [Fact]
    public void FourColourPlanesUseTheLeastSquaresInverse()
    {
        // A fourth plane that mixes the other three, as a CYGM sensor's would.
        double[] four = [.. SrgbCamera, 0.4 * SrgbCamera[0] + 0.6 * SrgbCamera[3], 0.4 * SrgbCamera[1] + 0.6 * SrgbCamera[4], 0.4 * SrgbCamera[2] + 0.6 * SrgbCamera[5]];
        var (x, y) = XyOfUv(RawColor.White(3_800, -8));
        var back = DngWhiteBalance.AsShot(Dng(four, x, y));
        Assert.True(back is { } b && Math.Abs(b.Temperature - 3_800) < 12 && Math.Abs(b.Tint + 8) < 0.3, $"{back}");
    }

    [Fact]
    public void BigEndianFilesReadTheSame()
    {
        var (x, y) = XyOfUv(RawColor.White(6_000, 6));
        var little = DngWhiteBalance.AsShot(Dng(SrgbCamera, x, y, little: true));
        var big = DngWhiteBalance.AsShot(Dng(SrgbCamera, x, y, little: false));
        Assert.True(little is not null && big is not null && Math.Abs(little.Value.Temperature - big.Value.Temperature) < 1e-6, $"{little} {big}");
        Assert.True(Math.Abs(big!.Value.Temperature - 6_000) < 20, $"{big}");
    }

    [Fact]
    public void WhatIsNotADngOrIsDamagedGivesNothingAndNeverThrows()
    {
        var (x, y) = XyOfUv(RawColor.White(5_000, 0));
        var good = Dng(SrgbCamera, x, y);
        Assert.NotNull(DngWhiteBalance.AsShot(good));
        // Not a DNG: the same tags without the version tag, a camera RAW TIFF, an ordinary picture, nothing.
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.SRational(50721, SrgbCamera), DngHeader.Rational(50728, [1, 1, 1])])));
        Assert.Null(DngWhiteBalance.AsShot(HostileFiles.Tiff(HostileFiles.Picture(), 96, 64)));
        Assert.Null(DngWhiteBalance.AsShot([]));
        Assert.Null(DngWhiteBalance.AsShot("II*\0"u8));
        // A DNG that doesn't say: no as-shot white, no colour matrix.
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, SrgbCamera)])));
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.Rational(50728, [1, 1, 1])])));
        // Values that make no white: a zero in the neutral, a singular matrix, a neutral of the wrong length.
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, SrgbCamera), DngHeader.Rational(50728, [1, 0, 1])])));
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, [1, 2, 3, 2, 4, 6, 1, 1, 1]), DngHeader.Rational(50728, [1, 1, 1])])));
        Assert.Null(DngWhiteBalance.AsShot(DngHeader.Build(true, [DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, SrgbCamera), DngHeader.Rational(50728, [1, 1])])));
        // Cut short anywhere, or with every byte of its header changed: an answer or nothing, never an exception.
        for (int length = 0; length < good.Length; length++) _ = DngWhiteBalance.AsShot(good.AsSpan(0, length));
        var random = new Random(7);
        for (int i = 0; i < good.Length; i++)
        {
            var bad = (byte[])good.Clone();
            bad[i] = (byte)random.Next(256);
            _ = DngWhiteBalance.AsShot(bad);
        }
    }

    // MARK: Helpers

    private static double[] Diagonal(double a, double b, double c) => [a, 0, 0, 0, b, 0, 0, 0, c];

    private static double[] Times3(double[] a, double[] b)
    {
        int rows = a.Length / 3;
        var m = new double[rows * 3];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++) m[r * 3 + c] += a[r * 3 + k] * b[k * 3 + c];
        return m;
    }
}

/// <summary>RawDevelopment with a DNG's own white balance and with a decoder's word for other RAW files.</summary>
[Collection("Raw")]
public class RawWhiteBalanceSourceTests
{
    /// <summary>A system decoder that takes any file for a camera RAW file and says the white balance it was given.</summary>
    private sealed class SayingDecoder((double, double)? says) : IPlatformRawDecoder
    {
        public bool IsRaw(ReadOnlyMemory<byte> data) => true;
        public (double Temperature, double Tint)? AsShotWhiteBalance(ReadOnlyMemory<byte> data) => says;
        public IReadOnlyList<string> Cameras => [];
        public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data) => (64, 48);
        public DecodedImage Decode(ReadOnlyMemory<byte> data) => throw new InvalidDataException("not decoded in these tests");
    }

    /// <summary>A camera RAW TIFF that is no DNG (a colour filter array, as NEF and ARW are).</summary>
    private static readonly byte[] NotDng = DngHeader.Build(true, [DngHeader.Long(256, 64), DngHeader.Long(257, 48), DngHeader.Short(262, 32803)]);

    [Fact]
    public void ADngsOwnWhiteBalanceComesFirst()
    {
        try
        {
            var (x, y) = (0.44757, 0.40745);   // standard illuminant A
            var file = DngHeader.Build(true,
            [
                DngHeader.Byte(50706, 1, 4, 0, 0), DngHeader.SRational(50721, DngWhiteBalanceTests.SrgbCamera), DngHeader.Short(50778, 21),
                DngHeader.Rational(50728, DngWhiteBalanceTests.Times(DngWhiteBalanceTests.SrgbCamera, DngWhiteBalanceTests.Xyz(x, y))),
            ]);
            RawDevelopment.Decoder = new SayingDecoder((7_000, 30));
            var info = RawDevelopment.Info(file);
            Assert.True(Math.Abs(info.Temperature - 2856) < 15 && Math.Abs(info.Tint) < 1.5, $"{info.Temperature} K tint {info.Tint}");
            // The tests' own DNG: identity colour matrices and an equal neutral are an equal-energy white (illuminant E,
            // 5454 K), what its file says rather than what the stand-in decoder says (6500 K).
            RawDevelopment.Decoder = new TestDngDecoder();
            var ramp = RawDevelopment.Info(DNGTestWriter.Ramp);
            Assert.True(ramp.Temperature is > 5_420 and < 5_490, $"{ramp.Temperature} K tint {ramp.Tint}");
        }
        finally
        {
            RawDevelopment.Decoder = new TestDngDecoder();
        }
    }

    [Fact]
    public void ForOtherRawFilesTheDecoderSpeaksAndWithoutItDaylight()
    {
        try
        {
            RawDevelopment.Decoder = new SayingDecoder((3_200, 7));
            var info = RawDevelopment.Info(NotDng);
            Assert.True(info.Temperature == 3_200 && info.Tint == 7, $"{info.Temperature} K tint {info.Tint}");
            // Out of range is clamped to what can be set; not finite is no answer.
            RawDevelopment.Decoder = new SayingDecoder((90_000, -400));
            info = RawDevelopment.Info(NotDng);
            Assert.True(info.Temperature == 50_000 && info.Tint == -150);
            RawDevelopment.Decoder = new SayingDecoder((double.NaN, 0));
            Assert.Equal(6_500, RawDevelopment.Info(NotDng).Temperature);
            RawDevelopment.Decoder = new SayingDecoder(null);
            info = RawDevelopment.Info(NotDng);
            Assert.True(info.Temperature == 6_500 && info.Tint == 0);
        }
        finally
        {
            RawDevelopment.Decoder = new TestDngDecoder();
        }
    }
}

/// <summary>New: the first directory of a TIFF with the given tags, in either byte order (no picture: DngWhiteBalance and
/// RawContainer read only the header).</summary>
internal static class DngHeader
{
    public sealed record Tag(ushort Id, ushort Type, int Count, Action<List<byte>, bool> Write);

    private static void Put16(List<byte> o, bool little, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v);
        o.AddRange(b.ToArray());
    }

    private static void Put32(List<byte> o, bool little, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v);
        o.AddRange(b.ToArray());
    }

    public static Tag Byte(ushort id, params byte[] v) => new(id, 1, v.Length, (o, _) => o.AddRange(v));
    public static Tag Ascii(ushort id, string s) { byte[] d = [.. Encoding.ASCII.GetBytes(s), 0]; return new(id, 2, d.Length, (o, _) => o.AddRange(d)); }
    public static Tag Short(ushort id, params ushort[] v) => new(id, 3, v.Length, (o, l) => { foreach (var x in v) Put16(o, l, x); });
    public static Tag Long(ushort id, params uint[] v) => new(id, 4, v.Length, (o, l) => { foreach (var x in v) Put32(o, l, x); });

    /// <summary>Unsigned rationals over 10⁶.</summary>
    public static Tag Rational(ushort id, double[] v) => new(id, 5, v.Length, (o, l) =>
    {
        foreach (var x in v) { Put32(o, l, (uint)Math.Round(x * 1e6)); Put32(o, l, 1_000_000); }
    });

    /// <summary>Signed rationals over 10⁶.</summary>
    public static Tag SRational(ushort id, double[] v) => new(id, 10, v.Length, (o, l) =>
    {
        foreach (var x in v) { Put32(o, l, unchecked((uint)(int)Math.Round(x * 1e6))); Put32(o, l, 1_000_000); }
    });

    public static byte[] Build(bool little, Tag[] tags)
    {
        var sorted = tags.OrderBy(t => t.Id).ToList();
        const int directory = 8;
        int extraAt = directory + 2 + sorted.Count * 12 + 4;
        var head = new List<byte>(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        Put16(head, little, 42);
        Put32(head, little, directory);
        Put16(head, little, (ushort)sorted.Count);
        var extra = new List<byte>();
        foreach (var tag in sorted)
        {
            var value = new List<byte>();
            tag.Write(value, little);
            Put16(head, little, tag.Id);
            Put16(head, little, tag.Type);
            Put32(head, little, (uint)tag.Count);
            if (value.Count <= 4)
            {
                head.AddRange(value);
                head.AddRange(new byte[4 - value.Count]);
            }
            else
            {
                Put32(head, little, (uint)(extraAt + extra.Count));
                extra.AddRange(value);
                if (extra.Count % 2 == 1) extra.Add(0);
            }
        }
        Put32(head, little, 0);
        return [.. head, .. extra, .. new byte[16]];
    }
}
#endif
