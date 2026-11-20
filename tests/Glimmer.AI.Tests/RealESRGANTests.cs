// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/RealESRGANTests.swift
// New: TheBundledModelMatchesPyTorch (the conversion checked against PyTorch with the original weights,
// Golden/realesrgan.json from OnDevice/Resources/realesrgan_onnx.py) and the 12-megapixel speed measurement.
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Glimmer.AI;
using Xunit.Abstractions;

namespace Glimmer.AI.Tests;

[Collection("On-device models")]
public class RealESRGANTests
{
    [Fact]
    public void TilesCoverTheImage()
    {
        foreach (int length in new[] { 1, 100, 224, 240, 241, 1000 })
        {
            var o = RealESRGAN.Origins(length);
            Assert.Equal(-RealESRGAN.Margin, o[0]);
            Assert.True(o[^1] + RealESRGAN.Tile - RealESRGAN.Margin >= length);
            Assert.All(o.Zip(o.Skip(1)), p => Assert.Equal(RealESRGAN.Tile - 2 * RealESRGAN.Margin, p.Second - p.First));
        }
    }

    [Fact]
    public async Task UpscalesFourTimesAndKeepsColours()
    {
        // A soft gradient with a hard-edged square and a transparent corner.
        int w = 300, h = 170;
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inSquare = x >= 100 && x < 160 && y >= 60 && y < 120;
                px[i] = inSquare ? 230 : (float)x / w * 255;
                px[i + 1] = inSquare ? 30 : (float)y / h * 255;
                px[i + 2] = inSquare ? 40 : 120;
                px[i + 3] = x < 20 && y < 20 ? 0 : 255;
            }
        }
        var image = new RGBAImage(w, h, px);
        var clock = Stopwatch.StartNew();
        var up = await RealESRGAN.UpscaleAsync(image);
        Console.WriteLine($"Real-ESRGAN {w}×{h} → {up.Width}×{up.Height} in {clock.Elapsed.TotalSeconds:0.00} s");
        Assert.True(up.Width == w * 4 && up.Height == h * 4);
        Assert.All(up.Pixels, v => Assert.True(float.IsFinite(v) && v >= 0 && v <= 255));
        // Compare with a plain resize: same picture, small average difference.
        var reference = image.Resized(w * 4, h * 4);
        double diff = 0, n = 0;
        for (int i = 0; i < up.Pixels.Length; i += 4)
        {
            if (!(reference.Pixels[i + 3] > 250)) continue;
            for (int c = 0; c < 3; c++) diff += Math.Abs(up.Pixels[i + c] - reference.Pixels[i + c]);
            n += 3;
        }
        Assert.True(diff / n < 8, $"mean difference {diff / n}");
        // Inside the square, the red stays red.
        int centre = ((90 * 4) * (w * 4) + 130 * 4) * 4;
        Assert.True(up.Pixels[centre] > 200 && up.Pixels[centre + 1] < 60);
        // Alpha is carried over.
        Assert.True(up.Pixels[3] < 5 && up.Pixels[(400 * w * 4 + 400) * 4 + 3] > 250);
    }

    [Fact]
    public async Task TwoTimesAveragesTheNetworkOutput()
    {
        var image = new RGBAImage(64, 40, new Vector4(200, 100, 50, 255));
        var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
        Assert.True(up.Width == 128 && up.Height == 80);
        int mid = (40 * 128 + 64) * 4;
        Assert.True(Math.Abs(up.Pixels[mid] - 200) < 6 && Math.Abs(up.Pixels[mid + 1] - 100) < 6 && Math.Abs(up.Pixels[mid + 2] - 50) < 6,
                    $"{up.Pixels[mid]}, {up.Pixels[mid + 1]}, {up.Pixels[mid + 2]}");
    }

    // MARK: New in the port

    /// <summary>
    /// The bundled ONNX model (float16 weights, run in float32 by ONNX Runtime) against PyTorch running the original
    /// float32 weights, on the formula picture of realesrgan_onnx.py: within 0.004 of the 0…1 range (about one level of
    /// 255) everywhere sampled. Measured when it was made: largest difference 0.0018 on this picture, 0.0021 on a random
    /// 256 px tile.
    /// </summary>
    [Fact]
    public void TheBundledModelMatchesPyTorch()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "realesrgan.json")));
        var root = doc.RootElement;
        int w = root.GetProperty("width").GetInt32(), h = root.GetProperty("height").GetInt32();
        // rgb(x, y, c) = 0.5 + 0.4 sin(0.37 x + 0.23 y + 2.1 c), and a hard-edged square at x 12…21, y 6…15.
        var input = new float[3 * w * h];
        float[] square = [0.9f, 0.12f, 0.16f];
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inSquare = x >= 12 && x < 22 && y >= 6 && y < 16;
                    input[(c * h + y) * w + x] = inSquare ? square[c] : (float)(0.5 + 0.4 * Math.Sin(0.37 * x + 0.23 * y + 2.1 * c));
                }
        var output = RealESRGAN.Network(input, w, h);
        Assert.Equal(3 * 4 * w * 4 * h, output.Length);
        double worst = 0;
        foreach (var sample in root.GetProperty("samples").EnumerateArray())
        {
            int index = sample[0].GetInt32();
            double expected = sample[1].GetDouble();
            worst = Math.Max(worst, Math.Abs(output[index] - expected));
        }
        Console.WriteLine($"Real-ESRGAN: largest difference from PyTorch {worst:0.000000} of 1 over {root.GetProperty("samples").GetArrayLength()} samples");
        Assert.True(worst < 0.004, $"largest difference {worst}");
        var means = root.GetProperty("mean").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        int plane = 16 * w * h;
        for (int c = 0; c < 3; c++)
        {
            double mean = 0;
            for (int i = 0; i < plane; i++) mean += output[c * plane + i];
            Assert.True(Math.Abs(mean / plane - means[c]) < 0.001, $"channel {c} mean {mean / plane} vs {means[c]}");
        }
    }
}

[Collection("On-device models")]
public class RealESRGANPerformance(ITestOutputHelper output)
{
    [PerfFact]
    public async Task FourK()
    {
        int w = 3840, h = 2160;
        var px = new float[w * h * 4];
        Array.Fill(px, 255f);
        for (int i = 0; i < px.Length; i += 4) { px[i] = i % 251; px[i + 1] = i % 127; }
        var image = new RGBAImage(w, h, px);
        await RealESRGAN.UpscaleAsync(new RGBAImage(64, 64, new Vector4(1, 1, 1, 255)), scale: 2);  // warm up
        var clock = Stopwatch.StartNew();
        var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
        output.WriteLine($"Real-ESRGAN 4K 2×: {clock.Elapsed.TotalSeconds:0.00} s → {up.Width}×{up.Height}");
    }

    /// <summary>A 12-megapixel photo (4000 × 3000) at 2× (the network runs at 4× either way; 2× keeps the output at 48 MP).</summary>
    [PerfFact]
    public async Task TwelveMegapixels()
    {
        var image = TestPictures.TwelveMegapixels();
        await RealESRGAN.UpscaleAsync(new RGBAImage(64, 64, new Vector4(1, 1, 1, 255)), scale: 2);  // warm up
        var clock = Stopwatch.StartNew();
        var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
        output.WriteLine($"Real-ESRGAN 12 MP 2×: {clock.Elapsed.TotalSeconds:0.0} s → {up.Width}×{up.Height}, {RealESRGAN.Origins(4000).Count * RealESRGAN.Origins(3000).Count} tiles");
    }
}
