// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/PipelineTests.swift (ImagingTests), plus golden-value tests
// against numpy / scipy / Pillow (Golden/make_imaging_golden.py writes Golden/imaging.json).
using System.Numerics;
using System.Text.Json;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class ImagingTests
{
    internal static RGBAImage Solid(int w, int h, Vector4 c) => new(w, h, c);

    [Fact]
    public void ColorMatchRecoversGainAndOffset()
    {
        // result = (ref − 10) / 1.2 everywhere; matching must undo it.
        var reference = new RGBAImage(20, 20, Vector4.Zero);
        var res = reference.Clone();
        for (int i = 0; i < 400; i++)
        {
            float v = i % 200 + 20;
            for (int c = 0; c < 3; c++)
            {
                reference.Pixels[i * 4 + c] = v;
                res.Pixels[i * 4 + c] = MathF.Round((v - 10) / 1.2f, MidpointRounding.AwayFromZero);
            }
            reference.Pixels[i * 4 + 3] = 255; res.Pixels[i * 4 + 3] = 255;
        }
        var matched = Imaging.ColorMatch(res, reference, new GrayImage(20, 20, 255f));
        foreach (int i in new[] { 0, 57, 399 })
        {
            Assert.True(MathF.Abs(matched.Pixels[i * 4] - reference.Pixels[i * 4]) <= 1.5f);
        }
    }

    [Fact]
    public void ColorMatchDegenerateUsesOffset()
    {
        var reference = Solid(8, 8, new Vector4(100, 100, 100, 255));
        var res = Solid(8, 8, new Vector4(80, 80, 80, 255));
        var m = Imaging.ColorMatch(res, reference, new GrayImage(8, 8, 255f));
        Assert.Equal(100f, m.Pixels[0]);
        // No trusted pixels → identity.
        var id = Imaging.ColorMatch(res, reference, new GrayImage(8, 8, 0f));
        Assert.Equal(80f, id.Pixels[0]);
    }

    [Fact]
    public void FeatherErodesThenBlurs()
    {
        var m = new GrayImage(40, 1, 0f);
        for (int x = 10; x < 30; x++) m.Values[x] = 255;
        var f = Imaging.FeatherMask(m, erode: 2, blur: 1);
        Assert.True(f.Values[20] > 250);       // centre stays
        Assert.True(f.Values[10] < 20);        // eroded edge
        Assert.True(f.Values[13] > f.Values[11]);
    }

    [Fact]
    public void ClampROIPads()
    {
        var r = Imaging.ClampROI(new PixelRect(10, 10, 100, 50), 1000, 1000);
        Assert.Equal(new PixelRect(-20, -5, 160, 80).Intersection(new PixelRect(0, 0, 1000, 1000)), r);
    }

    [Fact]
    public void LabTransferMovesTowardReference()
    {
        var src = Solid(4, 4, new Vector4(60, 60, 60, 255));
        src.Pixels[0] = 120;
        var reference = Solid(4, 4, new Vector4(200, 120, 80, 255));
        var o = Imaging.LabColorTransfer(src, reference, strength: 1);
        Assert.True(o.Pixels[4] > 150 && o.Pixels[6] < 110);  // pixel 1 becomes warm
        var same = Imaging.LabColorTransfer(reference, reference);
        Assert.True(MathF.Abs(same.Pixels[0] - 200) < 1.5f);
    }

    [Fact]
    public void ResizeAndRoundTrip()
    {
        var img = Solid(10, 6, new Vector4(10, 200, 30, 255));
        var big = img.Resized(20, 12);
        Assert.True(big.Width == 20 && big.Height == 12);
        Assert.True(MathF.Abs(big.Pixels[0] - 10) <= 1 && MathF.Abs(big.Pixels[1] - 200) <= 1);
        var back = RGBAImage.Decode(img.PngData());
        Assert.Equal(img, back);
    }

    // ---- additions: properties the Swift tests did not pin, and numbers from numpy/scipy/Pillow

    [Fact]
    public void ResizeKeepsAConstantColourExactlyAndNeverLeaksIntoTransparency()
    {
        var img = Solid(9, 7, new Vector4(250, 10, 10, 255));
        var o = img.Resized(40, 31);
        Assert.All(o.Pixels.Where((_, i) => i % 4 == 0), v => Assert.InRange(v, 249.9f, 250.1f));
        // Red on a transparent-black border: premultiplied resampling must not darken the red toward black.
        var edge = new RGBAImage(8, 8, new Vector4(255, 0, 0, 0));
        for (int y = 2; y < 6; y++) for (int x = 2; x < 6; x++) edge.Pixels[(y * 8 + x) * 4 + 3] = 255;
        var up = edge.Resized(32, 32);
        for (int i = 0; i < 32 * 32; i++)
            if (up.Pixels[i * 4 + 3] > 20) Assert.True(up.Pixels[i * 4] > 250f, $"red {up.Pixels[i * 4]} at alpha {up.Pixels[i * 4 + 3]}");
    }

    [Fact]
    public void LimitedKeepsTheAspect()
    {
        var img = Solid(4000, 3000, new Vector4(1, 2, 3, 255));
        var l = img.Limited(2048);
        Assert.Equal((2048, 1536), (l.Width, l.Height));
        Assert.Same(img, img.Limited(5000));
    }

    [Fact]
    public void TranslucentPngRoundTripIsWithinOneStep()
    {
        var img = new RGBAImage(6, 4, new Vector4(200, 100, 50, 128));
        var back = RGBAImage.Decode(img.PngData());
        for (int i = 0; i < img.Pixels.Length; i++) Assert.True(MathF.Abs(img.Pixels[i] - back.Pixels[i]) <= 2f, $"{i}: {img.Pixels[i]} vs {back.Pixels[i]}");
    }

    [Fact]
    public void LinearPremultipliedRoundTrips()
    {
        var img = new RGBAImage(3, 1, [10, 128, 250, 255, 200, 40, 90, 128, 0, 0, 0, 0]);
        var back = RGBAImage.FromLinearPremultiplied(img.ToLinearPremultiplied(), 3, 1);
        for (int i = 0; i < 8; i++) Assert.True(MathF.Abs(img.Pixels[i] - back.Pixels[i]) < 0.05f, $"{i}");
        Assert.Equal(0f, back.Pixels[11]);
        // Mid grey 128 (sRGB) is 0.2158 linear.
        Assert.InRange(img.ToLinearPremultiplied()[1], 0.2157f, 0.2160f);
    }

    [Fact]
    public void GrayRgbaAndInvert()
    {
        var g = new GrayImage(2, 1, [0f, 200f]);
        Assert.Equal(new float[] { 0, 0, 0, 255, 200, 200, 200, 255 }, g.Rgba().Pixels);
        Assert.Equal(new float[] { 255, 55 }, g.Inverted().Values);
        var big = g.Resized(4, 2);
        Assert.True(big.Values[0] < 1 && big.Values[3] > 150);
    }

    [Fact]
    public void CropAndPasteMatchPillow()
    {
        var img = new RGBAImage(5, 4, new float[5 * 4 * 4]);
        for (int i = 0; i < img.Pixels.Length; i++) img.Pixels[i] = i;
        var c = img.Cropped(new PixelRect(3, 2, 10, 10));   // clipped to the image
        Assert.Equal((2, 2), (c.Width, c.Height));
        Assert.Equal(img.Pixels[(2 * 5 + 3) * 4], c.Pixels[0]);
        var target = new RGBAImage(4, 4, Vector4.Zero);
        target.Paste(new RGBAImage(3, 3, new Vector4(9, 9, 9, 9)), -1, 2);   // partly outside
        Assert.Equal(9f, target.Pixels[(2 * 4 + 0) * 4]);
        Assert.Equal(0f, target.Pixels[(2 * 4 + 2) * 4]);
        Assert.Equal(0f, target.Pixels[(1 * 4 + 0) * 4]);
    }

    // ---- golden values

    static JsonElement Golden(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "imaging.json")));
        return doc.RootElement.GetProperty(name).Clone();
    }
    static float[] Floats(JsonElement e) => e.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();

    [Theory]
    [InlineData("resize_up")]
    [InlineData("resize_down")]
    [InlineData("resize_down_translucent")]
    public void LanczosResizeMatchesPillow(string name)
    {
        // Pillow's float ("F" mode) Lanczos on premultiplied channels: the same kernel and window, without 8-bit rounding between passes.
        var g = Golden(name);
        int sw = g.GetProperty("sw").GetInt32(), sh = g.GetProperty("sh").GetInt32();
        int dw = g.GetProperty("dw").GetInt32(), dh = g.GetProperty("dh").GetInt32();
        var input = new RGBAImage(sw, sh, Floats(g.GetProperty("input")));
        var expected = Floats(g.GetProperty("premultiplied"));
        var o = input.Resized(dw, dh);
        Assert.Equal((dw, dh), (o.Width, o.Height));
        for (int p = 0; p < dw * dh; p++)
        {
            Assert.True(Math.Abs(o.Pixels[p * 4 + 3] - expected[p * 4 + 3]) <= 0.01, $"{name} alpha {p}: {o.Pixels[p * 4 + 3]} vs {expected[p * 4 + 3]}");
            for (int c = 0; c < 3; c++)
            {
                double mine = o.Pixels[p * 4 + c] * o.Pixels[p * 4 + 3] / 255;
                Assert.True(Math.Abs(mine - expected[p * 4 + c]) <= 0.02, $"{name} pixel {p} ch {c}: {mine} vs Pillow {expected[p * 4 + c]}");
            }
        }
    }

    [Theory]
    [InlineData("feather", 0.01)]
    [InlineData("feather_blur_only", 0.01)]
    [InlineData("feather_erode_only", 0.0)]
    public void FeatherMaskMatchesScipy(string name, double tolerance)
    {
        var g = Golden(name);
        int w = g.GetProperty("w").GetInt32(), h = g.GetProperty("h").GetInt32();
        var mask = new GrayImage(w, h, 0f);
        for (int y = 8; y < 22; y++) for (int x = 10; x < 31; x++) mask.Values[y * w + x] = 255;
        mask.Values[3 * w + 3] = 255;
        var o = Imaging.FeatherMask(mask, g.GetProperty("erode").GetInt32(), g.GetProperty("blur").GetDouble());
        var expected = Floats(g.GetProperty("values"));
        for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(o.Values[i] - expected[i]) <= tolerance + 1e-4, $"{name} {i}: {o.Values[i]} vs {expected[i]}");
    }

    [Fact]
    public void LabTransferMatchesNumpy()
    {
        var g = Golden("lab");
        int sw = g.GetProperty("sw").GetInt32(), sh = g.GetProperty("sh").GetInt32();
        var src = new RGBAImage(sw, sh, Vector4.Zero);
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++)
            {
                int i = y * sw + x;
                for (int c = 0; c < 3; c++) src.Pixels[i * 4 + c] = (float)(128 + 81 * Math.Sin(0.31 * x + 0.17 * y + 1.7 * c));
                src.Pixels[i * 4 + 3] = 255;
            }
        var reference = new RGBAImage(13, 9, Vector4.Zero);
        for (int y = 0; y < 9; y++) for (int x = 0; x < 13; x++)
            {
                int i = y * 13 + x;
                reference.Pixels[i * 4] = 140 + x * 5; reference.Pixels[i * 4 + 1] = 90 + y * 6; reference.Pixels[i * 4 + 2] = 60 + (x + y) * 3;
                reference.Pixels[i * 4 + 3] = 255;
            }
        var o = Imaging.LabColorTransfer(src, reference, g.GetProperty("strength").GetDouble());
        var expected = Floats(g.GetProperty("values"));
        for (int i = 0; i < sw * sh; i++)
            for (int c = 0; c < 3; c++)
                Assert.True(MathF.Abs(o.Pixels[i * 4 + c] - expected[i * 3 + c]) < 0.002f, $"pixel {i} ch {c}: {o.Pixels[i * 4 + c]} vs {expected[i * 3 + c]}");
        Assert.Equal(255f, o.Pixels[3]);
    }

    [Theory]
    [InlineData("color_match")]
    [InlineData("color_match_low_gain")]    // wanted gain 0.33, clamped up to 0.5
    [InlineData("color_match_high_gain")]   // wanted gain 2.6, clamped down to 2
    public void ColorMatchMatchesNumpy(string name)
    {
        var g = Golden(name);
        int w = g.GetProperty("w").GetInt32(), h = g.GetProperty("h").GetInt32();
        RGBAImage Build(JsonElement e)
        {
            var rgb = Floats(e);
            var img = new RGBAImage(w, h, Vector4.One * 255);
            for (int i = 0; i < w * h; i++) for (int c = 0; c < 3; c++) img.Pixels[i * 4 + c] = rgb[i * 3 + c];
            return img;
        }
        var res = Build(g.GetProperty("result"));
        var reference = Build(g.GetProperty("reference"));
        var trusted = new GrayImage(w, h, Floats(g.GetProperty("trusted")));
        var o = Imaging.ColorMatch(res, reference, trusted);
        var expected = Floats(g.GetProperty("values"));
        int off = 0;
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < 3; c++)
            {
                // truncation of a float that lands on an integer boundary may differ by one step
                Assert.True(MathF.Abs(o.Pixels[i * 4 + c] - expected[i * 3 + c]) <= 1f, $"pixel {i} ch {c}: {o.Pixels[i * 4 + c]} vs {expected[i * 3 + c]}");
                if (o.Pixels[i * 4 + c] != expected[i * 3 + c]) off++;
            }
        Assert.True(off <= w * h * 3 / 50, $"{off} values differ from numpy by one step");
        Assert.Equal(255f, o.Pixels[3]);
    }

    [Fact]
    public void BlendMaskedTruncatesLikeTheOriginal()
    {
        var a = Solid(2, 1, new Vector4(10, 20, 30, 255));
        var b = Solid(2, 1, new Vector4(255, 255, 255, 255));
        var m = new GrayImage(2, 1, [0f, 128f]);
        var o = Imaging.BlendMasked(a, b, m);
        Assert.Equal(10f, o.Pixels[0]);
        Assert.Equal(MathF.Truncate(10 * (1 - 128f / 255) + 255 * 128f / 255), o.Pixels[4]);
        Assert.Equal(255f, o.Pixels[7]);
    }
}
