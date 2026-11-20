// Ported from Packages/GlimmerKit/Sources/GlimmerAI/OnDeviceVision.swift
//
// The Mac runs Apple's Core ML conversions of the same two models; here they are ONNX conversions (register entries
// `depth-anything-v2-small` and `sam2.1-tiny`) run by ONNX Runtime. The Mac's `pixelBuffer` helper (a picture drawn into a
// BGRA buffer of the model's size) is `PixelSampling.Tensor`, with each model's own normalisation, which Core ML did
// inside the model.
using Glimmer.Core;
using Microsoft.ML.OnnxRuntime;

namespace Glimmer.AI;

// MARK: Depth

/// <summary>Relative depth from Depth Anything V2 Small (ONNX, float16). Near = 255.</summary>
public static class DepthEstimator
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// The model's input size for a picture: the longer side 518 (the Core ML model's width), the shorter side in
    /// proportion, both multiples of 14 (the model's patch size). The Core ML model had one fixed size, 518 × 392,
    /// which stretched every picture to 4:3; the ONNX model takes any multiple of 14, so the picture keeps its shape.
    /// </summary>
    internal static (int Width, int Height) InputSize(int width, int height)
    {
        const int longSide = 518;
        double k = (double)longSide / Math.Max(width, height);
        static int Multiple(double v) => Math.Max(14, (int)Math.Round(v / 14) * 14);
        return width >= height ? (longSide, Multiple(height * k)) : (Multiple(width * k), longSide);
    }

    public static async Task<GrayImage> DepthAsync(RGBAImage image, ModelStore? store = null, CancellationToken ct = default)
    {
        var session = await OnnxModels.SessionAsync(store ?? ModelStore.Shared, OnDeviceModel.Depth, "onnx", "model_fp16.onnx", ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var (w, h) = InputSize(image.Width, image.Height);
            var input = PixelSampling.Tensor(image, w, h, Mean, Std);
            ct.ThrowIfCancellationRequested();
            var (depth, shape) = OnnxModels.Run(session, [OnnxModels.Input.Floats("pixel_values", input, 1, 3, h, w)], "predicted_depth")[0];
            int dh = (int)shape[^2], dw = (int)shape[^1];
            // Normalize: Depth Anything predicts relative inverse depth (larger = nearer).
            float lo = depth.Min(), hi = depth.Max();
            float span = Math.Max(hi - lo, 1e-3f);
            for (int i = 0; i < depth.Length; i++) depth[i] = (depth[i] - lo) / span;
            return SubjectSegmentation.Mask(depth, dw, dh, image.Width, image.Height);
        }, ct).ConfigureAwait(false);
    }
}

// MARK: Segment Anything

/// <summary>
/// Click-to-select with SAM 2.1 Tiny (ONNX, 8-bit). Encode the image once, then segment repeatedly from
/// positive/negative points. (The Swift type is an actor; here a semaphore keeps one call at a time.)
/// </summary>
public sealed class MagicSelector
{
    public readonly record struct Point
    {
        /// <summary>Image pixel coordinates (y-down).</summary>
        public Vec2 Location { get; init; }
        public bool Positive { get; init; }

        public Point(Vec2 location, bool positive)
        {
            Location = location;
            Positive = positive;
        }
    }

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly ModelStore? store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Encoded? encoded;

    private sealed record Encoded(int Key, float[] Level0, float[] Level1, float[] Level2, int Width, int Height);

    public MagicSelector(ModelStore? store = null)
    {
        this.store = store;
    }

    private ModelStore Store => store ?? ModelStore.Shared;

    internal const int InputSize = 1024;

    /// <summary>Runs the image encoder (cached per <paramref name="key"/>).</summary>
    public async Task PrepareAsync(RGBAImage image, int key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (encoded?.Key == key) return;
            var encoder = await OnnxModels.SessionAsync(Store, OnDeviceModel.SegmentAnything, "onnx", "vision_encoder_quantized.onnx", ct).ConfigureAwait(false);
            encoded = await Task.Run(() =>
            {
                var input = PixelSampling.Tensor(image, InputSize, InputSize, Mean, Std);
                ct.ThrowIfCancellationRequested();
                var outputs = OnnxModels.Run(encoder, [OnnxModels.Input.Floats("pixel_values", input, 1, 3, InputSize, InputSize)],
                                             "image_embeddings.0", "image_embeddings.1", "image_embeddings.2");
                return new Encoded(key, outputs[0].Data, outputs[1].Data, outputs[2].Data, image.Width, image.Height);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The best of SAM's three candidate masks for points, at image size (0...255). The Mac's Core ML mask decoder takes
    /// at most 15 prompt tokens (the points plus one padding token); the ONNX decoder has no such limit, but the same
    /// 14 are kept so Magic Select behaves the same on both.
    /// </summary>
    public const int MaxPoints = 14;

    /// <summary>Points beyond <see cref="MaxPoints"/>: the latest ones count (later clicks refine earlier ones).</summary>
    internal static IReadOnlyList<Point> Usable(IReadOnlyList<Point> points) => points.Skip(Math.Max(0, points.Count - MaxPoints)).ToList();

    public async Task<GrayImage> SegmentAsync(IReadOnlyList<Point> points, CancellationToken ct = default)
    {
        var usable = Usable(points);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (encoded is not { } e || usable.Count == 0)
                throw new InvalidOperationException("Magic Select has no prepared picture, or no points.");
            var decoder = await OnnxModels.SessionAsync(Store, OnDeviceModel.SegmentAnything, "onnx", "prompt_encoder_mask_decoder_quantized.onnx", ct).ConfigureAwait(false);
            return await Task.Run(() => Decode(decoder, e, usable), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static GrayImage Decode(InferenceSession decoder, Encoded e, IReadOnlyList<Point> points)
    {
        int n = points.Count;
        var coords = new float[n * 2];
        var labels = new long[n];
        double sx = (double)InputSize / e.Width, sy = (double)InputSize / e.Height;
        for (int i = 0; i < n; i++)
        {
            coords[i * 2] = (float)(points[i].Location.X * sx);
            coords[i * 2 + 1] = (float)(points[i].Location.Y * sy);
            labels[i] = points[i].Positive ? 1 : 0;
        }
        var outputs = OnnxModels.Run(decoder,
        [
            OnnxModels.Input.Floats("input_points", coords, 1, 1, n, 2),
            OnnxModels.Input.Longs("input_labels", labels, 1, 1, n),
            OnnxModels.Input.Floats("input_boxes", [], 1, 0, 4),
            OnnxModels.Input.Floats("image_embeddings.0", e.Level0, 1, 32, 256, 256),
            OnnxModels.Input.Floats("image_embeddings.1", e.Level1, 1, 64, 128, 128),
            OnnxModels.Input.Floats("image_embeddings.2", e.Level2, 1, 256, 64, 64),
        ], "iou_scores", "pred_masks");
        var scores = outputs[0].Data;
        var (masks, shape) = outputs[1];
        int best = 0;
        for (int i = 0; i < 3; i++) if (scores[i] > scores[best]) best = i;
        // Logits → 0...255 at 256², then stretched back to the image.
        int mh = (int)shape[^2], mw = (int)shape[^1];
        var low = new float[mw * mh];
        int offset = best * mw * mh;
        for (int i = 0; i < low.Length; i++) low[i] = 255 / (1 + MathF.Exp(-masks[offset + i] * 4));  // sharpened sigmoid keeps soft edges
        return new GrayImage(mw, mh, low).Resized(e.Width, e.Height);
    }

    public void Reset()
    {
        gate.Wait();
        try { encoded = null; }
        finally { gate.Release(); }
    }
}
