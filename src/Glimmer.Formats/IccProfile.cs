// New file, no Swift original: the Mac embeds the profile CoreGraphics keeps for a colour space
// (CGColorSpace.copyICCData()). SkiaSharp embeds its own profiles in the PNG, JPEG and WebP it writes but has no call that
// gives their bytes, and Glimmer's TIFF and Photoshop writers need them. This writes the same profile, byte for byte, as
// Skia's SkWriteICCProfile for the four spaces export uses (checked against the profiles in Skia's own files in
// IccProfileTests).
using System.Buffers.Binary;
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats;

/// <summary>ICC version 4.3 display profiles for the export colour spaces, as Skia writes them.</summary>
public static class IccProfile
{
    private static readonly Lazy<byte[]>[] Cache =
    [
        new(() => Make(WorkingColorSpace.SRGB, linear: false)),
        new(() => Make(WorkingColorSpace.DisplayP3, linear: false)),
        new(() => Make(WorkingColorSpace.SRGB, linear: true)),
        new(() => Make(WorkingColorSpace.DisplayP3, linear: true)),
    ];

    /// <summary>The profile of <paramref name="space"/> with the sRGB curve, or with a linear one (what a 32-bit float
    /// file holds). A new array each time; callers may keep it.</summary>
    public static byte[] Rgb(WorkingColorSpace space, bool linear = false) =>
        (byte[])Cache[(space == WorkingColorSpace.DisplayP3 ? 1 : 0) + (linear ? 2 : 0)].Value.Clone();

    /// <summary>The Skia colour space for <paramref name="space"/>, gamma-encoded or linear (extended range).</summary>
    public static SKColorSpace ColorSpace(WorkingColorSpace space, bool linear = false) => (space, linear) switch
    {
        (WorkingColorSpace.DisplayP3, false) => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
        (WorkingColorSpace.DisplayP3, true) => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.DisplayP3),
        (_, true) => SKColorSpace.CreateSrgbLinear(),
        _ => SKColorSpace.CreateSrgb(),
    };

    private const uint Desc = 0x64657363, RXyz = 0x7258595A, GXyz = 0x6758595A, BXyz = 0x6258595A, Wtpt = 0x77747074,
                       RTrc = 0x72545243, GTrc = 0x67545243, BTrc = 0x62545243, Cprt = 0x63707274;

    // Skia's D50 white, as floats.
    private const float D50X = 0.9642f, D50Y = 1.0f, D50Z = 0.8249f;

    private static byte[] Make(WorkingColorSpace space, bool linear)
    {
        // Row-major: rows X, Y, Z; columns red, green, blue (SkiaSharp's Values are row after row).
        var m = (space == WorkingColorSpace.DisplayP3 ? SKColorSpaceXyz.DisplayP3 : SKColorSpaceXyz.Srgb).Values;
        string gamut = space == WorkingColorSpace.DisplayP3 ? "Display P3" : "sRGB";
        // Skia names the spaces it knows: plain "sRGB" for sRGB itself, "<gamut> Gamut with <curve> Transfer" otherwise.
        string description = space == WorkingColorSpace.SRGB && !linear ? "sRGB" : $"{gamut} Gamut with {(linear ? "Linear" : "sRGB")} Transfer";
        var tags = new List<(uint Signature, byte[]? Data)>
        {
            (Desc, Mluc(description)),
            (RXyz, Xyz(m[0], m[3], m[6])),
            (GXyz, Xyz(m[1], m[4], m[7])),
            (BXyz, Xyz(m[2], m[5], m[8])),
            (Wtpt, Xyz(D50X, D50Y, D50Z)),
            (RTrc, Curve(linear ? SKColorSpaceTransferFn.Linear : SKColorSpaceTransferFn.Srgb)),
            // No data: the tag shares the one before it, as Skia writes three equal curves.
            (GTrc, null),
            (BTrc, null),
            (Cprt, Mluc("Google Inc. 2016")),
        };
        int tableEnd = 128 + 4 + 12 * tags.Count;
        int size = tableEnd + tags.Sum(t => t.Data?.Length ?? 0);
        var icc = new byte[size];
        var span = icc.AsSpan();
        // The header.
        BinaryPrimitives.WriteUInt32BigEndian(span, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x04300000);         // version 4.3
        "mntrRGB XYZ "u8.CopyTo(span[12..]);                                   // display, RGB data, XYZ connection space
        BinaryPrimitives.WriteUInt16BigEndian(span[24..], 2016);              // creation date: 2016-01-01 00:00:00
        BinaryPrimitives.WriteUInt16BigEndian(span[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..], 1);
        "acsp"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32BigEndian(span[64..], 1);                 // relative colorimetric
        BinaryPrimitives.WriteInt32BigEndian(span[68..], Fixed(D50X));        // the connection space's illuminant
        BinaryPrimitives.WriteInt32BigEndian(span[72..], Fixed(D50Y));
        BinaryPrimitives.WriteInt32BigEndian(span[76..], Fixed(D50Z));
        // The tag table, then the tags.
        BinaryPrimitives.WriteUInt32BigEndian(span[128..], (uint)tags.Count);
        int offset = tableEnd, previousOffset = 0, previousSize = 0;
        for (int i = 0; i < tags.Count; i++)
        {
            var (signature, data) = tags[i];
            int entry = 132 + i * 12;
            if (data is not null)
            {
                data.CopyTo(span[offset..]);
                previousOffset = offset;
                previousSize = data.Length;
                offset += data.Length;
            }
            BinaryPrimitives.WriteUInt32BigEndian(span[entry..], signature);
            BinaryPrimitives.WriteUInt32BigEndian(span[(entry + 4)..], (uint)previousOffset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(entry + 8)..], (uint)previousSize);
        }
        return icc;
    }

    /// <summary>s15Fixed16, rounded as Skia rounds.</summary>
    private static int Fixed(float x) => (int)Math.Clamp(Math.Floor((double)x * 65536.0 + 0.5), int.MinValue, int.MaxValue);

    private static byte[] Xyz(float x, float y, float z)
    {
        var d = new byte[20];
        "XYZ "u8.CopyTo(d);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), Fixed(x));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), Fixed(y));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), Fixed(z));
        return d;
    }

    /// <summary>A parametric curve: the plain power form when the curve is one, the seven-parameter form otherwise.</summary>
    private static byte[] Curve(SKColorSpaceTransferFn fn)
    {
        bool power = fn.A == 1f && fn.B == 0f && fn.C == 0f && fn.D == 0f && fn.E == 0f && fn.F == 0f;
        float[] parameters = power ? [fn.G] : [fn.G, fn.A, fn.B, fn.C, fn.D, fn.E, fn.F];
        var d = new byte[12 + 4 * parameters.Length];
        "para"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8), (ushort)(power ? 0 : 4));
        for (int i = 0; i < parameters.Length; i++) BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12 + 4 * i), Fixed(parameters[i]));
        return d;
    }

    /// <summary>A one-record (en-US) multi-localised Unicode text, padded to four bytes.</summary>
    private static byte[] Mluc(string text)
    {
        int length = 28 + 2 * text.Length;
        var d = new byte[(length + 3) & ~3];
        "mluc"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 1);       // one record
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 12);     // of twelve bytes
        "enUS"u8.CopyTo(d.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), (uint)(2 * text.Length));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(24), 28);
        for (int i = 0; i < text.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(28 + 2 * i), text[i]);
        return d;
    }
}
