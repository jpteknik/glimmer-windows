// Ported from Packages/GlimmerKit/Tests/GlimmerCoreTests/BlendModeMenuTests.swift

namespace Glimmer.Core.Tests;

public class BlendModeMenuTests
{
    [Fact]
    public void EveryPixelModeIsInTheMenuOnce()
    {
        var listed = BlendMode.MenuGroups.SelectMany(g => g).ToList();
        Assert.True(listed.ToHashSet().SetEquals(BlendMode.PixelModes) && listed.Count == BlendMode.PixelModes.Length);
        // Unique shader codes (pass-through shares normal's).
        Assert.Equal(BlendMode.AllCases.Count - 1, BlendMode.AllCases.Select(m => m.ShaderIndex()).ToHashSet().Count);
    }

    [Fact]
    public void WebProjectsGetTheNearestWebMode()
    {
        // The web app has the W3C modes only.
        Assert.True(BlendMode.HardMix.WebMode() == BlendMode.HardLight && BlendMode.VividLight.WebMode() == BlendMode.HardLight);
        Assert.True(BlendMode.Subtract.WebMode() == BlendMode.Difference && BlendMode.Divide.WebMode() == BlendMode.ColorDodge);
        Assert.True(BlendMode.PassThrough.WebMode() == BlendMode.Normal && BlendMode.Multiply.WebMode() == BlendMode.Multiply);
    }

    /// <summary>Raw values are the Mac's (and the web app's) strings, in the Mac's declared order. Not in the Mac's tests.</summary>
    [Fact]
    public void RawValuesAreTheMacs()
    {
        Assert.Equal("pass-through", BlendMode.PassThrough.RawValue());
        Assert.Equal("linear-dodge", BlendMode.LinearDodge.RawValue());
        Assert.Equal(BlendMode.ColorBurn, BlendMode.FromRawValue("color-burn"));
        Assert.Null(BlendMode.FromRawValue("colorBurn"));
        Assert.Equal(BlendMode.Luminosity, BlendModeExtensions.Parse("luminosity"));
        Assert.Equal(27, BlendMode.AllCases.Count);
        Assert.Equal(BlendMode.PassThrough, BlendMode.AllCases[0]);
        Assert.Equal("Linear Dodge (Add)", BlendMode.LinearDodge.Title());
    }
}
