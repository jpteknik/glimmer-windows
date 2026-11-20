// Ported from Packages/GlimmerKit/Tests/GlimmerHealTests/HealTests.swift
#if GLIMMER_HEAL
using Glimmer.Heal;

namespace Glimmer.Heal.Tests;

internal static class HealTestHelpers
{
    /// <summary>A <c>w</c> × <c>h</c> opaque image from a function of (x, y) giving grey 0…1.</summary>
    public static float[] Image(int w, int h, Func<int, int, float> f)
    {
        var p = new float[w * h * 4];
        Array.Fill(p, 1f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = f(x, y);
                for (int c = 0; c < 3; c++) p[(y * w + x) * 4 + c] = v;
            }
        return p;
    }

    public static bool[] Disc(int w, int h, int cx, int cy, int r)
    {
        var m = new bool[w * h];
        for (int i = 0; i < m.Length; i++)
        {
            int x = i % w - cx, y = i / w - cy;
            m[i] = x * x + y * y <= r * r;
        }
        return m;
    }

    /// <summary>The largest difference from the original inside the hole.</summary>
    public static float Worst(float[] a, float[] b, bool[] hole)
    {
        float m = 0;
        for (int i = 0; i < hole.Length; i++)
            if (hole[i])
                for (int c = 0; c < 4; c++) m = Math.Max(m, Math.Abs(a[i * 4 + c] - b[i * 4 + c]));
        return m;
    }

    public static bool OutsideUntouched(float[] a, float[] b, bool[] hole)
    {
        for (int i = 0; i < hole.Length; i++)
        {
            if (hole[i]) continue;
            for (int c = 0; c < 4; c++) if (a[i * 4 + c] != b[i * 4 + c]) return false;
        }
        return true;
    }

    /// <summary>A texture without a period, from a fixed integer hash (0…999).</summary>
    public static uint Hash(int x, int y)
    {
        unchecked
        {
            uint v = (uint)((long)x * 73_856_093L ^ (long)y * 19_349_663L);
            v ^= v >> 13; v = v * 0x5bd1_e995u; v ^= v >> 15;
            return v;
        }
    }
}

public class HealAreaTests
{
    [Fact]
    public void DistanceIsEuclidean()
    {
        var mask = new bool[9 * 7];
        mask[3 * 9 + 4] = true;
        float[] d = Heal.Distance(mask, 9, 7);
        Assert.Equal(0f, d[3 * 9 + 4]);
        Assert.Equal(3f, d[3 * 9 + 7]);
        Assert.True(Math.Abs(d[0] - MathF.Sqrt(25f)) < 1e-4);
        Assert.All(Heal.Distance(new[] { false, false }, 2, 1), v => Assert.Equal(float.PositiveInfinity, v));
    }

    [Fact]
    public void AThinLineIsThinHoweverLong()
    {
        // A 4-pixel-thick line 80 long.
        int w = 100, h = 40;
        var coverage = new float[w * h];
        for (int i = 0; i < coverage.Length; i++)
            coverage[i] = i % w >= 10 && i % w < 90 && i / w >= 18 && i / w < 22 ? 1f : 0f;
        var area = Heal.MakeArea(coverage, w, h, 0);
        Assert.NotNull(area);
        Assert.Equal(320, area!.Pixels);
        Assert.True(area.Thickness >= 3 && area.Thickness <= 4);
        Assert.Equal((10, 18, 80, 4), area.Bounds);
        var grown = Heal.MakeArea(coverage, w, h, 3);
        Assert.NotNull(grown);
        Assert.True(grown!.Thickness >= 9 && grown.Thickness <= 10);
        Assert.True(grown.Bounds.X == 7 && grown.Bounds.Width == 86);
    }

    [Fact]
    public void ASoftEdgeIsBlendedNotCut()
    {
        int w = 40, h = 1;
        // 1 in the middle, falling off over five pixels on each side.
        var coverage = new float[w];
        for (int x = 0; x < w; x++) coverage[x] = Math.Max(0f, Math.Min(1f, 1 - (float)(Math.Abs(x - 20) - 5) / 5));
        var area = Heal.MakeArea(coverage, w, h, 0);
        Assert.NotNull(area);
        Assert.Equal(1f, area!.Weight[20]);
        Assert.Equal(1f, area.Weight[27]);            // coverage 0.6: fully replaced
        Assert.True(area.Weight[29] > 0 && area.Weight[29] < 1);   // coverage 0.2: blended
        Assert.True(area.Hole[29] && !area.Hole[31]);
        var faint = new float[10];
        Array.Fill(faint, 0.3f);
        Assert.Null(Heal.MakeArea(faint, 10, 1, 2));
    }

    [Fact]
    public void SizeLimits()
    {
        Assert.Null(Heal.RefusalFor(5000, 80, false));
        var soft = Heal.RefusalFor(50_000, 200, false);
        Assert.True(soft?.Reason == "area_too_large" && soft.Hard == false);
        Assert.Null(Heal.RefusalFor(50_000, 200, true));
        Assert.True(Heal.RefusalFor(500_000, 600, true)?.Hard);
        Assert.True(Heal.RefusalFor(5_000_000, 50, true)?.Hard);
        Assert.True(Heal.Padding(10) == 64 && Heal.Padding(100) == 200 && Heal.Padding(300) == 256);
    }
}

public class HealFillTests
{
    [Fact]
    public void AFlatAreaComesBackFlat()
    {
        int w = 96, h = 96;
        var original = HealTestHelpers.Image(w, h, (_, _) => 0.4f);
        var hole = HealTestHelpers.Disc(w, h, 48, 48, 14);
        foreach (var method in Enum.GetValues<Heal.Method>())
        {
            var r = Heal.Fill(original, w, h, hole, 28, method);
            Assert.True(HealTestHelpers.Worst(r.Pixels, original, hole) < 0.002, $"{method}");
            Assert.True(HealTestHelpers.OutsideUntouched(r.Pixels, original, hole));
        }
    }

    [Fact]
    public void AGradientIsContinuedByTheSmoothFill()
    {
        int w = 96, h = 64;
        var original = HealTestHelpers.Image(w, h, (x, _) => 0.2f + 0.6f * (float)x / 95);
        var hole = HealTestHelpers.Disc(w, h, 48, 32, 6);
        var r = Heal.Fill(original, w, h, hole, 12, Heal.Method.Smooth);
        Assert.True(HealTestHelpers.Worst(r.Pixels, original, hole) < 0.02);
        // Thin holes take the smooth fill by themselves.
        var wire = new bool[w * h];
        for (int i = 0; i < wire.Length; i++) wire[i] = i / w >= 30 && i / w < 34 && i % w >= 10 && i % w < 86;
        Assert.Equal(Heal.Method.Smooth, Heal.Fill(original, w, h, wire, 4).Method);
    }

    [Fact]
    public void StripesAndChecksComeBackExactlyFromTheTextureFill()
    {
        int w = 128, h = 128;
        var cases = new (string Name, float[] Original)[]
        {
            ("stripes", HealTestHelpers.Image(w, h, (x, _) => (x / 4) % 2 == 0 ? 0.15f : 0.8f)),
            ("checks", HealTestHelpers.Image(w, h, (x, y) => ((x / 6) + (y / 6)) % 2 == 0 ? 0.1f : 0.9f)),
        };
        foreach (var (name, original) in cases)
        {
            var hole = HealTestHelpers.Disc(w, h, 64, 64, 16);
            var auto = Heal.Fill(original, w, h, hole, 32);
            Assert.True(auto.Method == Heal.Method.Texture, name);
            Assert.True(HealTestHelpers.Worst(auto.Pixels, original, hole) == 0, name);
            Assert.True(HealTestHelpers.OutsideUntouched(auto.Pixels, original, hole));
        }
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        int w = 120, h = 100;
        // Texture without a period, from a fixed integer hash.
        var original = HealTestHelpers.Image(w, h, (x, y) => (float)(HealTestHelpers.Hash(x, y) % 1000) / 1000);
        var hole = HealTestHelpers.Disc(w, h, 60, 50, 12);
        foreach (var method in new[] { Heal.Method.Smooth, Heal.Method.Texture })
        {
            var a = Heal.Fill(original, w, h, hole, 24, method);
            var b = Heal.Fill(original, w, h, hole, 24, method);
            Assert.True(a.Pixels.AsSpan().SequenceEqual(b.Pixels), $"{method}");
            Assert.True(HealTestHelpers.OutsideUntouched(a.Pixels, original, hole));
        }
        Assert.Equal(Heal.Method.Texture, Heal.Fill(original, w, h, hole, 24).Method);
    }

    [Fact]
    public void EveryFilledPixelOfTheTextureFillIsACopyOfAnExistingOne()
    {
        int w = 90, h = 90;
        var original = HealTestHelpers.Image(w, h, (x, y) => (float)((x * 7 + y * 13) % 31) / 31);
        var hole = HealTestHelpers.Disc(w, h, 45, 45, 10);
        var r = Heal.Fill(original, w, h, hole, 20, Heal.Method.Texture);
        var present = new HashSet<float>();
        for (int i = 0; i < hole.Length; i++) if (!hole[i]) present.Add(original[i * 4]);
        for (int i = 0; i < hole.Length; i++) if (hole[i]) Assert.Contains(r.Pixels[i * 4], present);
    }

    [Fact]
    public void TransparentPixelsAreNeverCopied()
    {
        int w = 80, h = 80;
        var original = HealTestHelpers.Image(w, h, (x, _) => (x / 3) % 2 == 0 ? 0.2f : 0.7f);
        for (int y = 0; y < h; y++)
            for (int x = 50; x < w; x++)
                for (int c = 0; c < 4; c++) original[(y * w + x) * 4 + c] = 0;   // right part empty
        var hole = HealTestHelpers.Disc(w, h, 25, 40, 8);
        var r = Heal.Fill(original, w, h, hole, 16, Heal.Method.Texture);
        for (int i = 0; i < hole.Length; i++) if (hole[i]) Assert.Equal(1f, r.Pixels[i * 4 + 3]);
    }

    /// <summary>
    /// What is in the hole is being thrown away, so it must not colour the fill: a black
    /// wire and a white one over the same picture give the same bytes. (The smooth fill
    /// once let a hole pixel into its neighbours' gradients.)
    /// </summary>
    [Fact]
    public void WhatIsInTheHoleNeverReachesTheFill()
    {
        int w = 200, h = 80;
        var baseImage = HealTestHelpers.Image(w, h, (x, y) => 0.3f + 0.4f * (float)((x * 7 + y * 13) % 31) / 31);
        var wire = new bool[w * h];
        for (int i = 0; i < wire.Length; i++) wire[i] = i / w >= 36 && i / w < 47;
        var blob = HealTestHelpers.Disc(w, h, 100, 40, 15);
        foreach (var hole in new[] { wire, blob })
        {
            var black = (float[])baseImage.Clone();
            var white = (float[])baseImage.Clone();
            for (int i = 0; i < hole.Length; i++)
                if (hole[i])
                    for (int c = 0; c < 3; c++) { black[i * 4 + c] = 0; white[i * 4 + c] = 1; }
            foreach (var method in new[] { Heal.Method.Smooth, Heal.Method.Texture, Heal.Method.Auto })
            {
                var a = Heal.Fill(black, w, h, hole, 11, method);
                var b = Heal.Fill(white, w, h, hole, 11, method);
                Assert.True(a.Pixels.AsSpan().SequenceEqual(b.Pixels), $"{method}");
                Assert.Equal(a.Method, b.Method);
            }
        }
    }

    /// <summary>
    /// The smooth fill stays within the range of the picture around it, however large the
    /// hole and however noisy the picture. (Extending gradients of already-filled pixels
    /// once made it run away into stripes of pure black, white and colour.)
    /// </summary>
    [Fact]
    public void TheSmoothFillNeverOvershoots()
    {
        int w = 260, h = 220;
        var noisy = HealTestHelpers.Image(w, h, (x, y) => 0.4f + 0.2f * (float)(HealTestHelpers.Hash(x, y) % 1000) / 1000);
        var hole = HealTestHelpers.Disc(w, h, 130, 110, 80);
        var r = Heal.Fill(noisy, w, h, hole, 160, Heal.Method.Smooth);
        for (int i = 0; i < hole.Length; i++)
            if (hole[i])
                Assert.True(r.Pixels[i * 4] >= 0.4f && r.Pixels[i * 4] <= 0.6f && r.Pixels[i * 4 + 3] == 1f);
    }

    [Fact]
    public void Refusals()
    {
        int w = 40, h = 40;
        var original = HealTestHelpers.Image(w, h, (_, _) => 0.5f);
        var most = new bool[w * h];
        for (int i = 0; i < most.Length; i++) most[i] = i % w > 5;
        var nothing = Assert.Throws<Heal.Refusal>(() => Heal.Fill(original, w, h, most, 30));
        Assert.Equal("nothing_to_sample", nothing.Reason);
        // Bands of image narrower than a patch: the texture fill has nothing whole to copy; auto fills smoothly.
        var bands = new bool[w * h];
        for (int i = 0; i < bands.Length; i++) bands[i] = (i % w) % 16 >= 8;
        var texture = Assert.Throws<Heal.Refusal>(() => Heal.Fill(original, w, h, bands, 8, Heal.Method.Texture));
        Assert.Equal("nothing_to_sample", texture.Reason);
        var textured = HealTestHelpers.Image(w, h, (x, y) => (float)((x * 7 + y * 13) % 31) / 31);
        Assert.Equal(Heal.Method.Smooth, Heal.Fill(textured, w, h, bands, 8).Method);
        // Nothing to do.
        Assert.True(Heal.Fill(original, w, h, new bool[w * h], 1).Pixels.AsSpan().SequenceEqual(original));
    }

    [Fact]
    public void CancelStops()
    {
        int w = 128, h = 128;
        var original = HealTestHelpers.Image(w, h, (x, y) => (float)((x * 5 + y * 3) % 17) / 17);
        var hole = HealTestHelpers.Disc(w, h, 64, 64, 30);
        Assert.Throws<OperationCanceledException>(() =>
            Heal.Fill(original, w, h, hole, 60, Heal.Method.Texture, () => true));
    }

    [Fact]
    public void TheRefusalTextsAreTheOriginalsWordForWord()
    {
        Assert.Equal("The area is 300 px across at its thickest (50000 pixels). Heal Selection fills areas up to 250 px across and 4 megapixels.",
                     Heal.RefusalFor(50_000, 300, false)!.Message);
        Assert.Equal("The area is 200 px across at its thickest. Heal Selection gives good results up to about 120 px; beyond that the fill may smear or repeat.",
                     Heal.RefusalFor(50_000, 200, false)!.Message);
    }
}
#endif
