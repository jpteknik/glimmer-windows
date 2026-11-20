// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/PhotoToolsTests.swift
// The document tests now test this port's own detector and perspective correction (ClassicVision) with the same
// pictures and tolerances; the tests after the ported ones are new.
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

[Collection("On-device models")]
public class PhotoToolsTests
{
    [Fact]
    public void SharpBeatsBlurred()
    {
        var sharp = TestPictures.Detailed();
        var soft = TestPictures.Blurred(sharp, radius: 8);
        double a = PhotoQuality.Sharpness(sharp), b = PhotoQuality.Sharpness(soft);
        Assert.True(a > PhotoQuality.BlurThreshold, $"sharp {a}");
        Assert.True(b < PhotoQuality.BlurThreshold, $"soft {b}");
        Assert.True(a > b);
    }

    [Fact]
    public void FlatGraphicsAreNeverBlurry()
    {
        var graphic = TestPictures.FlatGraphic();
        Assert.True(PhotoQuality.ColorVariety(graphic) < PhotoQuality.PhotoColorVariety);
        Assert.False(PhotoQuality.Rate(graphic).IsBlurry);
        Assert.True(PhotoQuality.ColorVariety(TestPictures.Detailed()) >= PhotoQuality.PhotoColorVariety);
    }

    [Fact]
    public void BlurryPhotosRankLower()
    {
        var sharp = new PhotoQuality.Rating(Aesthetics: 0.3, IsUtility: false, Sharpness: 0.9);
        var soft = new PhotoQuality.Rating(Aesthetics: 0.3, IsUtility: false, Sharpness: 0.2);
        Assert.True(soft.IsBlurry && !sharp.IsBlurry);
        Assert.True(sharp.Score > soft.Score);
        Assert.True(sharp.Stars >= soft.Stars);
        Assert.InRange(new PhotoQuality.Rating(-1, false, 0).Stars, 1, 5);
        Assert.Equal(5, new PhotoQuality.Rating(1, false, 1).Stars);
    }

    [Fact]
    public void FindsAndFlattensADocument()
    {
        var (image, truth) = TestPictures.PaperInPerspective();
        var quad = DocumentDetection.QuadIn(image);
        Assert.NotNull(quad);
        foreach (var (a, b) in quad!.Value.Corners.Zip(truth.Corners))
            Assert.True(Vec2.Distance(a, b) < 40, $"corner {a} vs {b}");
        var flat = DocumentDetection.Corrected(new LinearImage(image.Width, image.Height, image.ToLinearPremultiplied()), quad.Value);
        Assert.NotNull(flat);
        var size = quad.Value.OutputSize;
        // Core Image allows for foreshortening, so the result is at least as big as the edges, not exactly them.
        Assert.True(flat!.Width > size.Width * 0.8 && flat.Width < size.Width * 1.5);
        Assert.True(flat.Height > size.Height * 0.8 && flat.Height < size.Height * 1.5);
        Assert.True(flat.Height > flat.Width);  // a portrait sheet
    }

    [Fact]
    public void NoDocumentInAScene()
    {
        Assert.Null(DocumentDetection.QuadIn(TestPictures.Detailed()));
    }

    // MARK: New in the port

    /// <summary>The flattened sheet is the paper, corner to corner: no desk shows near its corners or in its margin.</summary>
    [Fact]
    public void TheFlattenedSheetIsThePaper()
    {
        var (image, truth) = TestPictures.PaperInPerspective();
        var flat = DocumentDetection.Corrected(new LinearImage(image.Width, image.Height, image.ToLinearPremultiplied()), truth)!;
        var srgb = RGBAImage.FromLinearPremultiplied(flat.Pixels, flat.Width, flat.Height);
        int w = flat.Width, h = flat.Height;
        foreach (var (x, y) in new[] { (6, 6), (w - 7, 6), (w - 7, h - 7), (6, h - 7), (w * 9 / 10, h / 2) })
        {
            var p = TestPictures.Pixel(srgb, x, y);
            Assert.True(p.X > 230 && p.Y > 230 && p.Z > 225, $"({x}, {y}): {p}");
        }
        // The text is in it: a dark row a little below the top, toward the left.
        bool ink = false;
        for (int y = h / 12; y < h / 4 && !ink; y++)
            for (int x = w / 8; x < w / 3 && !ink; x++)
                ink = TestPictures.Pixel(srgb, x, y).X < 100;
        Assert.True(ink);
    }

    /// <summary>The stand-in for Apple's aesthetics score ranks a well exposed, colourful picture above a dark, dull one.</summary>
    [Fact]
    public void TheTechnicalScoreRanksExposureAndColour()
    {
        var good = TestPictures.Landscape(tilt: 0);
        var dull = TestPictures.Landscape(tilt: 0, sky: (0.3, 0.3, 0.32), ground: (0.22, 0.22, 0.2), gain: 0.5);
        double g = PhotoQuality.TechnicalQuality(good), d = PhotoQuality.TechnicalQuality(dull);
        Assert.InRange(g, -1, 1);
        Assert.InRange(d, -1, 1);
        Assert.True(g > d + 0.3, $"good {g}, dull {d}");
        Assert.True(PhotoQuality.Rate(good).Score > PhotoQuality.Rate(dull).Score);
    }

    /// <summary>
    /// A screenshot-like graphic and a photographed document count as "utility"; a textured photo doesn't. (The test
    /// landscape has 38 colours: by the Mac's own measure a graphic.)
    /// </summary>
    [Fact]
    public void UtilityPicturesAreRecognised()
    {
        Assert.True(PhotoQuality.Rate(TestPictures.FlatGraphic()).IsUtility);
        Assert.True(PhotoQuality.Rate(TestPictures.PaperInPerspective().Image).IsUtility);
        Assert.False(PhotoQuality.Rate(TestPictures.Detailed()).IsUtility);
    }
}

public class SkySelectionTests
{
    /// <summary>Depth for a landscape: the sky (top half) far away, the ground coming nearer toward the bottom.</summary>
    internal static GrayImage Depth(int width, int height)
    {
        var v = new float[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                v[y * width + x] = y < height / 2 ? 5 : 60 + (float)y / height * 190;
        return new GrayImage(width, height, v);
    }

    [Fact]
    public void PromptsSitInTheSkyAndBelowIt()
    {
        var image = TestPictures.Landscape(tilt: 0);  // sky above y = 300 of 600
        var points = SkySelection.Prompts(image, Depth(800, 600));
        Assert.NotNull(points);
        Assert.True(points!.Count <= MagicSelector.MaxPoints);
        var positives = points.Where(p => p.Positive).ToList();
        var negatives = points.Where(p => !p.Positive).ToList();
        Assert.True(positives.Count >= 2);
        Assert.All(positives, p => Assert.True(p.Location.Y < 290, $"{p.Location}"));
        Assert.NotEmpty(negatives);
        Assert.All(negatives, p => Assert.True(p.Location.Y > 310, $"{p.Location}"));
    }

    [Fact]
    public void NoSkyWhenTheTopIsDarkForeground()
    {
        // Bright ground below a dark, near top: nothing to seed from.
        var image = TestPictures.Landscape(tilt: 0, sky: (0.1, 0.1, 0.1), ground: (0.9, 0.9, 0.8));
        var near = new GrayImage(800, 600, 220f);
        Assert.Null(SkySelection.Prompts(image, near));
    }

    [Fact]
    public void MagicSelectKeepsTheLatestPoints()
    {
        var points = Enumerable.Range(0, 20).Select(i => new MagicSelector.Point(new Vec2(i, 0), positive: true)).ToList();
        var usable = MagicSelector.Usable(points);
        Assert.Equal(MagicSelector.MaxPoints, usable.Count);
        Assert.Equal(19, usable[^1].Location.X);
    }

    // MARK: New in the port

    /// <summary>The grown sky, scaled up: sky above the horizon, none below.</summary>
    [Fact]
    public void TheSkyMaskCoversTheSky()
    {
        var mask = SkySelection.Mask(TestPictures.Landscape(tilt: 0), Depth(800, 600));
        Assert.NotNull(mask);
        Assert.True(mask!.Width == 800 && mask.Height == 600);
        Assert.True(TestPictures.At(mask, 400, 100) > 200);
        Assert.True(TestPictures.At(mask, 400, 500) < 50);
    }
}
