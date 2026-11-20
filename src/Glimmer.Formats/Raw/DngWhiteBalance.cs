// New file, no Swift original. On the Mac, CIRAWFilter reads a DNG's own white balance and reports it as
// neutralTemperature and neutralTint. On Windows the system's RAW codec may not say (IPlatformRawDecoder), so Glimmer reads
// it from a DNG itself, the way the DNG specification defines it (Adobe, Digital Negative Specification 1.7.1.0, 2023,
// "Mapping Camera Color Space to CIE XYZ Space"; the DNG SDK's dng_color_spec::NeutralToXY does the same):
//   - the camera's as-shot neutral (AsShotNeutral) is mapped to XYZ through the inverse of
//     AnalogBalance × CameraCalibration × ColorMatrix, the last two interpolated between the file's two calibration
//     illuminants by inverse temperature; as the temperature depends on the white being found, the mapping is iterated
//     from D50 until it settles;
//   - a file that gives the white's chromaticity directly (AsShotWhiteXY) is taken as it is;
//   - CameraCalibration counts only when its signature matches the profile's (CameraCalibrationSignature,
//     ProfileCalibrationSignature);
//   - the white is then temperature and tint on RawColor's scales (Planckian locus by Krystek, tint on Adobe's scale).
// Other RAW formats keep their white balance in the camera maker's own notes, with a colour matrix the file doesn't carry;
// for those Glimmer has only the system decoder's word.
#if GLIMMER_RAW   // Directory.Build.props switches this (Config/Features.xcconfig on the Mac)
using System.Buffers.Binary;

namespace Glimmer.Formats;

internal static class DngWhiteBalance
{
    private const ushort DngVersion = 50706, ColorMatrix1 = 50721, ColorMatrix2 = 50722, CameraCalibration1 = 50723,
                         CameraCalibration2 = 50724, AnalogBalance = 50727, AsShotNeutral = 50728, AsShotWhiteXY = 50729,
                         CalibrationIlluminant1 = 50778, CalibrationIlluminant2 = 50779,
                         CameraCalibrationSignature = 50931, ProfileCalibrationSignature = 50932;

    /// <summary>Where the iteration starts: D50, as in the DNG SDK.</summary>
    private const double StartX = 0.3457, StartY = 0.3585;

    /// <summary>
    /// The white balance a DNG says it was shot with, or null when <paramref name="file"/> is no DNG, lacks the tags, or
    /// holds values that make no white (a singular matrix, a neutral that isn't positive). Never throws.
    /// </summary>
    public static (double Temperature, double Tint)? AsShot(ReadOnlySpan<byte> file)
    {
        try
        {
            if (WhiteXy(file) is not { } xy) return null;
            var (temperature, tint) = RawColor.TemperatureTint(RawColor.UvOfXy(xy.X, xy.Y));
            return double.IsFinite(temperature) && double.IsFinite(tint) ? (temperature, tint) : null;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or ArithmeticException)
        {
            return null;
        }
    }

    /// <summary>The as-shot white's CIE 1931 xy chromaticity.</summary>
    internal static (double X, double Y)? WhiteXy(ReadOnlySpan<byte> d)
    {
        if (Directory0(d) is not { } ifd || !ifd.Entries.ContainsKey(DngVersion)) return null;

        if (Get(d, ifd, ColorMatrix1, 12) is not { Length: 9 or 12 } first) return null;
        int planes = first.Length / 3;

        // Given directly.
        if (Get(d, ifd, AsShotNeutral, 4) is not { } neutral)
        {
            if (Get(d, ifd, AsShotWhiteXY, 2) is [var wx, var wy] && wx > 0 && wy > 0 && wx + wy < 1) return (wx, wy);
            return null;
        }
        if (neutral.Length != planes || neutral.Any(v => !(v > 0))) return null;

        double[]? second = Get(d, ifd, ColorMatrix2, 12) is { } m2 && m2.Length == first.Length ? m2 : null;
        double t1 = IlluminantTemperature(Get(d, ifd, CalibrationIlluminant1, 1) is [var i1] ? (int)i1 : 0);
        double t2 = IlluminantTemperature(Get(d, ifd, CalibrationIlluminant2, 1) is [var i2] ? (int)i2 : 0);

        // CameraCalibration only when its signature is the profile's (both missing counts as the same).
        bool calibrated = SameText(Bytes(d, ifd, CameraCalibrationSignature), Bytes(d, ifd, ProfileCalibrationSignature));
        double[] identity = Identity(planes);
        double[] calibration1 = calibrated && Get(d, ifd, CameraCalibration1, 16) is { } c1 && c1.Length == planes * planes ? c1 : identity;
        double[] calibration2 = calibrated && Get(d, ifd, CameraCalibration2, 16) is { } c2 && c2.Length == planes * planes ? c2 : identity;
        double[] analog = Get(d, ifd, AnalogBalance, 4) is { } ab && ab.Length == planes && ab.All(v => v > 0) ? ab : [.. Enumerable.Repeat(1.0, planes)];

        // Two matrices only when both illuminants are known and differ; the lower temperature first.
        bool two = second is not null && t1 > 0 && t2 > 0 && t1 != t2;
        double[] matrix1 = first, matrix2 = two ? second! : first;
        if (two && t1 > t2)
        {
            (t1, t2) = (t2, t1);
            (matrix1, matrix2) = (matrix2, matrix1);
            (calibration1, calibration2) = (calibration2, calibration1);
        }

        double[] XyzToCamera(double x, double y)
        {
            double g = 1;
            if (two)
            {
                double t = RawColor.TemperatureTint(RawColor.UvOfXy(x, y)).Temperature;
                g = t <= t1 ? 1 : t >= t2 ? 0 : (1 / t - 1 / t2) / (1 / t1 - 1 / t2);
            }
            var colour = Blend(matrix1, matrix2, g);
            var camera = Blend(calibration1, calibration2, g);
            var balanced = new double[planes * planes];
            for (int r = 0; r < planes; r++)
                for (int c = 0; c < planes; c++) balanced[r * planes + c] = analog[r] * camera[r * planes + c];
            return Multiply(balanced, planes, planes, colour, 3);   // planes × 3
        }

        // The DNG SDK's iteration: at most 30 passes; a last pass that hasn't settled averages the last two (an
        // oscillation between two values is what keeps it from settling).
        double lastX = StartX, lastY = StartY;
        for (int pass = 0; pass < 30; pass++)
        {
            if (PseudoInverse(XyzToCamera(lastX, lastY), planes) is not { } inverse) return null;
            var xyz = Multiply(inverse, 3, planes, neutral, 1);
            double sum = xyz[0] + xyz[1] + xyz[2];
            if (!(sum > 0) || !double.IsFinite(sum)) return null;
            double nextX = xyz[0] / sum, nextY = xyz[1] / sum;
            if (!(nextX > 0 && nextY > 0 && nextX + nextY < 1)) return null;
            if (Math.Abs(nextX - lastX) + Math.Abs(nextY - lastY) < 1e-7) return (nextX, nextY);
            if (pass == 29) return ((lastX + nextX) / 2, (lastY + nextY) / 2);
            (lastX, lastY) = (nextX, nextY);
        }
        return (lastX, lastY);
    }

    /// <summary>The nominal temperature of an EXIF light source, as the DNG SDK assigns them (dng_camera_profile.cpp);
    /// 0 for one without (other, unknown).</summary>
    internal static double IlluminantTemperature(int lightSource) => lightSource switch
    {
        17 or 3 => 2850,              // standard light A, tungsten
        24 => 3200,                   // ISO studio tungsten
        23 => 5000,                   // D50
        20 or 1 or 9 or 4 or 18 => 5500,  // D55, daylight, fine weather, flash, standard light B
        21 or 19 or 10 => 6500,       // D65, standard light C, cloudy weather
        22 or 11 => 7500,             // D75, shade
        12 => (5700 + 7100) * 0.5,    // daylight fluorescent
        13 => (4600 + 5500) * 0.5,    // day white fluorescent
        14 or 2 => (3800 + 4500) * 0.5,   // cool white fluorescent, fluorescent
        15 => (3250 + 3800) * 0.5,    // white fluorescent
        16 => (2600 + 3250) * 0.5,    // warm white fluorescent
        _ => 0,
    };

    // MARK: Matrices (row-major)

    private static double[] Identity(int n)
    {
        var m = new double[n * n];
        for (int i = 0; i < n; i++) m[i * n + i] = 1;
        return m;
    }

    private static double[] Blend(double[] a, double[] b, double g)
    {
        var m = new double[a.Length];
        for (int i = 0; i < m.Length; i++) m[i] = g * a[i] + (1 - g) * b[i];
        return m;
    }

    private static double[] Multiply(double[] a, int rows, int inner, double[] b, int columns)
    {
        var m = new double[rows * columns];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                double s = 0;
                for (int k = 0; k < inner; k++) s += a[r * inner + k] * b[k * columns + c];
                m[r * columns + c] = s;
            }
        return m;
    }

    /// <summary>(MᵀM)⁻¹Mᵀ of a <paramref name="rows"/> × 3 matrix: its inverse for three colour planes, its least-squares
    /// inverse for four. Null when it has none.</summary>
    private static double[]? PseudoInverse(double[] m, int rows)
    {
        var transposed = new double[3 * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < 3; c++) transposed[c * rows + r] = m[r * 3 + c];
        var square = Multiply(transposed, 3, rows, m, 3);
        if (Invert3(square) is not { } inverse) return null;
        return Multiply(inverse, 3, 3, transposed, rows);
    }

    private static double[]? Invert3(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double ei = e * i - f * h, fg = f * g - d * i, dh = d * h - e * g;
        double det = a * ei + b * fg + c * dh;
        double scale = m.Max(Math.Abs);
        if (!double.IsFinite(det) || !(scale > 0) || Math.Abs(det) < 1e-12 * scale * scale * scale) return null;
        return
        [
            ei / det, (c * h - b * i) / det, (b * f - c * e) / det,
            fg / det, (a * i - c * g) / det, (c * d - a * f) / det,
            dh / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }

    // MARK: TIFF (classic, either byte order; DNG keeps these tags in the first directory)

    private readonly record struct Entry(ushort Type, uint Count, long Field);

    private sealed record Directory(bool Little, Dictionary<ushort, Entry> Entries);

    private static Directory? Directory0(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8) return null;
        bool little = d[0] == 'I' && d[1] == 'I', big = d[0] == 'M' && d[1] == 'M';
        if ((!little && !big) || U16(d, little, 2) != 42) return null;
        long at = U32(d, little, 4);
        if (at < 8 || at + 2 > d.Length) return null;
        int count = (int)U16(d, little, at);
        if (count == 0 || at + 2 + count * 12L > d.Length) return null;
        var entries = new Dictionary<ushort, Entry>();
        for (int i = 0; i < count; i++)
        {
            long e = at + 2 + i * 12L;
            entries.TryAdd((ushort)U16(d, little, e), new Entry((ushort)U16(d, little, e + 2), U32(d, little, e + 4), e + 8));
        }
        return new Directory(little, entries);
    }

    private static int Size(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    /// <summary>Where an entry's value starts: in the entry itself when it fits in four bytes, else at its offset. Null
    /// when it lies outside the file.</summary>
    private static long? ValueAt(ReadOnlySpan<byte> d, bool little, Entry e, int max)
    {
        int size = Size(e.Type);
        if (size == 0 || e.Count == 0 || e.Count > max) return null;
        long bytes = size * (long)e.Count;
        long at = bytes <= 4 ? e.Field : U32(d, little, e.Field);
        return at >= 0 && at + bytes <= d.Length ? at : null;
    }

    private static double[]? Get(ReadOnlySpan<byte> d, Directory ifd, ushort tag, int max) =>
        ifd.Entries.TryGetValue(tag, out var e) ? Numbers(d, ifd.Little, e, max) : null;

    private static double[]? Numbers(ReadOnlySpan<byte> d, bool little, Entry e, int max)
    {
        if (e.Type is 2 or 7 || ValueAt(d, little, e, max) is not { } at) return null;
        int size = Size(e.Type);
        var values = new double[e.Count];
        for (int i = 0; i < values.Length; i++)
        {
            long p = at + i * (long)size;
            values[i] = e.Type switch
            {
                1 => d[(int)p],
                6 => (sbyte)d[(int)p],
                3 => U16(d, little, p),
                8 => (short)U16(d, little, p),
                4 => U32(d, little, p),
                9 => (int)U32(d, little, p),
                11 => BitConverter.Int32BitsToSingle((int)U32(d, little, p)),
                5 => U32(d, little, p) / (double)U32(d, little, p + 4),
                10 => (int)U32(d, little, p) / (double)(int)U32(d, little, p + 4),
                _ => BitConverter.Int64BitsToDouble(little ? BinaryPrimitives.ReadInt64LittleEndian(d[(int)p..]) : BinaryPrimitives.ReadInt64BigEndian(d[(int)p..])),
            };
        }
        return values.All(double.IsFinite) ? values : null;
    }

    /// <summary>A text or byte entry's bytes without trailing zeros; empty when the tag is missing.</summary>
    private static byte[] Bytes(ReadOnlySpan<byte> d, Directory ifd, ushort tag)
    {
        if (!ifd.Entries.TryGetValue(tag, out var e) || e.Type is not (1 or 2 or 7) || ValueAt(d, ifd.Little, e, 4096) is not { } at) return [];
        var bytes = d.Slice((int)at, (int)e.Count);
        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0) length--;
        return bytes[..length].ToArray();
    }

    private static bool SameText(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private static uint U16(ReadOnlySpan<byte> d, bool little, long at) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(d[(int)at..]) : BinaryPrimitives.ReadUInt16BigEndian(d[(int)at..]);

    private static uint U32(ReadOnlySpan<byte> d, bool little, long at) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(d[(int)at..]) : BinaryPrimitives.ReadUInt32BigEndian(d[(int)at..]);
}
#endif
