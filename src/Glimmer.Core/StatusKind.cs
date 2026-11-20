// Ported from Packages/GlimmerKit/Sources/GlimmerCore/StatusKind.swift

namespace Glimmer.Core;

/// <summary>
/// A state said in the window: an error, a warning, a notice, a success, or work under way (the-apps/SIBLINGS.md,
/// "Tillstånd i fönstret: fel, varning, upplysning").
/// </summary>
/// <remarks>
/// They must be told apart without colour, in light and dark, and by someone who cannot tell red from green. So each
/// kind has one symbol, used for nothing else in the whole app, and a prefix that a screen reader speaks; the colour
/// sits on the symbol and the text stays in the label colour. This one type gives all three, so that no view picks a
/// colour or a symbol for a state by itself.
///
/// Why the shape and the word, and not the colour, carry it: the error and warning colours below differ in luminance
/// by 1.56 : 1 on a dark surface and 1.01 : 1 on a light one. Seen without hue they are the same.
/// </remarks>
public enum StatusKind
{
    /// <summary>Something could not be done. The text says what.</summary>
    [RawValue("error")] Error,
    /// <summary>Something was done, or stands, with a consequence the person may need to act on. The text says the consequence.</summary>
    [RawValue("warning")] Warning,
    /// <summary>Something worth knowing; nothing to act on.</summary>
    [RawValue("notice")] Notice,
    [RawValue("success")] Success,
    /// <summary>Work under way: shown with a progress indicator and the word, not with a symbol.</summary>
    [RawValue("working")] Working,
}

public static class StatusKindExtensions
{
    extension(StatusKind)
    {
        public static IReadOnlyList<StatusKind> AllCases => RawValues<StatusKind>.AllCases;

        /// <summary>WCAG relative luminance of an sRGB colour.</summary>
        public static double Luminance((double Red, double Green, double Blue) c) => ColorMath.RelativeLuminance(c.Red, c.Green, c.Blue);

        /// <summary>WCAG contrast ratio of two sRGB colours.</summary>
        public static double Contrast((double Red, double Green, double Blue) a, (double Red, double Green, double Blue) b)
        {
            double la = ColorMath.RelativeLuminance(a.Red, a.Green, a.Blue), lb = ColorMath.RelativeLuminance(b.Red, b.Green, b.Blue);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }

    public static string RawValue(this StatusKind kind) => RawValues<StatusKind>.Of(kind);

    /// <summary>
    /// The SF Symbol the Mac shows; null for <see cref="StatusKind.Working"/>. The Windows app maps these names to its own
    /// icons, keeping the shapes: a square for the error, a triangle for the warning, circles for notice and success.
    /// </summary>
    public static string? Symbol(this StatusKind kind) => kind switch
    {
        // A cross in a filled square. Not the octagon: at 12 to 16 points it can't be told from the circle that carries
        // the check for "done", and for someone who can't tell red from green the two have the same colour (Flimmer's
        // simulation, 2026-11-17). Square against circle against triangle shows at every size.
        StatusKind.Error => "xmark.square.fill",
        StatusKind.Warning => "exclamationmark.triangle.fill",
        StatusKind.Notice => "info.circle",
        StatusKind.Success => "checkmark.circle.fill",
        _ => null,
    };

    /// <summary>What a screen reader says before the text ("Error: …"); a catalogue key. A notice and work under way need no prefix.</summary>
    public static string? Prefix(this StatusKind kind) => kind switch
    {
        StatusKind.Error => "Error:",
        StatusKind.Warning => "Warning:",
        StatusKind.Success => "Done:",
        _ => null,
    };

    /// <summary>
    /// The symbol's colour as sRGB (0…1) on a dark surface (Glimmer's own windows, and the system's dark ones) and on a
    /// light one. At least 3 : 1 against every surface it stands on (<c>StatusKindTests</c>).
    /// </summary>
    public static (double Red, double Green, double Blue) Color(this StatusKind kind, bool onDark)
    {
        uint hex = (kind, onDark) switch
        {
            (StatusKind.Error, true) => 0xff6b6b,
            (StatusKind.Warning, true) => 0xffb340,
            (StatusKind.Notice or StatusKind.Working, true) => 0x969aa7,
            (StatusKind.Success, true) => 0x4cd787,
            (StatusKind.Error, false) => 0xc22a26,
            (StatusKind.Warning, false) => 0x9a5600,
            (StatusKind.Notice or StatusKind.Working, false) => 0x5f636f,
            (StatusKind.Success, false) => 0x1a733d,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return (((hex >> 16) & 0xff) / 255.0, ((hex >> 8) & 0xff) / 255.0, (hex & 0xff) / 255.0);
    }
}
