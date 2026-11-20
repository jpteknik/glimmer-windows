// New in the port, no Swift file of its own. On the Mac, text recognition and face detection come from Vision, which
// every Mac has. On Windows the system's own APIs do them (Windows.Media.Ocr, Windows.Media.FaceAnalysis), and only the
// WPF app can call those; this library defines what it needs from them, so the analysis stays testable on any OS.
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>
/// Reads the text in a picture. The WPF app implements it with <c>Windows.Media.Ocr</c>
/// (<c>OcrEngine.TryCreateFromUserProfileLanguages</c>; pictures larger than <c>OcrEngine.MaxImageDimension</c> scaled
/// down first, the boxes scaled back).
/// </summary>
public interface IImageTextRecognizer
{
    /// <summary>
    /// Every line of text in <paramref name="image"/> (straight sRGB): its words joined with spaces, its box in the
    /// image's pixels (y down) and a confidence 0…1. <c>Windows.Media.Ocr</c> gives no confidence; it reports 1.
    /// The order doesn't matter: <see cref="ImageText.LinesAsync"/> sorts.
    /// </summary>
    Task<IReadOnlyList<ImageText.Line>> RecognizeAsync(RGBAImage image, CancellationToken ct = default);
}

/// <summary>
/// Finds faces. The WPF app implements it with <c>Windows.Media.FaceAnalysis.FaceDetector</c> (on a Gray8 copy of the
/// picture).
/// </summary>
public interface IFaceDetector
{
    /// <summary>The faces in <paramref name="image"/>, as rectangles in the image's pixels (y down).</summary>
    Task<IReadOnlyList<CGRectLike>> DetectAsync(RGBAImage image, CancellationToken ct = default);
}

/// <summary>
/// What the computer offers beside the downloaded models, set once by the app when it starts. Null is "not available"
/// (the tests, Linux, or a Windows without a text recognition language): the analysis then goes without (no faces, no
/// text), Suggest Crop frames the salient part instead of the faces, and the commands that read text say why they can't.
/// </summary>
public static class OnDeviceServices
{
    public static IImageTextRecognizer? TextRecognizer { get; set; }
    public static IFaceDetector? FaceDetector { get; set; }
}

/// <summary>Copy Text in Image and Image Text to Layers need a text recognizer, and this computer has none.</summary>
public sealed class TextRecognitionUnavailableError : Exception
{
    public override string Message =>
        "Text recognition isn't available on this computer. Windows reads text with an installed language's optical "
        + "character recognition; add one in Settings › Time & language › Language & region, then try again.";
}

/// <summary>Swift's <c>CGRect</c> arithmetic on <see cref="CGRectLike"/> (min corner plus size, y down).</summary>
internal static class Rects
{
    public static CGRectLike Make(double x, double y, double width, double height) => new(x, y, x + width, y + height);

    public static double MidX(this CGRectLike r) => (r.MinX + r.MaxX) / 2;
    public static double MidY(this CGRectLike r) => (r.MinY + r.MaxY) / 2;

    /// <summary>CGRect's <c>contains(_:)</c> for a point: the max edges are outside.</summary>
    public static bool Contains(this CGRectLike r, double x, double y) => x >= r.MinX && x < r.MaxX && y >= r.MinY && y < r.MaxY;

    /// <summary>CGRect's <c>contains(_:)</c> for a rectangle.</summary>
    public static bool Contains(this CGRectLike r, CGRectLike other) =>
        other.MinX >= r.MinX && other.MaxX <= r.MaxX && other.MinY >= r.MinY && other.MaxY <= r.MaxY;

    /// <summary>CGRect's <c>union</c>, with null standing for <c>CGRect.null</c>.</summary>
    public static CGRectLike Union(CGRectLike? a, CGRectLike b) =>
        a is not { } r ? b : new(Math.Min(r.MinX, b.MinX), Math.Min(r.MinY, b.MinY), Math.Max(r.MaxX, b.MaxX), Math.Max(r.MaxY, b.MaxY));

    /// <summary>CGRect's <c>intersection</c>; null when they don't overlap.</summary>
    public static CGRectLike? Intersection(this CGRectLike a, CGRectLike b)
    {
        double x0 = Math.Max(a.MinX, b.MinX), y0 = Math.Max(a.MinY, b.MinY), x1 = Math.Min(a.MaxX, b.MaxX), y1 = Math.Min(a.MaxY, b.MaxY);
        return x1 > x0 && y1 > y0 ? new CGRectLike(x0, y0, x1, y1) : null;
    }

    /// <summary>CGRect's <c>integral</c>: the smallest rectangle with whole coordinates that holds it.</summary>
    public static CGRectLike Integral(this CGRectLike r) =>
        new(Math.Floor(r.MinX), Math.Floor(r.MinY), Math.Ceiling(r.MaxX), Math.Ceiling(r.MaxY));
}
