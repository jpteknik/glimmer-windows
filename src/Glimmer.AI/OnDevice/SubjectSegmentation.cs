// Ported from Packages/GlimmerKit/Sources/GlimmerAI/SubjectSegmentation.swift
//
// The Mac asks Vision for the foreground instance mask (VNGenerateForegroundInstanceMaskRequest). Windows has no such
// API, so the mask comes from IS-Net (Qin et al., "Highly Accurate Dichotomous Image Segmentation", ECCV 2022), its
// general-use weights in IMG.LY's float16 ONNX conversion, downloaded once like the depth and SAM models (register
// entry `isnet-general-use`). Vision finds separate instances; IS-Net gives one mask of everything salient, which is what
// both callers (Remove Background, Select Subject) use anyway.
namespace Glimmer.AI;

public sealed class SegmentationError : Exception, IEquatable<SegmentationError>
{
    /// <summary>The Swift case name.</summary>
    public string Case { get; }

    private SegmentationError(string @case) { Case = @case; }

    public static SegmentationError NoSubject() => new("noSubject");

    public override string Message => Case switch
    {
        _ => "No subject was found in the image.",
    };

    public bool Equals(SegmentationError? other) => other is not null && Case == other.Case;
    public override bool Equals(object? obj) => Equals(obj as SegmentationError);
    public override int GetHashCode() => Case.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// On-device subject segmentation. Replaces the web app's in-browser RMBG-1.4 model (whose licence is non-commercial):
/// free, offline once downloaded, and a second or two on a CPU.
/// </summary>
public static partial class SubjectSegmentation
{
    /// <summary>IS-Net's working size: the picture is stretched to it, as the model was trained.</summary>
    internal const int SubjectSide = 1024;

    /// <summary>A soft mask (0...255) of all foreground subjects at the image's size.</summary>
    public static async Task<GrayImage> SubjectMaskAsync(RGBAImage image, ModelStore? store = null, CancellationToken ct = default)
    {
        var session = await OnnxModels.SessionAsync(store ?? ModelStore.Shared, OnDeviceModel.Subject, "onnx", "model_fp16.onnx", ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            // rembg's normalisation for this model: value / 255 − 0.5.
            var input = PixelSampling.Tensor(image, SubjectSide, SubjectSide, [0.5f, 0.5f, 0.5f], [1f, 1f, 1f]);
            ct.ThrowIfCancellationRequested();
            var (mask, _) = OnnxModels.Run(session, [OnnxModels.Input.Floats("input", input, 1, 3, SubjectSide, SubjectSide)], "output")[0];
            // Nothing above one half anywhere: no subject, as Vision returning no instances.
            if (!mask.Any(v => v > 0.5f)) throw SegmentationError.NoSubject();
            return Mask(mask, SubjectSide, SubjectSide, image.Width, image.Height);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A 0…1 plane stretched to the image's size, as 0…255 (Swift's <c>grayImage(from:width:height:)</c>).</summary>
    internal static GrayImage Mask(ReadOnlySpan<float> plane, int w, int h, int width, int height)
    {
        var values = PixelSampling.ResizePlane(plane, w, h, width, height);
        for (int i = 0; i < values.Length; i++) values[i] = Math.Clamp(values[i], 0f, 1f) * 255;
        return new GrayImage(width, height, values);
    }
}
