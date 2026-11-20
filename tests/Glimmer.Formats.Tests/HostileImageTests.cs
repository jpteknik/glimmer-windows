// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/HostileImageTests.swift (in spirit where the instrument
// differs): the files are made by SkiaSharp and Glimmer's own writers instead of ImageIO, TIFF is read by the tests'
// stand-in for the system decoder (TestTiffDecoder), HEIC is not covered (only the system decoder reads it on
// Windows), and memory is the process's working set where the Mac reads its physical footprint.
using SkiaSharp;

namespace Glimmer.Formats.Tests;

/// <summary>Tests that measure the process (time, memory) and must not share it with other tests.</summary>
[CollectionDefinition("Alone", DisableParallelization = true)]
public sealed class AloneCollection;

/// <summary>Image files are untrusted input (the web, sibling apps, agents). These generate broken, lying and oversized
/// files in every format Glimmer opens and check that opening them never crashes, hangs or decodes without bound: each
/// either opens or is refused. The generators also write fixtures for the live API check
/// (<c>GLIMMER_FIXTURE_DIR=&lt;dir&gt; dotnet test --filter HostileImageTests</c>).</summary>
[Collection("Alone")]
public class HostileImageTests
{
    private static readonly TestTiffDecoder SystemDecoder = new();

    private static double Megabytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return Environment.WorkingSet / 1_048_576.0;
    }

    /// <summary>Opens <paramref name="data"/> the way every path into Glimmer does. Returns what happened.</summary>
    private static string Open(byte[] data)
    {
        try
        {
            var image = ImageImporter.Image(data, SystemDecoder);
            if (!(image.Width >= 1 && image.Height >= 1 && (long)image.Width * image.Height <= ImageImporter.MaxPixels))
                return $"opened with a bad size {image.Width}×{image.Height}";
            _ = ImageImporter.Tiles(image);
            return $"opened {image.Width}×{image.Height}";
        }
        catch (ImageImportError e) { return $"refused: {e.Message}"; }
        catch (Exception e) { return $"other error: {e}"; }
    }

    [Fact]
    public void TheValidFilesOpen()
    {
        var files = HostileFiles.Valid();
        Assert.Equal(new[] { "jpeg", "png", "psd", "tiff", "webp" }, files.Select(f => f.Name).Order());   // every format has a generator
        foreach (var (name, data) in files) Assert.True(Open(data) == "opened 96×64", $"{name}: {Open(data)}");
    }

    [Fact]
    public void BrokenFilesAreRefusedOrOpenedButNeverCrashHangOrBalloon()
    {
        var files = HostileFiles.Hostile();
        Assert.True(files.Count > 75, $"{files.Count}");   // the Mac's 80 has one more format (HEIC)
        double before = Megabytes();
        var slow = new List<string>();
        var odd = new List<string>();
        foreach (var (name, data) in files)
        {
            var start = global::System.Diagnostics.Stopwatch.StartNew();
            string result = Open(data);
            double seconds = start.Elapsed.TotalSeconds;
            if (seconds > 5) slow.Add($"{name} {seconds}");
            if (!(result.StartsWith("refused: The image", StringComparison.Ordinal) || result.StartsWith("opened ", StringComparison.Ordinal)) || result.Contains("bad size"))
                odd.Add($"{name}: {result}");
            // A file that is cut short never opens as half a picture.
            if (name.Contains("truncated") || name.Contains("header-only") || name.Contains("then-garbage"))
            {
                if (!result.StartsWith("refused", StringComparison.Ordinal)) odd.Add($"{name} opened: {result}");
            }
        }
        Assert.True(slow.Count == 0, string.Join("\n", slow));
        Assert.True(odd.Count == 0, string.Join("\n", odd));
        Assert.True(Megabytes() - before < 600, $"memory stays bounded over {files.Count} hostile files");
    }

    [Fact]
    public void WholeFilesWithSomethingAppendedStillOpen()
    {
        // Phones append a video after a JPEG (motion photos), and tools append data after a PNG.
        var trailer = Enumerable.Repeat((byte)0x5A, 300_000).ToArray();
        foreach (var (name, data) in HostileFiles.Valid().Where(f => f.Name is "png" or "jpeg"))
            Assert.True(Open([.. data, .. trailer]) == "opened 96×64", $"{name} with a trailer");
        var big = HostileFiles.Encoded(HostileFiles.Picture(1200, 800), 1200, 800, SKEncodedImageFormat.Jpeg);
        Assert.Equal("opened 1200×800", Open(big));
        Assert.StartsWith("refused", Open(big[..^2]));   // without its end marker it is cut short
    }

    [Fact]
    public void AFileThatClaimsAHugePictureIsRefusedOnItsHeader()
    {
        foreach (var (name, data) in HostileFiles.Hostile().Where(f => f.Name.Contains("claims")))
        {
            var start = global::System.Diagnostics.Stopwatch.StartNew();
            string result = Open(data);
            Assert.True(result.StartsWith("refused", StringComparison.Ordinal), $"{name}: {result}");
            Assert.True(start.Elapsed.TotalSeconds < 1, $"{name} is refused without decoding");
        }
        string message = ImageImportError.TooLarge(100_000, 100_000).Message;
        Assert.True(message.Contains("100000 × 100000") && message.Contains("256 megapixels"), message);
    }

    [Fact]
    public void ADecompressionBombIsRefusedWithoutBeingDecoded()
    {
        // 30 000 × 30 000 pixels in a file of about 100 kB; as RGBA it would take 3.6 GB.
        var bomb = HostileFiles.PngBomb(30_000);
        Assert.True(bomb.Length < 2_000_000, $"{bomb.Length} bytes");
        double before = Megabytes();
        var start = global::System.Diagnostics.Stopwatch.StartNew();
        string result = Open(bomb);
        Assert.StartsWith("refused: The image is too large (30000 × 30000", result);
        Assert.True(start.Elapsed.TotalSeconds < 2);
        Assert.True(Megabytes() - before < 200);
    }

    [Fact]
    public void ALargeButAllowedPictureStillOpens()
    {
        // 12 000 × 8 000 (96 megapixels) is under the limit and must still open.
        Assert.True(12_000L * 8_000 <= ImageImporter.MaxPixels);
        var image = ImageImporter.Image(HostileFiles.PngClaimingFlat(12_000, 8_000), SystemDecoder);
        Assert.True(image.Width == 12_000 && image.Height == 8_000);
    }

    /// <summary>Writes the fixtures for the live API check when asked to.</summary>
    [Fact]
    public void WritesFixturesWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_FIXTURE_DIR") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var (name, data) in HostileFiles.Hostile().Append(("png-bomb-30000", HostileFiles.PngBomb(30_000))))
            File.WriteAllBytes(Path.Combine(folder, name + ".bin"), data);
        foreach (var (name, data) in HostileFiles.Valid()) File.WriteAllBytes(Path.Combine(folder, "valid-" + name + ".bin"), data);
    }
}
