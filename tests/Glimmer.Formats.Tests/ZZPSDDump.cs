// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/ZZPSDDump.swift: opt-in, a diagnostic dump of Photoshop files
// named by GLIMMER_PSD_DUMP (separated by the system's path-list separator, ';' on Windows, where ':' is part of a path):
// each layer as the reader sees it, and where Glimmer's picture differs from the file's own. The Swift prints the lines;
// here they go to psd-dump.txt in GLIMMER_PSD_DUMP_OUT (or the temporary folder), with each file's two pictures as PNG
// beside it. GLIMMER_PSD_DUMP_MASK adds sample mask and transparency values, as there.
#if GLIMMER_PSD
using Glimmer.Core;

namespace Glimmer.Formats.Tests;

public class ZZPSDDump
{
    [OptInFact("GLIMMER_PSD_DUMP")]
    public void Dump()
    {
        string output = Environment.GetEnvironmentVariable("GLIMMER_PSD_DUMP_OUT") is { Length: > 0 } o ? o : Path.GetTempPath();
        bool masks = Environment.GetEnvironmentVariable("GLIMMER_PSD_DUMP_MASK") is not null;
        var lines = new List<string>();
        foreach (var path in Environment.GetEnvironmentVariable("GLIMMER_PSD_DUMP")!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var data = File.ReadAllBytes(path);
            lines.Add($"DUMP == {Path.GetFileName(path)} {data.Length} bytes");
            try
            {
                var file = new PSDFile(data);
                lines.Add($"DUMP header {file.Header} composite planes {file.Composite.Length}");
                foreach (var l in file.Layers)
                {
                    string planes = string.Join(", ", l.Planes.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value.Length}"));
                    string mask = l.Mask is { } m ? $"{m.Rect} default {m.DefaultColor} disabled {m.IsDisabled} pixels {m.Pixels?.Length ?? -1}" : "-";
                    lines.Add($"DUMP layer “{l.Name}” rect {l.Rect} blend '{l.BlendKey}' op {l.Opacity} vis {l.IsVisible} clip {l.IsClipped} group {l.Group} planes [{planes}] mask {mask} keys {string.Join(" ", l.ExtraKeys)}");
                    if (l.Blocks.TryGetValue("vmsk", out var v) || l.Blocks.TryGetValue("vsms", out v))
                    {
                        var bytes = v.Span;
                        var selectors = new List<int>();
                        for (int at = 8; at + 1 < Math.Min(bytes.Length, 8 + 26 * 14); at += 26) selectors.Add(bytes[at] << 8 | bytes[at + 1]);
                        lines.Add($"DUMP   vmsk {v.Length} bytes, version+flags: [{string.Join(", ", bytes[..Math.Min(8, bytes.Length)].ToArray())}], selectors: [{string.Join(", ", selectors)}]");
                        var subpaths = PSDPath.Subpaths(v.Length > 8 ? v[8..] : ReadOnlyMemory<byte>.Empty, file.Header.Width, file.Header.Height);
                        lines.Add($"DUMP   subpaths: [{string.Join(", ", subpaths.Select(s => $"{(s.IsClosed ? "closed" : "open")} {string.Concat(s.Anchors.Select(a => $"({(int)a.Point.X},{(int)a.Point.Y})"))} "))}]");
                    }
                    if (masks && l.Mask is { Pixels: { } maskPixels } maskRecord)
                    {
                        long mw = maskRecord.Rect.Width;
                        int At(int x, int y) { long i = (y - maskRecord.Rect.Top) * mw + (x - maskRecord.Rect.Left); return i >= 0 && i < maskPixels.Length ? maskPixels.Span[(int)i] : -1; }
                        lines.Add($"DUMP   mask row 0, x 120...126: [{string.Join(", ", Enumerable.Range(120, 7).Select(x => At(x, 0)))}]; row 1: [{string.Join(", ", Enumerable.Range(120, 7).Select(x => At(x, 1)))}]; column 199, y 40...44: [{string.Join(", ", Enumerable.Range(40, 5).Select(y => At(199, y)))}]; column 198: [{string.Join(", ", Enumerable.Range(40, 5).Select(y => At(198, y)))}]; row 199: [{string.Join(", ", Enumerable.Range(120, 5).Select(x => At(x, 199)))}]");
                    }
                    if (masks && l.Planes.TryGetValue(-1, out var alpha) && l.Rect.Width > 0)
                    {
                        long lw = l.Rect.Width;
                        int At(int x, int y) { long i = (y - l.Rect.Top) * lw + (x - l.Rect.Left); return i >= 0 && i < alpha.Length ? alpha.Span[(int)i] : -1; }
                        lines.Add($"DUMP   alpha row 0, x 120...126: [{string.Join(", ", Enumerable.Range(120, 7).Select(x => At(x, 0)))}]; row 1: [{string.Join(", ", Enumerable.Range(120, 7).Select(x => At(x, 1)))}]; column 199: [{string.Join(", ", Enumerable.Range(40, 5).Select(y => At(199, y)))}]; column 198: [{string.Join(", ", Enumerable.Range(40, 5).Select(y => At(198, y)))}]; row 199: [{string.Join(", ", Enumerable.Range(120, 5).Select(x => At(x, 199)))}]; row -2: [{string.Join(", ", Enumerable.Range(120, 5).Select(x => At(x, -2)))}]");
                    }
                    if (l.Blocks.TryGetValue("curv", out var curv)) lines.Add($"DUMP   curv {curv.Length} bytes: [{string.Join(", ", curv.Span[..Math.Min(48, curv.Length)].ToArray())}]");
                    if (l.Blocks.TryGetValue("SoCo", out var soco))
                    {
                        string value;
                        try { value = Describe(PSDDescriptor.Read(soco.Length > 4 ? soco[4..] : ReadOnlyMemory<byte>.Empty)); }
                        catch (PSDFile.Problem) { value = "unreadable"; }
                        lines.Add(("DUMP   SoCo: " + value)[..Math.Min(300, 13 + value.Length)]);
                    }
                }
            }
            catch (PSDFile.Problem error) { lines.Add($"DUMP PSDFile error: {error.Message}"); }
            // Where the two pictures differ, and by how much.
            DocumentState? state = null;
            try { state = PSDImport.Document(data).State; } catch (PSDFile.Problem) { }
            if (state is not null)
            {
                using var ours = ImageExporter.FlattenedSRGB(state);
                using var theirs = PSDAppearance.FlattenedPicture(data);
                if (theirs is not null && PSDAppearance.OnWhite(ours) is { } a && PSDAppearance.OnWhite(theirs) is { } b && a.Length == b.Length)
                {
                    int w = state.Width, shown = 0, worst = 0, count = 0, minX = int.MaxValue, maxX = 0, minY = int.MaxValue, maxY = 0;
                    for (int i = 0; i < a.Length; i += 4)
                    {
                        int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
                        if (d > 0) worst = Math.Max(worst, d);
                        if (d <= 4) continue;
                        count += 1;
                        int x = i / 4 % w, y = i / 4 / w;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        if (shown < 14 && count % 23 == 1)
                        {
                            shown += 1;
                            lines.Add($"DUMP   differs at ({x},{y}): ours {a[i]},{a[i + 1]},{a[i + 2]}  file {b[i]},{b[i + 1]},{b[i + 2]}");
                        }
                    }
                    lines.Add($"DUMP   {count} pixels differ by more than 4; worst {worst}; box x {minX}...{maxX} y {minY}...{maxY} of {w} wide");
                }
                string stem = Path.Combine(output, Path.GetFileNameWithoutExtension(path));
                File.WriteAllBytes(stem + "-ours.png", ImageExporter.Encode(ours, new ExportOptions(ExportFormat.Png)));
                if (theirs is not null) File.WriteAllBytes(stem + "-theirs.png", ImageExporter.Encode(theirs, new ExportOptions(ExportFormat.Png)));
            }
        }
        File.WriteAllLines(Path.Combine(output, "psd-dump.txt"), lines);
    }

    /// <summary>A descriptor value written out whole (Swift's description of its enum).</summary>
    private static string Describe(PSDValue value) => value switch
    {
        PSDValue.Object o => $"{o.ClassID} {{{string.Join(", ", o.Items.Select(i => $"{i.Key}: {Describe(i.Value)}"))}}}",
        PSDValue.List l => $"[{string.Join(", ", l.Values.Select(Describe))}]",
        PSDValue.Raw r => $"raw {r.Data.Length} bytes",
        PSDValue.Double d => d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        PSDValue.UnitFloat u => $"{u.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {u.Unit}",
        PSDValue.String t => $"\"{t.Value}\"",
        PSDValue.Enumerated e => $"{e.Type}.{e.Value}",
        PSDValue.Integer n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PSDValue.Boolean b => b.Value ? "true" : "false",
        PSDValue.Other other => $"({other.Type})",
        _ => value.GetType().Name,
    };
}
#endif
