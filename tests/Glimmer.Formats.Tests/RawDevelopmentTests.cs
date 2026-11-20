// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/RawDevelopmentTests.swift (RawDevelopmentTests and
// RawLayerTests), on the DNG the tests write themselves, decoded by the tests' stand-in for the system decoder
// (TestDngDecoder) where the Swift uses Apple's. What the stand-in can't stand in for, and so is said differently here:
// the camera list (the decoder's own), and a stored process the system no longer offers (a Mac process, "9.dng", here).
// Not ported: RawRealFiles and RawDraftMeasurements (camera files through the system decoder, which the tests can't reach;
// the second measures Core Image's own options). The measurements the Swift prints are kept in the assertion messages.
#if GLIMMER_RAW
using Glimmer.Core;
using SkiaSharp;

namespace Glimmer.Formats.Tests;

[CollectionDefinition("Raw", DisableParallelization = true)]
public sealed class RawCollection;

/// <summary>RAW development through the system decoder (a stand-in here), on a DNG the tests write themselves.</summary>
[Collection("Raw")]
public class RawDevelopmentTests
{
    public RawDevelopmentTests() => RawDevelopment.Decoder = new TestDngDecoder();

    /// <summary>The mean red, green and blue (0…255, sRGB) of a small square of a developed picture.</summary>
    internal static (double R, double G, double B) Colour(SKBitmap image, int x, int y, int size = 12)
    {
        var (px, w, _) = ExportedFiles.Read(image);
        double r = 0, g = 0, b = 0;
        for (int dy = 0; dy < size; dy++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                int i = ((y + dy) * w + x + dx) * 4;
                r += px[i]; g += px[i + 1]; b += px[i + 2];
            }
        }
        double n = size * size;
        return (r / n, g / n, b / n);
    }

    private static (double R, double G, double B) DevelopedColour(RawSettings settings, int x, int y, byte[]? data = null)
    {
        using var image = RawDevelopment.Develop(data ?? DNGTestWriter.Ramp, settings).Image;
        return Colour(image, x, y);
    }

    /// <summary>The draft is the real development: what is shown while a control moves is what is kept on release.</summary>
    [Fact]
    public void TheDraftIsTheFinishedPictureAtEverySetting()
    {
        var draft = new RawDraft(DNGTestWriter.Ramp);
        Assert.True(draft.Width == 640 && draft.Height == 480 && draft.Scale == 1);
        foreach (double exposure in new[] { 0.0, 1.0, -1.0, 0.4 })   // the same decoder, one setting after another, back and forth
        {
            var settings = new RawSettings { Exposure = exposure };
            using var shown = draft.Picture(settings);
            var a = Colour(shown, 300, 200);
            var b = DevelopedColour(settings, 300, 200);
            Assert.True(Math.Abs(a.R - b.R) < 1.5 && Math.Abs(a.G - b.G) < 1.5 && Math.Abs(a.B - b.B) < 1.5, $"exposure {exposure}: {a} against {b}");
        }
        // A smaller draft keeps the proportions.
        var small = new RawDraft(DNGTestWriter.Ramp, maxSide: 320);
        Assert.True(small.Width == 320 && small.Height == 240);
        using var picture = small.Picture(new RawSettings());
        Assert.Equal(320, picture.Width);
        // And is the finished picture made small, on the ramp's smooth parts.
        using var finished = RawDevelopment.Develop(DNGTestWriter.Ramp, new RawSettings { Exposure = 0.5 }, scale: small.Scale).Image;
        using var shownSmall = small.Picture(new RawSettings { Exposure = 0.5 });
        var c = Colour(shownSmall, 150, 50);
        var d = Colour(finished, 150, 50);
        Assert.True(Math.Abs(c.G - d.G) < 1.5, $"{c} against {d}");
        Assert.Throws<RawDevelopment.Problem>(() => new RawDraft(Enumerable.Repeat((byte)7, 4_000).ToArray()));
    }

    /// <summary>The agent API's rules: only what is given changes, nothing is clamped, nothing unsupported is accepted
    /// silently.</summary>
    [Fact]
    public void ARequestChangesOnlyWhatItNamesAndIsRefusedOutsideWhatTheFileAllows()
    {
        var info = RawDevelopment.Info(DNGTestWriter.Ramp) with { SupportsContrast = true, SupportsLensCorrection = true };
        var current = new RawSettings { Exposure = 0.5, Shadows = 1.4, Process = "9.dng" };
        var changed = RawRequest.Settings(current, info, new Dictionary<string, double> { ["temperature"] = 4_000 }, new Dictionary<string, bool>(), asShot: false);
        Assert.True(changed.Temperature == 4_000 && changed.Exposure == 0.5 && changed.Shadows == 1.4 && changed.Process == "9.dng");
        // As shot first, then the given value; the process stays.
        var reset = RawRequest.Settings(current, info, new Dictionary<string, double> { ["exposure"] = 1 }, new Dictionary<string, bool>(), asShot: true);
        Assert.True(reset.Exposure == 1 && reset.Shadows == 1 && reset.Temperature is null && reset.Process == "9.dng");
        RawRequest.Refusal? Refusal(RawInfo about, Dictionary<string, double> numbers, Dictionary<string, bool>? flags = null)
        {
            try { _ = RawRequest.Settings(current, about, numbers, flags ?? [], asShot: false); return null; }
            catch (RawRequest.Refusal refusal) { return refusal; }
        }
        Assert.Equal("out_of_range", Refusal(info, new() { ["exposure"] = 7 })?.Reason);
        Assert.Equal("exposure must be -5…5 for this file; 7 is outside that.", Refusal(info, new() { ["exposure"] = 7 })?.Message);
        Assert.True(Refusal(info, new() { ["temperature"] = 900 })?.Reason == "out_of_range" && Refusal(info, new() { ["shadows"] = double.NaN })?.Reason == "out_of_range");
        Assert.Equal("unknown_control", Refusal(info, new() { ["brightness"] = 1 })?.Reason);
        Assert.Equal("shadows must be 0…2 for this file; 2.50 is outside that.", Refusal(info, new() { ["shadows"] = 2.5 })?.Message);
        // A control the file doesn't have is refused by name, whatever the value.
        var without = info with { SupportsContrast = false, SupportsLensCorrection = false };
        Assert.Equal("This RAW file has no contrast control: Glimmer doesn't offer it on this computer.", Refusal(without, new() { ["contrast"] = 0.5 })?.Message);
        Assert.Equal("unsupported_control", Refusal(without, [], new() { ["lensCorrection"] = true })?.Reason);
        Assert.False(RawRequest.Controls(without).First(c => c.Name == "contrast").Supported);
    }

    [Fact]
    public void TheHeaderSaysWhatTheFileIsBeforeAnythingIsDeveloped()
    {
        var decoder = new TestDngDecoder();
        RawDevelopment.Decoder = decoder;
        var info = RawDevelopment.Info(DNGTestWriter.Ramp);
        Assert.True(info.Width == 640 && info.Height == 480 && info.Processes.Count > 0);
        Assert.True(info.Temperature > 2_000 && info.Temperature < 50_000);
        Assert.Equal(0, decoder.Decodes);   // nothing developed
        // What Glimmer develops here, and what it doesn't.
        Assert.True(info.SupportsContrast && !info.SupportsSharpness && !info.SupportsLuminanceNoise && !info.SupportsColorNoise
                    && !info.SupportsHighlightRecovery && !info.SupportsLensCorrection);
        // Larger than this computer handles: refused from the header, before any decode.
        var error = Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(DNGTestWriter.Ramp, maxPixels: 1_000));
        Assert.True(error.TooLarge);
        Assert.Equal(0, decoder.Decodes);
    }

    [Fact]
    public void DevelopingGivesThePictureAndExposureAndWhiteBalanceChangeIt()
    {
        var (image, applied) = RawDevelopment.Develop(DNGTestWriter.Ramp, new RawSettings());
        using (image)
        {
            Assert.True(image.Width == 640 && image.Height == 480 && image.ColorType == SKColorType.RgbaF16);
            var left = Colour(image, 40, 100);
            var right = Colour(image, 580, 100);
            Assert.True(right.G > left.G + 40, $"a ramp from dark to light: {left} {right}");
            Assert.True(Math.Abs(left.R - left.B) < 6 && Math.Abs(right.R - right.B) < 6, "grey stays grey");
        }
        Assert.Equal(RawDevelopment.Info(DNGTestWriter.Ramp).Processes[^1], applied.Process);
        // Exposure.
        double middle = DevelopedColour(new RawSettings(), 300, 100).G;
        double up = DevelopedColour(new RawSettings { Exposure = 1 }, 300, 100).G;
        double down = DevelopedColour(new RawSettings { Exposure = -1 }, 300, 100).G;
        Assert.True(up > middle + 15 && down < middle - 15, $"middle grey: −1 stop {down}, as shot {middle}, +1 stop {up}");
        // White balance: a lower temperature setting cools the picture, a higher one warms it.
        var cool = DevelopedColour(new RawSettings { Temperature = 3_000 }, 300, 100);
        var warm = DevelopedColour(new RawSettings { Temperature = 12_000 }, 300, 100);
        Assert.True(cool.B - cool.R > 10 && warm.R - warm.B > 10, $"middle grey: 3000 K {cool}, 12000 K {warm}");
        // Tint: a positive one takes the light for greenish and turns the picture magenta; a negative one green.
        var magenta = DevelopedColour(new RawSettings { Tint = 60 }, 300, 100);
        var green = DevelopedColour(new RawSettings { Tint = -60 }, 300, 100);
        Assert.True(magenta.G < magenta.R && green.G > green.R, $"tint +60 {magenta}, −60 {green}");
    }

    [Fact]
    public void ShadowsAndContrastShapeTheTonesAndKeepBlackAndWhite()
    {
        double dark = DevelopedColour(new RawSettings(), 20, 100).G;
        double lifted = DevelopedColour(new RawSettings { Shadows = 1.8 }, 20, 100).G;
        double deepened = DevelopedColour(new RawSettings { Shadows = 0.2 }, 20, 100).G;
        Assert.True(lifted > dark + 5 && deepened < dark - 5, $"a dark grey: as shot {dark}, shadows 1.8 {lifted}, 0.2 {deepened}");
        double light = DevelopedColour(new RawSettings(), 600, 100).G;
        double moreContrast = DevelopedColour(new RawSettings { Contrast = 1 }, 600, 100).G;
        double darkMoreContrast = DevelopedColour(new RawSettings { Contrast = 1 }, 20, 100).G;
        Assert.True(moreContrast > light + 3 && darkMoreContrast < dark - 3, $"contrast 1: light {light} → {moreContrast}, dark {dark} → {darkMoreContrast}");
        // The curves keep their ends and never fold back.
        Assert.Equal(0, RawColor.Tone(0, 1, 0.5f));
        Assert.Equal(1, RawColor.Tone(1, -1, -0.5f), 5);
        float last = 0;
        for (int i = 1; i <= 1000; i++)
        {
            float v = RawColor.Tone(i / 1000f, 1, 0.5f);
            Assert.True(v >= last, $"{i}");
            last = v;
        }
    }

    [Fact]
    public void ClickingOnSomethingThatShouldBeGreyGivesItsWhiteBalance()
    {
        var data = DNGTestWriter.Ramp;
        var before = DevelopedColour(new RawSettings(), 500, 380, data);
        var balance = RawDevelopment.WhiteBalance(data, new RawSettings(), new Vec2(506, 386));
        var after = DevelopedColour(new RawSettings { Temperature = balance.Temperature, Tint = balance.Tint }, 500, 380, data);
        Assert.True(Math.Abs(after.R - after.B) < Math.Abs(before.R - before.B) / 2,
                    $"clicking on the warm patch: {(int)balance.Temperature} K tint {(int)balance.Tint}; the patch before {before}, after {after}");
        // Something already grey keeps the white balance it was shot with.
        var asShot = RawDevelopment.Info(data);
        var grey = RawDevelopment.WhiteBalance(data, new RawSettings(), new Vec2(300, 100));
        Assert.True(Math.Abs(grey.Temperature - asShot.Temperature) < 150 && Math.Abs(grey.Tint - asShot.Tint) < 3, $"{grey} against as shot {asShot.Temperature} {asShot.Tint}");
    }

    [Fact]
    public void TheTemperatureAndTintScalesAreTheOnesCamerasUse()
    {
        // The locus at 6504 K (CIE 1960 uv 0.2004, 0.3104), D65's correlated colour temperature; D65 itself (0.1978,
        // 0.3122) lies 0.0032 from it, towards green: a tint of about +10.
        var (u, v) = RawColor.Locus(6504);
        Assert.True(Math.Abs(u - 0.2004) < 0.0005 && Math.Abs(v - 0.3104) < 0.0005, $"{u} {v}");
        var (t, tint) = RawColor.TemperatureTint((0.19784, 0.31221));
        Assert.True(Math.Abs(t - 6504) < 100 && Math.Abs(tint - 9.6) < 1, $"{t} {tint}");
        // A white made from a temperature and tint gives them back.
        foreach (var (temperature, tintIn) in new[] { (2_500.0, 0.0), (5_000.0, 20.0), (9_000.0, -40.0), (30_000.0, 10.0) })
        {
            var back = RawColor.TemperatureTint(RawColor.White(temperature, tintIn));
            Assert.True(Math.Abs(back.Temperature - temperature) / temperature < 0.002 && Math.Abs(back.Tint - tintIn) < 0.1, $"{temperature} {tintIn}: {back}");
        }
        // As shot is no change at all.
        var same = RawColor.Balance(6500, 0, 6500, 0);
        for (int i = 0; i < 9; i++) Assert.Equal(i % 4 == 0 ? 1 : 0, same[i], 5);
    }

    /// <summary>A document remembers the process it was developed with: the same picture again, and words when this
    /// computer doesn't have it.</summary>
    [Fact]
    public void TheProcessIsKeptAndAMissingOneIsSaid()
    {
        var data = DNGTestWriter.Ramp;
        var (first, applied) = RawDevelopment.Develop(data, new RawSettings());
        Assert.NotNull(applied.Process);
        var saved = System.Text.Json.JsonSerializer.Deserialize<RawSettings>(System.Text.Json.JsonSerializer.Serialize(applied, SwiftJson.Compact), SwiftJson.Compact);
        Assert.Equal(applied, saved);
        var (again, sameSettings) = RawDevelopment.Develop(data, saved!);
        Assert.Equal(saved, sameSettings);
        var a = Colour(first, 300, 200);
        var b = Colour(again, 300, 200);
        Assert.Equal(a, b);
        first.Dispose(); again.Dispose();
        // A process this computer doesn't offer, such as the Mac's decoder version.
        var error = Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Develop(data, new RawSettings { Process = "9.dng" }));
        Assert.Contains("process 9.dng, which this computer doesn't offer", error.Message);
        Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.WhiteBalance(data, new RawSettings { Process = "1.gone" }, new Vec2(1, 1)));
    }

    [Fact]
    public void ADraftIsSmallerAndWhatIsNotARawFileIsRefusedWithWords()
    {
        using (var draft = RawDevelopment.Develop(DNGTestWriter.Ramp, new RawSettings(), scale: 0.25).Image)
            Assert.True(draft.Width <= 330 && draft.Width >= 150, $"{draft.Width}");
        string notRaw = RawDevelopment.NotRaw().Message;
        Assert.Equal(notRaw, Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(ReadOnlyMemory<byte>.Empty)).Message);
        Assert.Equal(notRaw, Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(new byte[10_000])).Message);
        Assert.Equal(notRaw, Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info("II*\0not a picture at all, just words that go on for a while longer than sixty-four bytes"u8.ToArray())).Message);
        // An ordinary picture the decoder could read is no RAW file either.
        Assert.Equal(notRaw, Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(HostileFiles.Tiff(HostileFiles.Picture(), 96, 64))).Message);
        // Cut short at many lengths: a refusal or a picture, never a crash.
        var good = DNGTestWriter.Ramp;
        int developed = 0, refused = 0;
        for (int length = 0; length < good.Length; length += good.Length / 37)
        {
            try
            {
                using var image = RawDevelopment.Develop(good.AsMemory(0, length), new RawSettings(), scale: 0.1).Image;
                developed += 1;
            }
            catch (RawDevelopment.Problem) { refused += 1; }
        }
        Assert.True(developed + refused >= 37, $"developed {developed} times, refused {refused}");
        // The cameras are the decoder's own.
        Assert.Equal(new[] { "Glimmer Test Camera" }, RawDevelopment.Cameras);
    }

    [Fact]
    public void WithoutASystemDecoderEveryFileIsRefusedWithWords()
    {
        RawDevelopment.Decoder = null;
        var previous = ImageImporter.PlatformDecoder;
        try
        {
            ImageImporter.PlatformDecoder = null;
            Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(DNGTestWriter.Ramp));
            Assert.Empty(RawDevelopment.Cameras);
            // A decoder that can't say what is RAW: the header decides.
            RawDevelopment.Decoder = new PlainDecoder(new TestDngDecoder());
            Assert.Equal(640, RawDevelopment.Info(DNGTestWriter.Ramp).Width);
            // The DNG says its own white balance (DngWhiteBalance); the decoder isn't asked.
            Assert.Equal(DngWhiteBalance.AsShot(DNGTestWriter.Ramp)!.Value.Temperature, RawDevelopment.Info(DNGTestWriter.Ramp).Temperature, 6);
            Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(HostileFiles.Tiff(HostileFiles.Picture(), 96, 64)));
        }
        finally
        {
            ImageImporter.PlatformDecoder = previous;
        }
    }

    /// <summary>A system decoder that is only an image decoder.</summary>
    private sealed class PlainDecoder(IPlatformImageDecoder inner) : IPlatformImageDecoder
    {
        public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data) => inner.ReadSize(data);
        public DecodedImage Decode(ReadOnlyMemory<byte> data) => inner.Decode(data);
    }

    /// <summary>A system decoder that, like WIC, reads ordinary pictures as well (PNG here, through SkiaSharp).</summary>
    private sealed class EverythingDecoder : IPlatformRawDecoder
    {
        private readonly TestDngDecoder dng = new();

        public bool IsRaw(ReadOnlyMemory<byte> data) => dng.IsRaw(data);
        public (double Temperature, double Tint)? AsShotWhiteBalance(ReadOnlyMemory<byte> data) => dng.AsShotWhiteBalance(data);
        public IReadOnlyList<string> Cameras => dng.Cameras;

        public (int Width, int Height)? ReadSize(ReadOnlyMemory<byte> data)
        {
            if (dng.ReadSize(data) is { } size) return size;
            using var codec = SKCodec.Create(SKData.CreateCopy(data.Span));
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }

        public DecodedImage Decode(ReadOnlyMemory<byte> data)
        {
            if (dng.ReadSize(data) is not null) return dng.Decode(data);
            using var bitmap = SKBitmap.Decode(data.ToArray()) ?? throw new InvalidDataException("unreadable");
            var (px, w, h) = ExportedFiles.Read(bitmap);
            return DecodedImage.FromRgba8(w, h, px);
        }
    }

    [Fact]
    public void AnOrdinaryPictureTheDecoderCanReadIsNoRawFile()
    {
        var png = HostileFiles.Encoded(HostileFiles.Picture(), 96, 64, SKEncodedImageFormat.Png);
        var decoder = new EverythingDecoder();
        Assert.Equal((96, 64), decoder.ReadSize(png));
        RawDevelopment.Decoder = decoder;
        Assert.Equal(RawDevelopment.NotRaw().Message, Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(png)).Message);
        Assert.Throws<RawDevelopment.Problem>(() => RawLayer.Make(png, "photo.png"));
        Assert.Equal(640, RawDevelopment.Info(DNGTestWriter.Ramp).Width);
        // A decoder that can't say: the header decides, and a PNG's is no camera's.
        RawDevelopment.Decoder = new PlainDecoder(decoder);
        Assert.Throws<RawDevelopment.Problem>(() => RawDevelopment.Info(png));
        Assert.Equal(640, RawDevelopment.Info(DNGTestWriter.Ramp).Width);
    }

    /// <summary>A one-pixel TIFF directory with the given photometric interpretation, a DNG version tag or not, and its
    /// raw picture in a sub-directory when <paramref name="subPhotometric"/> is given.</summary>
    private static byte[] TinyTiff(ushort photometric, bool dng = false, ushort? subPhotometric = null)
    {
        static byte[] Entry(ushort tag, ushort type, uint count, uint value) =>
            [(byte)tag, (byte)(tag >> 8), (byte)type, (byte)(type >> 8), .. BitConverter.GetBytes(count), .. BitConverter.GetBytes(value)];
        static byte[] Directory(List<byte[]> entries) =>
            [(byte)entries.Count, 0, .. entries.SelectMany(e => e), 0, 0, 0, 0];
        var main = new List<byte[]> { Entry(256, 4, 1, 1), Entry(257, 4, 1, 1), Entry(262, 3, 1, photometric) };
        if (dng) main.Add(Entry(50706, 1, 4, 0x00000401));
        if (subPhotometric is not null) main.Add(Entry(330, 4, 1, (uint)(8 + 2 + (main.Count + 1) * 12 + 4)));
        main.Sort((a, b) => BitConverter.ToUInt16(a).CompareTo(BitConverter.ToUInt16(b)));
        byte[] file = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, .. Directory(main)];
        if (subPhotometric is { } sub) file = [.. file, .. Directory([Entry(256, 4, 1, 1), Entry(257, 4, 1, 1), Entry(262, 3, 1, sub)])];
        return [.. file, .. new byte[16]];
    }

    [Fact]
    public void TheHeaderSaysWhichFilesAreCameraRaw()
    {
        // New: the formats RawContainer recognises, for a system decoder that can't say.
        Assert.True(RawContainer.Looks(DNGTestWriter.Ramp));
        Assert.True(RawContainer.Looks(TinyTiff(2, dng: true)));                      // a DNG version tag alone
        Assert.True(RawContainer.Looks(TinyTiff(32803)));                             // a colour filter array
        Assert.True(RawContainer.Looks(TinyTiff(34892)));                             // linear raw
        Assert.True(RawContainer.Looks(TinyTiff(6, subPhotometric: 32803)));          // the raw picture in a sub-directory (NEF, ARW …)
        Assert.False(RawContainer.Looks(TinyTiff(2)));                                // an ordinary RGB TIFF
        Assert.False(RawContainer.Looks(TinyTiff(6, subPhotometric: 2)));
        Assert.False(RawContainer.Looks(HostileFiles.Tiff(HostileFiles.Picture(), 96, 64)));
        byte[] tail = new byte[32];
        Assert.True(RawContainer.Looks([(byte)'I', (byte)'I', 42, 0, 16, 0, 0, 0, (byte)'C', (byte)'R', 2, 0, .. tail]));   // Canon CR2
        Assert.True(RawContainer.Looks([.. "IIRO"u8.ToArray(), .. tail]));                                                // Olympus ORF
        Assert.True(RawContainer.Looks([(byte)'I', (byte)'I', (byte)'U', 0, .. tail]));                                // Panasonic RW2
        Assert.True(RawContainer.Looks([.. "FUJIFILMCCD-RAW 0201"u8.ToArray(), .. tail]));                                // Fujifilm RAF
        Assert.True(RawContainer.Looks([0, 0, 0, 24, .. "ftypcrx "u8.ToArray(), .. tail]));                              // Canon CR3
        Assert.False(RawContainer.Looks([0, 0, 0, 24, .. "ftypheic"u8.ToArray(), .. tail]));
        Assert.False(RawContainer.Looks(HostileFiles.Encoded(HostileFiles.Picture(), 96, 64, SKEncodedImageFormat.Png)));
        Assert.False(RawContainer.Looks(HostileFiles.Encoded(HostileFiles.Picture(), 96, 64, SKEncodedImageFormat.Jpeg)));
        // A directory that points at itself: no answer, never a hang.
        var loop = TinyTiff(2);
        loop[8 + 2 + 3 * 12] = 8;   // the first directory's next-directory offset, back to itself
        Assert.False(RawContainer.Looks(loop));
    }
}

/// <summary>A layer that keeps its RAW file: made, saved, read back and developed again.</summary>
[Collection("Raw")]
public class RawLayerTests
{
    public RawLayerTests() => RawDevelopment.Decoder = new TestDngDecoder();

    private static (double R, double G, double B) FlatColour(DocumentState state, int x, int y)
    {
        using var image = ImageExporter.FlattenedSRGB(state);
        return RawDevelopmentTests.Colour(image, x, y);
    }

    [Fact]
    public void ARawLayerKeepsItsFileThroughSavingAndCanBeDevelopedAgain()
    {
        var data = DNGTestWriter.Ramp;
        var (layer, width, height) = RawLayer.Make(data, "ramp.dng");
        Assert.True(width == 640 && height == 480 && layer.Name == "ramp" && layer.Raw?.Data == data && layer.Raw?.Settings.Process is not null);
        Assert.True(RawLayer.PixelsAreAsDeveloped(layer));
        var state = new DocumentState(width, height, layers: [layer]);
        var before = FlatColour(state, 300, 100);
        // Saved and read back: the file's bytes, its name and its settings are there, and the picture is the same.
        var read = GlimmerPackage.State(GlimmerPackage.Contents(state));
        var back = read.Layers.First();
        Assert.True(back.Raw?.Data.SequenceEqual(data) == true && back.Raw?.FileName == "ramp.dng" && back.Raw?.Settings == layer.Raw?.Settings
                    && RawLayer.PixelsAreAsDeveloped(back));
        var after = FlatColour(read, 300, 100);
        Assert.Equal(before, after);
        // Developed again, brighter: new pixels, the same file, and the layer's own settings stay.
        var masked = back with { Opacity = 0.5, BlendMode = BlendMode.Multiply, Mask = new LayerMask(defaultValue: 0.5f) };
        var brighter = masked.Raw!.Settings with { Exposure = 1 };
        var again = RawLayer.Developed(masked, brighter);
        Assert.True(again.Raw?.Data.SequenceEqual(data) == true && again.Raw?.Settings.Exposure == 1 && again.Raw?.Settings.Process == layer.Raw?.Settings.Process);
        Assert.True(again.Opacity == 0.5 && again.BlendMode == BlendMode.Multiply && again.Mask?.DefaultValue == 0.5f && again.Id == masked.Id);
        var plain = again with { Opacity = 1, BlendMode = BlendMode.Normal, Mask = null };
        var bright = FlatColour(read with { Layers = [plain] }, 300, 100);
        Assert.True(bright.G > after.G + 15, $"{bright} against {after}");
        var timed = RawLayer.DevelopedTimed(masked, brighter);
        Assert.True(timed.DecoderSeconds >= 0 && timed.TileSeconds >= 0);
    }

    [Fact]
    public void PixelsChangedAfterDevelopingAreNoticedAlsoAfterSaving()
    {
        var (layer, width, height) = RawLayer.Make(DNGTestWriter.Ramp, "ramp.dng");
        var painted = layer with { Content = new RasterContent(TileGrid.Solid(new System.Numerics.Vector4(1, 0, 0, 1), new PixelRect(0, 0, 256, 256))) };
        Assert.False(RawLayer.PixelsAreAsDeveloped(painted));
        var state = new DocumentState(width, height, layers: [painted]);
        var back = GlimmerPackage.State(GlimmerPackage.Contents(state)).Layers.First();
        Assert.True(back.Raw is not null && !RawLayer.PixelsAreAsDeveloped(back));
        // A layer without a RAW file can't be developed, and says so.
        Assert.Throws<RawDevelopment.Problem>(() => RawLayer.Developed(new Layer("Plain", new RasterContent(TileGrid.Empty)), new RawSettings()));
    }

    [Fact]
    public void APackageWithoutItsRawFileStillOpensWithThePixels()
    {
        var (layer, width, height) = RawLayer.Make(DNGTestWriter.Ramp, "ramp.dng");
        var state = new DocumentState(width, height, layers: [layer]);
        var contents = GlimmerPackage.Contents(state);
        // The RAW file goes missing from the package, or is cut short: the layer opens as pixels, without it.
        string name = $"{GlimmerPackage.LayersDirectory}/{SwiftJson.UuidString(layer.Id)}.raw";
        Assert.True(contents.Files.ContainsKey(name), name);
        var missing = new PackageContents(contents.Files.Remove(name), contents.Directories);
        var without = GlimmerPackage.State(missing).Layers.First();
        Assert.True(without.Raw is null && without.Content is RasterContent { Tiles.IsEmpty: false });
        var cut = new PackageContents(contents.Files.SetItem(name, new byte[100]), contents.Directories);
        Assert.Null(GlimmerPackage.State(cut).Layers.First().Raw);
    }

    [Fact]
    public void AFileTooLargeToKeepIsRefusedWithItsSize()
    {
        Assert.Equal("600 MB", RawLayer.FileSize(600_000_000));
        Assert.Equal("1.2 GB", RawLayer.FileSize(1_200_000_000));
        Assert.Equal("12 bytes", RawLayer.FileSize(12));
    }
}
#endif
