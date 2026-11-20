// Ported from Packages/GlimmerKit/Sources/GlimmerAI/RealESRGAN.swift
//
// The Mac bundles a Core ML conversion; this port bundles an ONNX conversion of the same weights
// (OnDevice/Resources/RealESRGAN.onnx, made by realesrgan_onnx.py beside it, embedded in Glimmer.AI.dll) and runs it with
// ONNX Runtime: on the CPU, or on the graphics card when the app switches that on (UseGpu; any failure there falls back
// to the CPU, see OnnxModels.RunBundled). The tiling, the 2× averaging and the alpha handling are the original's. Measured
// against PyTorch running the original float32 weights: largest difference 0.0021 of the 0…1 range, 0.54 of 255 (the
// bundled weights are float16, as the Mac's).
namespace Glimmer.AI;

public sealed class RealESRGANError : Exception
{
    public override string Message => "The Real-ESRGAN model is missing from the app.";
}

/// <summary>
/// 4× super-resolution on this computer with Real-ESRGAN's compact general model (realesr-general-x4v3,
/// BSD-3-Clause, bundled as an ONNX model).
///
/// The model runs on 256 px tiles. Tiles overlap by <see cref="Margin"/> pixels of context on each side and only their
/// centres are kept, so there are no seams.
/// </summary>
public static class RealESRGAN
{
    public const int Factor = 4;
    internal const int Tile = 256;
    internal const int Margin = 16;
    internal const string Resource = "Glimmer.AI.RealESRGAN.onnx";

    /// <summary>
    /// Whether the network may run on the graphics card (ONNX Runtime's WebGPU provider: Direct3D 12 on Windows). Off by
    /// default, so the tests and Linux use the CPU; the app switches it on at start-up. GLIMMER_GPU=0 keeps the CPU. The
    /// first failure on the graphics card moves the rest of the process to the CPU, and the tile it failed on is made
    /// again there.
    /// </summary>
    public static bool UseGpu { get => OnnxModels.UseGpu; set => OnnxModels.UseGpu = value; }

    /// <summary>What runs the network, in words: "graphics card: &lt;device&gt;" or "CPU: &lt;reason&gt;".</summary>
    public static string GpuStatus => OnnxModels.GpuStatus;

    /// <summary>Tile origins along one axis: steps of <c>tile − 2·margin</c>, the last one flush with the end.</summary>
    internal static IReadOnlyList<int> Origins(int length)
    {
        int step = Tile - 2 * Margin;
        var result = new List<int>();
        int x = -Margin;
        while (true)
        {
            result.Add(x);
            if (x + Tile - Margin >= length) break;
            x += step;
        }
        return result;
    }

    /// <summary>The network once: a 1 × 3 × h × w tile (0…1) to 1 × 3 × 4h × 4w.</summary>
    internal static float[] Network(float[] input, int width, int height)
    {
        try { return OnnxModels.RunBundled(Resource, [OnnxModels.Input.Floats("input", input, 1, 3, height, width)], "output")[0].Data; }
        catch (FileNotFoundException) { throw new RealESRGANError(); }
    }

    /// <summary>
    /// Upscales <paramref name="image"/> (straight sRGB, 0…255) by <paramref name="scale"/> (2 or 4). The network always
    /// runs at 4×; for 2× each tile is averaged down as it lands, so memory stays at the output size. Colour goes through
    /// the network, alpha is resampled. <paramref name="progress"/> gets 0…1.
    /// </summary>
    public static Task<RGBAImage> UpscaleAsync(RGBAImage image, int scale = 4, Action<double>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Upscale(image, scale, progress, ct), ct);

    private static RGBAImage Upscale(RGBAImage image, int scale, Action<double>? progress, CancellationToken ct)
    {
        int w = image.Width, h = image.Height, f = Factor;
        int down = scale == 2 ? 2 : 1, ow = w * f / down, oh = h * f / down;
        var output = new float[(long)ow * oh * 4];
        var xs = Origins(w);
        var ys = Origins(h);
        double total = xs.Count * ys.Count, done = 0;
        int plane = Tile * Tile, outTile = Tile * f;
        var input = new float[3 * plane];
        var src = image.Pixels;
        foreach (int y0 in ys)
        {
            foreach (int x0 in xs)
            {
                ct.ThrowIfCancellationRequested();
                // Gather the tile, clamping at the image edges.
                for (int ty = 0; ty < Tile; ty++)
                {
                    int sy = Math.Clamp(y0 + ty, 0, h - 1);
                    for (int tx = 0; tx < Tile; tx++)
                    {
                        int sx = Math.Clamp(x0 + tx, 0, w - 1);
                        int s = (sy * w + sx) * 4, t = ty * Tile + tx;
                        input[t] = src[s] / 255;
                        input[plane + t] = src[s + 1] / 255;
                        input[2 * plane + t] = src[s + 2] / 255;
                    }
                }
                var result = Network(input, Tile, Tile);
                // Keep the tile's centre (its margin is context only), unless it touches the image edge.
                int keepX0 = x0 <= -Margin ? 0 : Margin, keepY0 = y0 <= -Margin ? 0 : Margin;
                // Columns and rows of the 4× tile to keep, clipped to the image.
                int tx0 = Math.Max(keepX0 * f, -x0 * f), tx1 = Math.Min((Tile - Margin) * f, (w - x0) * f);
                int ty0 = Math.Max(keepY0 * f, -y0 * f), ty1 = Math.Min((Tile - Margin) * f, (h - y0) * f);
                if (tx1 > tx0 && ty1 > ty0)
                {
                    float weight = 255f / (down * down);
                    int outPlane = outTile * outTile;
                    for (int ty = ty0; ty < ty1; ty++)
                    {
                        long dy = (y0 * f + ty) / down;
                        for (int c = 0; c < 3; c++)
                        {
                            int row = c * outPlane + ty * outTile;
                            if (down == 1)
                            {
                                long target = (dy * ow + x0 * f + tx0) * 4 + c;
                                for (int tx = tx0; tx < tx1; tx++, target += 4)
                                    output[target] = Math.Clamp(result[row + tx], 0f, 1f) * weight;
                            }
                            else
                            {
                                // Average 2 × 2: add neighbours across, accumulate the two rows.
                                long target = (dy * ow + (x0 * f + tx0) / 2) * 4 + c;
                                for (int tx = tx0; tx < tx1; tx += 2, target += 4)
                                    output[target] += (Math.Clamp(result[row + tx], 0f, 1f) + Math.Clamp(result[row + tx + 1], 0f, 1f)) * weight;
                            }
                        }
                    }
                }
                done += 1;
                progress?.Invoke(done / total);
            }
        }
        // Alpha: opaque images (the usual case) just get 255; otherwise plain resampling.
        float minAlpha = 255;
        for (int i = 3; i < src.Length; i += 4) minAlpha = Math.Min(minAlpha, src[i]);
        if (minAlpha >= 255)
        {
            for (long i = 3; i < output.Length; i += 4) output[i] = 255;
        }
        else
        {
            var alpha = image.Resized(ow, oh);
            for (long i = 3; i < output.Length; i += 4) output[i] = alpha.Pixels[i];
        }
        return new RGBAImage(ow, oh, output);
    }
}
