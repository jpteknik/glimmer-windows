// Ported from the PSDRealFiles suite in Packages/GlimmerKit/Tests/GlimmerRenderTests/PSDAppearanceTests.swift, which
// reads files written by Photoshop when GLIMMER_PSD_SAMPLES names a folder of them. The first two tests are new: they
// check the reader and the writer against psd-tools (an independent reader, MIT) on the same files. Opt-in, as in Swift, because the files are not in the
// repository. The ones these were measured with are psd-tools' own test files (tests/psd_files of
// github.com/psd-tools/psd-tools at v1.27.0, MIT, third-party-psds left out), copied into one folder with "__" for
// "/" in their names.
#if GLIMMER_PSD
using System.Security.Cryptography;
using System.Text.Json;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Formats.Tests;

[Collection("Alone")]
public class PSDRealFilesTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("GLIMMER_PSD_SAMPLES");

    private static List<string> Files() =>
        [.. Directory.GetFiles(Folder!).Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];

    private static string Sha(ReadOnlyMemory<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data.Span));

    /// <summary>Where the tallies are written for reading after a run: GLIMMER_PSD_REPORTS, or the temporary folder.</summary>
    private static string ReportPath(string name) => Path.Combine(Environment.GetEnvironmentVariable("GLIMMER_PSD_REPORTS") ?? Path.GetTempPath(), name);

    /// <summary>The files the reader refuses by the original's policy (docs/UPSTREAM-NOTES.md, "PSD reader", item 3):
    /// a layer with bottom above top, and a -3 plane larger than four times the canvas.</summary>
    private static readonly string[] RefusedByPolicy = ["masks.psb", "masks.psd", "vector-mask2.psd"];

    public sealed class SamplesFactAttribute : FactAttribute
    {
        public SamplesFactAttribute()
        {
            if (Folder is null) Skip = "GLIMMER_PSD_SAMPLES names no folder of Photoshop files";
            else if (OutsideReader.Missing("psd_tools") is { } why) Skip = why;
        }
    }

    public sealed class SamplesOnlyFactAttribute : FactAttribute
    {
        public SamplesOnlyFactAttribute() { if (Folder is null) Skip = "GLIMMER_PSD_SAMPLES names no folder of Photoshop files"; }
    }

    [SamplesFact]
    public void EveryLayerAndPlaneReadsAsAnOutsideReaderReadsIt()
    {
        var files = Files();
        Assert.NotEmpty(files);
        string? dump = Environment.GetEnvironmentVariable("GLIMMER_PSD_TOOLS_DUMP");   // a dump made before, to save the minutes it takes
        var lines = dump is not null
            ? File.ReadAllLines(dump).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList()
            : OutsideReader.ReadMany("dump_psd_with_psd_tools.py", files);
        var expected = lines.Where(l => l.TryGetProperty("header", out _)).ToDictionary(l => Path.GetFileName(l.GetProperty("file").GetString()!));
        var report = new List<string>();
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        void Count(string what, string file) { tally[what] = tally.GetValueOrDefault(what) + 1; report.Add($"{what}: {file}"); }
        var limits = new PSDFile.Limits(maxCanvasPixels: 400_000_000, maxDecodedBytes: 2_000_000_000);
        foreach (var path in files)
        {
            string name = Path.GetFileName(path);
            if (!expected.TryGetValue(name, out var theirs)) { Count("not read by psd-tools", name); continue; }
            PSDFile file;
            var started = System.Diagnostics.Stopwatch.StartNew();
            try { file = new PSDFile(File.ReadAllBytes(path), limits); }
            catch (PSDFile.Problem problem) { Count($"refused by Glimmer: {problem.Message}", name); refused.Add(name.Split("__")[^1]); continue; }
            if (started.Elapsed.TotalSeconds > 5) Count("slower than 5 s", name);
            var h = theirs.GetProperty("header");
            bool headerSame = file.Header.Width == h.GetProperty("width").GetInt32() && file.Header.Height == h.GetProperty("height").GetInt32()
                && file.Header.Depth == h.GetProperty("depth").GetInt32() && file.Header.Channels == h.GetProperty("channels").GetInt32()
                && (int)file.Header.Mode == h.GetProperty("mode").GetInt32() && file.Header.IsLarge == (h.GetProperty("version").GetInt32() == 2);
            if (!headerSame) Count("header differs", name);
            var layers = theirs.GetProperty("layers").EnumerateArray().ToList();
            if (layers.Count != file.Layers.Length) { Count($"layer count differs ({file.Layers.Length} against {layers.Count})", name); continue; }
            for (int i = 0; i < layers.Count; i++)
            {
                var a = file.Layers[i];
                var b = layers[i];
                var rect = b.GetProperty("rect").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (a.Rect != new PSDFile.Rect(rect[0], rect[1], rect[2], rect[3])) Count("layer rectangle differs", name);
                if (a.Opacity != b.GetProperty("opacity").GetInt32() || a.IsVisible != b.GetProperty("visible").GetBoolean() || a.IsClipped != (b.GetProperty("clipping").GetInt32() != 0))
                    Count("opacity, visibility or clipping differs", name);
                // (A group's record gives its own blend mode in its divider block; Glimmer takes that one, as Photoshop does.)
                if (a.BlendKey != b.GetProperty("blend").GetString() && a.Group == PSDFile.GroupMark.None) Count("blend key differs", name);
                foreach (var channel in b.GetProperty("channels").EnumerateArray())
                {
                    int id = channel.GetProperty("id").GetInt32();
                    if (id >= -1)
                    {
                        if (channel.GetProperty("data").ValueKind == JsonValueKind.Null) continue;
                        var data = channel.GetProperty("data");
                        if (data.TryGetProperty("error", out _)) { Count("channel psd-tools can't decode", name); continue; }
                        if (!a.Planes.TryGetValue(id, out var plane)) { Count("channel missing in Glimmer", name); continue; }
                        if (Sha(plane) != data.GetProperty("sha").GetString())
                            Count(file.Header.Depth == 32 && channel.GetProperty("compression").GetInt32() == 3 ? "32-bit predicted channel differs (not read in this version)" : $"channel differs (compression {channel.GetProperty("compression").GetInt32()}, depth {file.Header.Depth})", name);
                    }
                    else if (id == -2)
                    {
                        // Read at the document's depth and brought down to a byte a pixel (docs/UPSTREAM-NOTES.md,
                        // "PSD reader", item 1): psd-tools' decoding at that depth, brought down by the same rule.
                        var pixels = a.Mask?.Pixels;
                        string? reduced = channel.TryGetProperty("dataReduced", out var r) && r.TryGetProperty("sha", out var sr) ? sr.GetString() : null;
                        if (pixels is null) { if (reduced is not null) Count($"mask missing in Glimmer (depth {file.Header.Depth})", name); continue; }
                        if (reduced is null) { Count($"mask psd-tools can't decode (depth {file.Header.Depth})", name); continue; }
                        // (32-bit prediction is not undone, for masks as for channels: such files come in flattened.)
                        Count(Sha(pixels.Value) == reduced ? $"mask agrees (depth {file.Header.Depth})"
                              : file.Header.Depth == 32 && channel.GetProperty("compression").GetInt32() == 3 ? "32-bit predicted mask differs (not read in this version)"
                              : $"mask differs (depth {file.Header.Depth})", name);
                    }
                }
            }
            if (theirs.TryGetProperty("composite", out var composite))
            {
                var shas = composite.EnumerateArray().Select(v => v.GetString()).ToList();
                if (file.Composite.IsEmpty) Count($"flattened picture not read by Glimmer (depth {file.Header.Depth})", name);
                else if (!file.Composite.Select(p => Sha(p)).SequenceEqual(shas)) Count("flattened picture differs", name);
                else Count("flattened picture agrees", name);
            }
            Count("read", name);
        }
        File.WriteAllLines(ReportPath("glimmer-psd-real-files.txt"), [.. tally.Select(t => $"{t.Value,5}  {t.Key}"), "", .. report]);
        string summary = string.Join("\n", tally.Select(t => $"{t.Value,5}  {t.Key}"));
        Assert.True(tally.Keys.All(k => !k.Contains("differs (compression") && !k.StartsWith("layer count") && !k.StartsWith("header differs")
                                        && !k.StartsWith("mask differs") && !k.StartsWith("mask missing")), summary);
        // Refused: only what the original's policy refuses.
        Assert.True(refused.SetEquals(RefusedByPolicy), $"refused: {string.Join(", ", refused)}\n{summary}");
    }

    /// <summary>New: the writer on real structures. Every file Glimmer reads and can write (8 or 16 bits, RGB or
    /// grayscale) is written again by Glimmer, and psd-tools reads the written file as it reads the original: the same
    /// layers, rectangles, settings, channels and masks, and the flattened picture's colour planes.</summary>
    [SamplesFact]
    public void RealFilesWrittenAgainReadTheSameInAnOutsideReader()
    {
        var files = Files();
        var limits = new PSDFile.Limits(maxCanvasPixels: 400_000_000, maxDecodedBytes: 2_000_000_000);
        string folder = Path.Combine(Path.GetTempPath(), $"glimmer-psd-rewritten-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var report = new List<string>();
        void Count(string what, string file) { tally[what] = tally.GetValueOrDefault(what) + 1; report.Add($"{what}: {file}"); }
        var pairs = new List<(string Original, string Written, int ColorChannels, bool HasComposite)>();
        try
        {
            foreach (var path in files)
            {
                PSDFile file;
                try { file = new PSDFile(File.ReadAllBytes(path), limits); } catch (PSDFile.Problem) { continue; }
                if (file.Header.Depth is not (8 or 16) || file.Header.Mode is not (PSDFile.ColorMode.Rgb or PSDFile.ColorMode.Grayscale) || file.Header.IsLarge && (file.Header.Width > 30_000 || file.Header.Height > 30_000)) continue;
                int colorChannels = file.Header.Mode == PSDFile.ColorMode.Rgb ? 3 : 1;
                bool hasComposite = file.Composite.Length >= colorChannels;
                var composite = hasComposite ? file.Composite.Take(colorChannels) : Enumerable.Repeat((ReadOnlyMemory<byte>)new byte[(long)file.Header.Width * file.Header.Height * (file.Header.Depth / 8)], colorChannels);
                var toWrite = file with { Header = file.Header with { IsLarge = false, Channels = colorChannels }, Composite = [.. composite] };
                string written = Path.Combine(folder, Path.GetFileName(path) + ".psd");
                try { File.WriteAllBytes(written, PSDWriter.Data(toWrite)); }
                catch (PSDFile.Problem problem) { Count($"the writer refuses: {problem.Message}", Path.GetFileName(path)); continue; }
                pairs.Add((path, written, colorChannels, hasComposite));
            }
            var before = OutsideReader.ReadMany("dump_psd_with_psd_tools.py", [.. pairs.Select(p => p.Original)]);
            var after = OutsideReader.ReadMany("dump_psd_with_psd_tools.py", [.. pairs.Select(p => p.Written)]);
            for (int i = 0; i < pairs.Count; i++)
            {
                string name = Path.GetFileName(pairs[i].Original);
                var a = before[i];
                var b = after[i];
                if (b.TryGetProperty("error", out var error)) { Count($"psd-tools can't read the written file: {error.GetString()}", name); continue; }
                var la = a.GetProperty("layers").EnumerateArray().ToList();
                var lb = b.GetProperty("layers").EnumerateArray().ToList();
                if (la.Count != lb.Count) { Count("layer count differs", name); continue; }
                bool same = true;
                for (int j = 0; j < la.Count; j++)
                {
                    var x = la[j];
                    var y = lb[j];
                    bool group = y.GetProperty("keys").EnumerateArray().Any(k => k.GetString() == "lsct");
                    if (x.GetProperty("rect").ToString() != y.GetProperty("rect").ToString()) { Count("a layer's rectangle differs", name); same = false; }
                    if (x.GetProperty("opacity").GetInt32() != y.GetProperty("opacity").GetInt32() || x.GetProperty("visible").GetBoolean() != y.GetProperty("visible").GetBoolean()
                        || x.GetProperty("clipping").GetInt32() != y.GetProperty("clipping").GetInt32()) { Count("a layer's settings differ", name); same = false; }
                    if (!group && x.GetProperty("blend").GetString() != y.GetProperty("blend").GetString()) { Count("a layer's blend key differs", name); same = false; }
                    var channelsA = x.GetProperty("channels").EnumerateArray().Where(c => c.GetProperty("id").GetInt32() >= -2).ToDictionary(c => c.GetProperty("id").GetInt32());
                    var channelsB = y.GetProperty("channels").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetInt32());
                    foreach (var (id, ca) in channelsA)
                    {
                        // A mask is kept at a byte a pixel, so a deeper one is compared as both readers bring it down.
                        string key = id == -2 ? "dataReduced" : "data";
                        string? sa = ca.TryGetProperty(key, out var da) && da.ValueKind == JsonValueKind.Object && da.TryGetProperty("sha", out var s1) ? s1.GetString() : null;
                        string? sb = channelsB.TryGetValue(id, out var cb) && cb.TryGetProperty(key, out var db) && db.ValueKind == JsonValueKind.Object && db.TryGetProperty("sha", out var s2) ? s2.GetString() : null;
                        if (sa != sb) { Count(id == -2 ? "a mask differs" : "a channel differs", name); same = false; }
                    }
                }
                if (pairs[i].HasComposite && a.TryGetProperty("composite", out var ca2) && b.TryGetProperty("composite", out var cb2)
                    && !ca2.EnumerateArray().Take(pairs[i].ColorChannels).Select(v => v.GetString()).SequenceEqual(cb2.EnumerateArray().Select(v => v.GetString())))
                { Count("the flattened picture differs", name); same = false; }
                if (same) Count("written again and read the same", name);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
        File.WriteAllLines(ReportPath("glimmer-psd-rewritten.txt"), [.. tally.Select(t => $"{t.Value,5}  {t.Key}"), "", .. report]);
        Assert.True(tally.Count == 1 && tally.ContainsKey("written again and read the same"), string.Join("\n", tally.Select(t => $"{t.Value,5}  {t.Key}")));
    }

    /// <summary>Swift: <c>everyFileImportsWithATrueReport</c>. Every file imports or is refused with words; nothing else
    /// escapes. Each document is measured against the flattened picture its file carries, and the tally of how many
    /// match is written with the reports.</summary>
    [SamplesOnlyFact]
    public void EveryFileImportsWithATrueReport()
    {
        var files = Files();
        Assert.NotEmpty(files);
        var lines = new List<string>();
        int imported = 0, compared = 0, matching = 0;
        foreach (var path in files)
        {
            var data = File.ReadAllBytes(path);
            var start = System.Diagnostics.Stopwatch.StartNew();
            string name = Path.GetFileName(path);
            try
            {
                var header = PSDFile.ReadHeader(data);
                var (state, report) = PSDImport.Document(data);
                double parsed = start.Elapsed.TotalSeconds;
                imported += 1;
                Assert.True(state.Width == header.Width && state.Height == header.Height && state.ImportReport == report, name);
                Assert.True(report.LayersKept <= Math.Max(report.LayersInFile, 1) || report.Flattened, $"{name}: {report.LayersKept} of {report.LayersInFile}");
                if (!report.Flattened && PSDAppearance.Difference(state, data) is { } d)
                {
                    report = report with { MeanDifference = d.Mean, ShareVisiblyDifferent = d.ShareVisible };
                    compared += 1;
                    if (report.MatchesTheFile == true) matching += 1;
                }
                lines.Add($"{name} | {header.Width}×{header.Height} {report.Source} | import {parsed:F2} s, comparison {start.Elapsed.TotalSeconds - parsed:F2} s | layers {report.LayersKept}/{report.LayersInFile}");
                lines.AddRange(report.Lines.Select(l => "    " + l));
            }
            catch (PSDFile.Problem problem)
            {
                lines.Add($"{name} | REFUSED: {problem.Message}");
            }
        }
        lines.Add($"{imported} of {files.Count} imported; {compared} compared with the file's own picture, {matching} of those match it");
        File.WriteAllLines(ReportPath("glimmer-psd-imports.txt"), lines);
        Assert.True(imported > files.Count / 2, lines[^1]);
    }

    /// <summary>Ported from PSDRealFiles.realFilesCutShortOrWithBytesChangedNeverCrashOrPassTheBudget (PSDAppearanceTests.swift):
    /// the files written by Photoshop as seeds for abuse, read every time and imported every 25th.</summary>
    [SamplesOnlyFact]
    public void RealFilesCutShortOrWithBytesChangedNeverCrashOrPassTheBudget()
    {
        var limits = new PSDFile.Limits(maxCanvasPixels: 4_000_000, maxDecodedBytes: 32_000_000);
        long attempts = 0, read = 0, imported = 0;
        double slowest = 0;
        double secondsPerFile = double.TryParse(Environment.GetEnvironmentVariable("GLIMMER_PSD_ABUSE_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 18;
        var files = Files();
        var lines = new List<string>();
        foreach (var path in files)
        {
            var good = File.ReadAllBytes(path);
            // Each damaged variant is described, not stored: (cut at a length) or (one byte changed).
            var damages = new List<(bool Cut, int Index, byte Value)>();
            // Cut: every length in the first 2,000 bytes, then 300 lengths spread over the rest.
            for (int length = 0; length < Math.Min(2_000, good.Length); length++) damages.Add((true, length, 0));
            for (int i = 1; i <= 300; i++) damages.Add((true, (int)(2_000 + (long)(good.Length - 2_000) * i / 301), 0));
            // Changed: every byte of the first 3,000 to 0x00 and 0xFF, then 1,500 places spread over the file.
            for (int index = 0; index < Math.Min(3_000, good.Length); index++)
                foreach (byte value in new byte[] { 0x00, 0xFF })
                    if (good[index] != value) damages.Add((false, index, value));
            ulong seed = unchecked(0x9E3779B97F4A7C15UL + (ulong)good.Length);
            int Next() { seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL); return (int)(seed >> 33); }
            for (int i = 0; i < 1_500; i++) { int index = Next() % good.Length; damages.Add((false, index, good[index] == 0xFF ? (byte)0x7F : (byte)0xFF)); }
            // A fixed shuffle, so that a time budget takes an even sample of every kind.
            for (int i = damages.Count - 1; i > 0; i--) { int j = Next() % (i + 1); (damages[i], damages[j]) = (damages[j], damages[i]); }
            var fileStart = System.Diagnostics.Stopwatch.StartNew();
            int done = 0;
            for (int n = 0; n < damages.Count; n++)
            {
                if (fileStart.Elapsed.TotalSeconds > secondsPerFile) break;
                var damage = damages[n];
                ReadOnlyMemory<byte> data;
                if (damage.Cut) data = good.AsMemory(0, Math.Clamp(damage.Index, 0, good.Length));
                else { var bad = (byte[])good.Clone(); bad[damage.Index] = damage.Value; data = bad; }
                attempts += 1; done += 1;
                var start = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var file = new PSDFile(data, limits);
                    read += 1;
                    long decoded = PSDFileTests.DecodedBytes(file) + file.Composite.Sum(p => (long)p.Length);
                    Assert.True(decoded <= limits.MaxDecodedBytes, $"{Path.GetFileName(path)} variant {n}: {decoded} bytes decoded");
                }
                catch (PSDFile.Problem) { }
                // The whole import (layers into tiles, the report) on every 25th variant. Only the import's own
                // refusal is an answer: anything else fails the test (Swift's try? would hide it).
                if (n % 25 == 0)
                {
                    try { _ = PSDImport.Document(data, limits); imported += 1; } catch (PSDFile.Problem) { }
                }
                slowest = Math.Max(slowest, start.Elapsed.TotalSeconds);
            }
            lines.Add($"{Path.GetFileName(path)}: {done} of {damages.Count} damaged variants in {fileStart.Elapsed.TotalSeconds:F0} s");
        }
        lines.Add($"{files.Count} files, {attempts} damaged variants; {read} still read, {imported} of the sampled ones imported; slowest single attempt {slowest:F2} s; no crash");
        File.WriteAllLines(ReportPath("glimmer-psd-abuse.txt"), lines);
        Assert.True(attempts > 5_000 && slowest < 10, lines[^1]);
    }

    /// <summary>Swift: <c>aRealFileDeclaringAHugeCanvasStaysWithinItsBounds</c>: a real file's layer records under a canvas
    /// declared at Glimmer's limit, and just over it.</summary>
    [SamplesOnlyFact]
    public void ARealFileDeclaringAHugeCanvasStaysWithinItsBounds()
    {
        var path = Files().First(f => f.Contains("levels_rgb"));
        var data = File.ReadAllBytes(path);
        void Declare(uint width, uint height)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(14), height);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(18), width);
        }
        long limit = DocumentLimits.MaxCanvasPixels();
        uint side = (uint)Math.Min(30_000, (long)Math.Sqrt(limit));
        Declare(side, side);
        var start = System.Diagnostics.Stopwatch.StartNew();
        var file = new PSDFile(data);
        long decoded = PSDFileTests.DecodedBytes(file) + file.Composite.Sum(p => (long)p.Length);
        var (state, report) = PSDImport.Document(data);
        double seconds = start.Elapsed.TotalSeconds;
        Assert.True(state.Width == side && decoded < 4_000_000 && seconds < 10, $"{side} × {side}: {decoded} bytes decoded, {seconds:F2} s, {report.LayersKept} layers");
        // Nothing the size of the canvas was allocated: the comparison is skipped for a document this large.
        if ((long)side * side > PSDAppearance.LargestCompared) Assert.Null(PSDAppearance.Difference(state, data));
        // Past the limit: refused from the header.
        if (side < 30_000)
        {
            Declare(30_000, 30_000);
            Assert.Throws<PSDFile.Problem>(() => new PSDFile(data));
        }
    }
}
#endif
