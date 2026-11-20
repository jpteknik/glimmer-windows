// New file, no Swift original: on the Mac, Core ML picks the GPU by itself and the tests don't look. These check the
// port's graphics card path for Real-ESRGAN (ONNX Runtime's WebGPU provider, OnDevice/OnnxGpu.cs) against its CPU path,
// and that every way the graphics card can fail ends on the CPU with the same picture.
using System.Diagnostics;
using System.Numerics;
using Glimmer.AI;
using Xunit.Abstractions;

namespace Glimmer.AI.Tests;

/// <summary>
/// Runs Real-ESRGAN on the graphics card: only with GLIMMER_GPU_TEST=1 (with <c>perf</c>, GLIMMER_PERF=1 as well). On
/// Linux it needs a Vulkan driver; Mesa's lavapipe (Vulkan on the CPU) works, slowly.
/// </summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute(bool perf = false)
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_GPU_TEST") != "1")
            Skip = "Runs Real-ESRGAN on the graphics card: set GLIMMER_GPU_TEST=1 to run it.";
        else if (perf && Environment.GetEnvironmentVariable("GLIMMER_PERF") != "1")
            Skip = "A speed measurement on the graphics card: set GLIMMER_PERF=1 as well to run it.";
    }
}

[Collection("On-device models")]
public class RealESRGANGpuTests(ITestOutputHelper output)
{
    /// <summary>
    /// The same picture upscaled 4× on the graphics card and on the CPU agrees within 0.25 of 255 everywhere, and every
    /// tile ran on the graphics card. The two compute the same float32 network in a different order, so they differ in
    /// the last bits; 0.25 of 255 is a quarter of an 8-bit level (and a quarter of what the model may differ from PyTorch
    /// in TheBundledModelMatchesPyTorch). Measured on 2026-11-20 in WSL, where the only Vulkan device is Mesa's lavapipe
    /// (Vulkan computed on the CPU): largest difference 0.00052 of 255, and 5,083,927 of the 7,680,000 values differ at
    /// all (alpha doesn't come from the network). The 4 tiles took 2.6 s on the CPU and 28.4 s on lavapipe, which says
    /// nothing about a real graphics card. With the provider library hidden the test fails on the status, as it should.
    /// </summary>
    [GpuFact]
    public async Task GraphicsCardMatchesTheCpu()
    {
        var image = TestPictures.Detailed(400, 300);
        int tiles = RealESRGAN.Origins(image.Width).Count * RealESRGAN.Origins(image.Height).Count;
        var oneTile = new RGBAImage(64, 40, new Vector4(200, 100, 50, 255));
        try
        {
            UseCpu();
            await RealESRGAN.UpscaleAsync(oneTile);  // warm up
            var clock = Stopwatch.StartNew();
            var cpu = await RealESRGAN.UpscaleAsync(image);
            var cpuTime = clock.Elapsed;

            UseGpu();
            clock.Restart();
            await RealESRGAN.UpscaleAsync(oneTile);  // makes the session and its shaders
            var setup = clock.Elapsed;
            clock.Restart();
            var gpu = await RealESRGAN.UpscaleAsync(image);
            var gpuTime = clock.Elapsed;

            double worst = 0;
            long differ = 0;
            for (int i = 0; i < cpu.Pixels.Length; i++)
            {
                double d = Math.Abs(gpu.Pixels[i] - cpu.Pixels[i]);
                worst = Math.Max(worst, d);
                if (d != 0) differ++;
            }
            output.WriteLine($"{RealESRGAN.GpuStatus}. {tiles} tiles at 4×: CPU {cpuTime.TotalSeconds:0.00} s, graphics card " +
                             $"{gpuTime.TotalSeconds:0.00} s (first tile, with the session and its shaders, {setup.TotalSeconds:0.00} s). " +
                             $"Largest difference {worst:0.000000} of 255; {differ} of {cpu.Pixels.Length} values differ.");
            Assert.StartsWith("graphics card: ", RealESRGAN.GpuStatus);
            Assert.DoesNotContain("not tried yet", RealESRGAN.GpuStatus);
            Assert.Equal(1 + tiles, OnnxModels.GpuRuns);
            Assert.True(worst <= 0.25, $"largest difference {worst} of 255");
        }
        finally { UseCpu(); }
    }

    /// <summary>A run on the graphics card that throws: that tile is made again on the CPU and the rest stays there.</summary>
    [GpuFact]
    public async Task AFailedRunIsMadeAgainOnTheCpu()
    {
        var image = TestPictures.Detailed(300, 120);  // two tiles
        try
        {
            UseCpu();
            var cpu = await RealESRGAN.UpscaleAsync(image, scale: 2);
            UseGpu();
            int calls = 0;
            OnnxModels.GpuRunOverride = (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("device lost (test)");
            };
            var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
            Assert.Equal("CPU: the graphics card failed in a run: device lost (test)", RealESRGAN.GpuStatus);
            Assert.Equal(1, calls);
            Assert.Equal(0, OnnxModels.GpuRuns);
            Assert.True(cpu.Pixels.AsSpan().SequenceEqual(up.Pixels), "the picture differs from the CPU's");
        }
        finally { UseCpu(); }
    }

    /// <summary>A run on the graphics card that returns a value that isn't a number counts as a failure.</summary>
    [GpuFact]
    public async Task ANonFiniteResultIsMadeAgainOnTheCpu()
    {
        var image = TestPictures.Detailed(120, 90);
        try
        {
            UseCpu();
            var cpu = await RealESRGAN.UpscaleAsync(image);
            UseGpu();
            OnnxModels.GpuRunOverride = (_, _, _) => [([0.5f, float.NaN], [2])];
            var up = await RealESRGAN.UpscaleAsync(image);
            Assert.Equal("CPU: the graphics card returned a value that is not a number", RealESRGAN.GpuStatus);
            Assert.Equal(0, OnnxModels.GpuRuns);
            Assert.True(cpu.Pixels.AsSpan().SequenceEqual(up.Pixels), "the picture differs from the CPU's");
        }
        finally { UseCpu(); }
    }

    /// <summary>The 4K measurement of RealESRGANPerformance.FourK on the graphics card (for a Windows machine).</summary>
    [GpuFact(perf: true)]
    public async Task FourKOnTheGraphicsCard()
    {
        int w = 3840, h = 2160;
        var px = new float[w * h * 4];
        Array.Fill(px, 255f);
        for (int i = 0; i < px.Length; i += 4) { px[i] = i % 251; px[i + 1] = i % 127; }
        var image = new RGBAImage(w, h, px);
        try
        {
            UseGpu();
            await RealESRGAN.UpscaleAsync(new RGBAImage(64, 64, new Vector4(1, 1, 1, 255)), scale: 2);  // warm up
            var clock = Stopwatch.StartNew();
            var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
            output.WriteLine($"Real-ESRGAN 4K 2× on {RealESRGAN.GpuStatus}: {clock.Elapsed.TotalSeconds:0.00} s → {up.Width}×{up.Height}");
            Assert.StartsWith("graphics card: ", RealESRGAN.GpuStatus);
        }
        finally { UseCpu(); }
    }

    // MARK: Always on: the ways the graphics card is not used

    /// <summary>
    /// With the switch on but no provider library, the picture is still upscaled, on the CPU, bit for bit as with the
    /// switch off, and the status says why.
    /// </summary>
    [Fact]
    public async Task AMissingProviderFallsBackToTheCpu()
    {
        var image = TestPictures.Detailed(120, 90);
        try
        {
            UseCpu();
            var cpu = await RealESRGAN.UpscaleAsync(image, scale: 2);
            UseGpu(library: MissingLibrary);
            var up = await RealESRGAN.UpscaleAsync(image, scale: 2);
            Assert.Equal($"CPU: the graphics card library is missing ({OnnxGpu.LibraryFileName})", RealESRGAN.GpuStatus);
            Assert.Equal(0, OnnxModels.GpuRuns);
            Assert.True(cpu.Pixels.AsSpan().SequenceEqual(up.Pixels), "the picture differs from the CPU's");
        }
        finally { UseCpu(); }
    }

    /// <summary>
    /// GLIMMER_GPU=0 keeps the CPU with the switch on. The provider isn't even looked for: it is "missing" here, and the
    /// status would say so had it been.
    /// </summary>
    [Fact]
    public async Task GlimmerGpuZeroKeepsTheCpu()
    {
        var image = TestPictures.Detailed(120, 90);
        try
        {
            UseCpu();
            var cpu = await RealESRGAN.UpscaleAsync(image);
            UseGpu(library: MissingLibrary);
            Environment.SetEnvironmentVariable("GLIMMER_GPU", "0");
            var up = await RealESRGAN.UpscaleAsync(image);
            Assert.Equal("CPU: GLIMMER_GPU=0", RealESRGAN.GpuStatus);
            Assert.Equal(0, OnnxModels.GpuRuns);
            Assert.True(cpu.Pixels.AsSpan().SequenceEqual(up.Pixels), "the picture differs from the CPU's");
        }
        finally { UseCpu(); }
    }

    /// <summary>
    /// The test process runs with ONNX Runtime's telemetry off (Glimmer.AI.Tests.runsettings): ONNX Runtime 1.32's Linux
    /// library and the WebGPU provider carry a client that uploads usage events to Microsoft over HTTPS, and no test may
    /// reach the network.
    /// </summary>
    [Fact]
    public void OnnxRuntimeTelemetryIsOffInTests() =>
        Assert.True(Environment.GetEnvironmentVariable("ORT_DISABLE_TELEMETRY") == "1",
                    "ORT_DISABLE_TELEMETRY is not 1: run the tests with Glimmer.AI.Tests.runsettings (dotnet test does by itself).");

    private static string MissingLibrary => Path.Combine(AppContext.BaseDirectory, "no-such-folder", OnnxGpu.LibraryFileName);

    private static readonly string? GlimmerGpuOutside = Environment.GetEnvironmentVariable("GLIMMER_GPU");

    /// <summary>The switch off and the graphics card forgotten, as every other test expects it.</summary>
    private static void UseCpu()
    {
        Environment.SetEnvironmentVariable("GLIMMER_GPU", GlimmerGpuOutside);
        OnnxModels.UseGpu = false;
        OnnxModels.GpuRunOverride = null;
        OnnxGpu.LibraryPathOverride = null;
        OnnxModels.ResetGpu();
    }

    /// <summary>The switch on, tried afresh; GLIMMER_GPU from outside the test doesn't count.</summary>
    private static void UseGpu(string? library = null)
    {
        Environment.SetEnvironmentVariable("GLIMMER_GPU", null);
        OnnxGpu.LibraryPathOverride = library;
        OnnxModels.ResetGpu();
        OnnxModels.UseGpu = true;
    }
}
