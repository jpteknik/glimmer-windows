// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/SmartAnalysisTests.swift
// The pictures are drawn by TestPictures (SkiaSharp) as the original draws them with Core Graphics. The horizon test now
// tests this port's own detector (ClassicVision) with the same tilted landscapes and the same tolerance; the tests after
// the ported ones are new: saliency, a balanced picture, and the faces and text the app's detectors report.
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

[Collection("On-device models")]
public class SmartAnalysisTests
{
    [Fact]
    public void DarkFlatPhotoGetsANoticeableEnhancement()
    {
        var image = TestPictures.Landscape(tilt: 0, gain: 0.35);
        var recipe = SmartAnalysis.EnhanceRecipeFor(image);
        Assert.True(recipe.IsNoticeable);
        // Lifts the image: the curve's midpoint rises.
        Assert.True(CurveMath.Evaluate(recipe.Curve, 0.3) > 0.3);
        Assert.NotEmpty(recipe.Adjustments);
        Assert.All(recipe.Adjustments, a => Assert.Contains(a.Kind, new[] { AdjustmentKind.Curves, AdjustmentKind.Vibrance, AdjustmentKind.ColorBalance }));
    }

    [Fact]
    public void ToneStatsSeeADarkImage()
    {
        var stats = SmartAnalysis.ToneStatsOf(TestPictures.Landscape(tilt: 0, gain: 0.3));
        Assert.True(stats.IsDark);
        Assert.True(stats.High < 0.5);
    }

    [Fact]
    public void FoldingShadowsKeepsTheCurveMonotoneAndPinned()
    {
        var curve = new[] { new CurvePoint(0, 0), new CurvePoint(0.25, 0.25), new CurvePoint(0.5, 0.5), new CurvePoint(0.75, 0.75), new CurvePoint(1, 1) };
        var folded = SmartAnalysis.Folded(curve, shadows: 1, highlights: 0.5);
        Assert.True(folded[0].Y == 0 && folded[^1].Y == 1);
        Assert.True(folded[1].Y > 0.25);   // shadows lifted
        Assert.True(folded[3].Y < 0.75);   // highlights eased
        Assert.All(folded.Zip(folded.Skip(1)), p => Assert.True(p.First.Y <= p.Second.Y));
    }

    [Fact]
    public async Task HorizonTiltIsReportedWithItsSign()
    {
        var rising = (await SmartAnalysis.InsightsAsync(TestPictures.Landscape(tilt: 4))).HorizonDegrees;
        var falling = (await SmartAnalysis.InsightsAsync(TestPictures.Landscape(tilt: -4))).HorizonDegrees;
        var level = (await SmartAnalysis.InsightsAsync(TestPictures.Landscape(tilt: 0))).HorizonDegrees;
        Assert.Null(level);
        Assert.NotNull(rising);
        Assert.NotNull(falling);
        Assert.True(Math.Abs(rising!.Value - 4) < 1.5, $"rising {rising}");
        Assert.True(Math.Abs(falling!.Value + 4) < 1.5, $"falling {falling}");
    }

    [Fact]
    public void TiltOutsideTheUsableRangeIsIgnored()
    {
        Assert.Null(SmartAnalysis.UsableTilt(0.1));
        Assert.Null(SmartAnalysis.UsableTilt(35));
        Assert.Equal(-3, SmartAnalysis.UsableTilt(-3));
    }

    [Fact]
    public void CropForANewAspectSlidesOverTheSubject()
    {
        // Subject on the right of a 4:3 image; ask for a square.
        var subject = Rects.Make(0.7, 0.4, 0.15, 0.2);
        var crop = SmartAnalysis.SuggestedCrop(subject, 800, 600, aspect: 1)!.Value;
        Assert.True(crop.Width == 600 && crop.Height == 600);
        Assert.True(crop.MaxX <= 800 && crop.MinX >= 0);
        Assert.True(crop.MinX > 100);  // moved toward the subject
        Assert.True(crop.Contains(0.775 * 800, 0.5 * 600));
    }

    [Fact]
    public void FreeCropTightensAroundASmallSubject()
    {
        var subject = Rects.Make(0.4, 0.4, 0.1, 0.1);
        var crop = SmartAnalysis.SuggestedCrop(subject, 1000, 500, aspect: null)!.Value;
        Assert.True(Math.Abs(crop.Width / crop.Height - 2) < 0.02);  // keeps the image's proportions
        Assert.True(crop.Width < 1000);
        Assert.True(crop.Contains(450, 225));
    }

    [Fact]
    public void ASubjectFillingTheFrameNeedsNoCrop()
    {
        var subject = Rects.Make(0.05, 0.05, 0.9, 0.9);
        Assert.Null(SmartAnalysis.SuggestedCrop(subject, 800, 600, aspect: null));
    }

    [Fact]
    public void PortraitCropFramesHeadAndShoulders()
    {
        // A face upper-left in a 4000 × 3000 landscape.
        var face = Rects.Make(0.2, 0.2, 0.08, 0.1);
        var crop = SmartAnalysis.SuggestedCrop(default, 4000, 3000, aspect: 4.0 / 5, faces: [face])!.Value;
        Assert.True(Math.Abs(crop.Width / crop.Height - 0.8) < 0.01);
        var faceBox = Rects.Make(800, 600, 320, 300);
        Assert.True(crop.Contains(faceBox));
        // Eyes around the upper third, not centred.
        double rel = (faceBox.MidY() - crop.MinY) / crop.Height;
        Assert.True(rel > 0.3 && rel < 0.45);
        Assert.True(crop.Width < 4000 * 0.5);  // tight, not the whole frame
    }

    [Fact]
    public void PortraitCropKeepsAGroupTogether()
    {
        var faces = new[] { Rects.Make(0.2, 0.3, 0.06, 0.08), Rects.Make(0.6, 0.32, 0.06, 0.08) };
        var crop = SmartAnalysis.SuggestedCrop(default, 3000, 2000, aspect: 16.0 / 9, faces: faces)!.Value;
        Assert.True(crop.MinX < 0.2 * 3000 && crop.MaxX > 0.66 * 3000);
        Assert.True(crop.MinX >= 0 && crop.MaxX <= 3000 && crop.MinY >= 0 && crop.MaxY <= 2000);
    }

    // MARK: New in the port

    /// <summary>The spectral residual finds a red square on a plain wall, and nothing on a plain wall.</summary>
    [Fact]
    public void TheSalientPartIsTheObjectOnAPlainBackground()
    {
        var withObject = TestPictures.Draw(800, 600, canvas =>
        {
            using (var wall = TestPictures.Fill(0.8, 0.82, 0.85)) canvas.DrawRect(0, 0, 800, 600, wall);
            using var red = TestPictures.Fill(0.85, 0.15, 0.1);
            canvas.DrawRect(500, 150, 120, 140, red);
        });
        var rect = ClassicVision.SalientRect(withObject);
        Assert.NotNull(rect);
        var r = rect!.Value;
        // The object spans x 0.625…0.775, y 0.25…0.48; the box holds its middle and isn't the whole picture.
        Assert.True(r.Contains(0.7, 0.36), $"{r}");
        Assert.True(r.Width < 0.5 && r.Height < 0.6, $"{r}");
        var plain = new RGBAImage(800, 600, new System.Numerics.Vector4(200, 205, 210, 255));
        Assert.Null(ClassicVision.SalientRect(plain));
    }

    /// <summary>
    /// A well exposed, colourful picture with its full range needs nothing from Auto Enhance; the same picture darkened
    /// and greyed does.
    /// </summary>
    [Fact]
    public void ABalancedPictureNeedsNoEnhancement()
    {
        // Light from black (top) to white (bottom), hue across, colour strongest in the middle tones.
        static RGBAImage Picture(double gain, double colour)
        {
            int w = 600, h = 400;
            var px = new float[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                double l = (y + 0.5) / h;
                double chroma = colour * Math.Min(l, 1 - l);
                for (int x = 0; x < w; x++)
                {
                    double hue = 2 * Math.PI * x / w;
                    int i = (y * w + x) * 4;
                    px[i] = (float)(Math.Clamp(l + chroma * Math.Cos(hue), 0, 1) * 255 * gain);
                    px[i + 1] = (float)(Math.Clamp(l + chroma * Math.Cos(hue - 2.094), 0, 1) * 255 * gain);
                    px[i + 2] = (float)(Math.Clamp(l + chroma * Math.Cos(hue + 2.094), 0, 1) * 255 * gain);
                    px[i + 3] = 255;
                }
            }
            return new RGBAImage(w, h, px);
        }
        var balanced = SmartAnalysis.EnhanceRecipeFor(Picture(1, 0.9));
        Assert.False(balanced.IsNoticeable, string.Join(", ", balanced.Curve.Select(p => $"({p.X:0.00},{p.Y:0.000})")) + $" vibrance {balanced.Vibrance}");
        var dull = SmartAnalysis.EnhanceRecipeFor(Picture(0.4, 0.1));
        Assert.True(dull.IsNoticeable);
        Assert.True(dull.Vibrance > 0.08);
    }

    /// <summary>The faces the app's detector reports become the insights' normalised face rectangles; no detector, no faces.</summary>
    [Fact]
    public async Task FacesComeFromTheAppsDetector()
    {
        var image = TestPictures.Landscape(width: 2000, height: 1500);
        // The analysis runs on a copy whose longer side is 1024: the detector sees that copy and reports in its pixels.
        var detector = new FakeFaceDetector(Rects.Make(204.8, 153.6, 102.4, 76.8));
        var insights = await SmartAnalysis.InsightsAsync(image, detector);
        Assert.Equal(1, insights.FaceCount);
        var f = insights.FaceRects[0];
        Assert.True(Math.Abs(f.MinX - 0.2) < 1e-9 && Math.Abs(f.MinY - 0.2) < 1e-9 && Math.Abs(f.Width - 0.1) < 1e-9 && Math.Abs(f.Height - 0.1) < 1e-9, $"{f}");
        Assert.Equal(0, (await SmartAnalysis.InsightsAsync(image)).FaceCount);
    }

    /// <summary>
    /// Readable text, as the Mac counts it: lines with a confidence above one half, at least 2 % of the picture high,
    /// six characters in all.
    /// </summary>
    [Fact]
    public async Task TextIsCountedAsTheMacCountsIt()
    {
        var image = TestPictures.Landscape(width: 1000, height: 500);
        async Task<bool> HasText(params ImageText.Line[] lines) => (await SmartAnalysis.InsightsAsync(image, textRecognizer: new FakeTextRecognizer(lines))).HasText;
        Assert.True(await HasText(new ImageText.Line("SALE 50%", Rects.Make(100, 100, 300, 40), 0.9)));
        Assert.False(await HasText(new ImageText.Line("SALE", Rects.Make(100, 100, 300, 40), 0.9)));          // too few characters
        Assert.False(await HasText(new ImageText.Line("SALE 50%", Rects.Make(100, 100, 300, 40), 0.4)));      // not confident
        Assert.False(await HasText(new ImageText.Line("fine print here", Rects.Make(100, 100, 300, 5), 0.9))); // 5 of 500 px: under 2 %
        Assert.True(await HasText(new ImageText.Line("OPEN", Rects.Make(0, 0, 90, 30), 0.8), new ImageText.Line("DAILY", Rects.Make(0, 40, 90, 30), 0.8)));
        Assert.False((await SmartAnalysis.InsightsAsync(image)).HasText);
    }
}
