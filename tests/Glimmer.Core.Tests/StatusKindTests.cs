// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/StatusKindTests.swift

namespace Glimmer.Core.Tests;

/// <summary>
/// The states said in the window (SIBLINGS.md, "Tillstånd i fönstret"): one symbol each, and a colour that stands out
/// from every surface it is shown on.
/// </summary>
public class StatusKindTests
{
    private static (double Red, double Green, double Blue) Rgb(uint hex) =>
        (((hex >> 16) & 0xff) / 255.0, ((hex >> 8) & 0xff) / 255.0, (hex & 0xff) / 255.0);

    /// <summary>Glimmer's own surfaces (App/Theme.swift): background, panel, raised. These are exact.</summary>
    private static readonly uint[] GlimmerDark = [0x0c0d11, 0x16171c, 0x1f2027];
    /// <summary>
    /// The system's surfaces where settings and sheets stand. Dark: sampled from a picture of the Mac's Settings
    /// (window #232222, form row #2a2929, 2026-11-17); the two lighter greys are kept as the harder bound.
    /// </summary>
    private static readonly uint[] SystemDark = [0x232222, 0x2a2929, 0x323232, 0x3a3a3c];
    /// <summary>Light: sampled from a picture of Settings (window #ffffff, form row #f7f7f7, 2026-11-17); #ececec kept as the darker bound.</summary>
    private static readonly uint[] SystemLight = [0xececec, 0xf7f7f7, 0xffffff];

    [Fact]
    public void EachKindHasItsOwnSymbolAndTheTriangleIsOnlyAWarning()
    {
        var symbols = StatusKind.AllCases.Select(k => k.Symbol()).OfType<string>().ToList();
        Assert.Equal(symbols.Count, symbols.ToHashSet().Count);   // one symbol, one meaning
        Assert.Contains("triangle", StatusKind.Warning.Symbol());
        // A square for the error, a circle for done, a triangle for the warning: three outlines that differ at every size.
        Assert.True(StatusKind.Error.Symbol()!.Contains("square") && !StatusKind.Error.Symbol()!.Contains("triangle"));
        Assert.True(!StatusKind.Error.Symbol()!.Contains("circle") && !StatusKind.Error.Symbol()!.Contains("octagon"));
        Assert.Contains("circle", StatusKind.Success.Symbol());
        Assert.Null(StatusKind.Working.Symbol());
        Assert.True(StatusKind.Error.Prefix() == "Error:" && StatusKind.Warning.Prefix() == "Warning:");
    }

    [Fact]
    public void TheSymbolColourStandsOutFromEverySurface()
    {
        foreach (var kind in StatusKind.AllCases)
        {
            foreach (var surface in GlimmerDark.Concat(SystemDark))
            {
                double ratio = StatusKind.Contrast(kind.Color(onDark: true), Rgb(surface));
                Assert.True(ratio >= 3, $"{kind} on dark {surface:x}: {ratio}");
            }
            // On Glimmer's own surfaces the colours would pass as text too.
            foreach (var surface in GlimmerDark)
                Assert.True(StatusKind.Contrast(kind.Color(onDark: true), Rgb(surface)) >= 4.5, $"{kind} on {surface:x}");
            foreach (var surface in SystemLight)
            {
                double ratio = StatusKind.Contrast(kind.Color(onDark: false), Rgb(surface));
                Assert.True(ratio >= 4.5, $"{kind} on light {surface:x}: {ratio}");
            }
        }
    }

    /// <summary>
    /// The figures behind the rule: without hue, an error and a warning are the same colour. Kept as a test so the claim
    /// in the notes is a measured one; it is the shape and the prefix that tell them apart.
    /// </summary>
    [Fact]
    public void ErrorAndWarningAreNotToldApartByLuminance()
    {
        double dark = StatusKind.Contrast(StatusKind.Error.Color(onDark: true), StatusKind.Warning.Color(onDark: true));
        double light = StatusKind.Contrast(StatusKind.Error.Color(onDark: false), StatusKind.Warning.Color(onDark: false));
        Assert.True(dark < 1.6 && light < 1.05, $"dark {dark}, light {light}");
    }

    /// <summary>The status colours are not the accent (indigo, #6d7dff): each is far from it in hue, or has no hue (the grey).</summary>
    [Fact]
    public void NoStatusColourIsTheAccent()
    {
        static (double Hue, double Saturation) Hue((double Red, double Green, double Blue) c)
        {
            double hi = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), lo = Math.Min(c.Red, Math.Min(c.Green, c.Blue)), d = hi - lo;
            if (!(d > 0)) return (0, 0);
            double h = hi == c.Red ? (c.Green - c.Blue) / d : hi == c.Green ? 2 + (c.Blue - c.Red) / d : 4 + (c.Red - c.Green) / d;
            h = h * 60 % 360;
            if (h < 0) h += 360;
            return (h, d / hi);
        }
        var accent = Hue(Rgb(0x6d7dff));
        foreach (var kind in StatusKind.AllCases)
        {
            foreach (var dark in new[] { true, false })
            {
                var c = Hue(kind.Color(onDark: dark));
                double apart = Math.Min(Math.Abs(c.Hue - accent.Hue), 360 - Math.Abs(c.Hue - accent.Hue));
                Assert.True(apart >= 60 || c.Saturation < 0.2, $"{kind} (dark: {dark}) is {apart}° from the accent, saturation {c.Saturation}");
            }
        }
    }
}
