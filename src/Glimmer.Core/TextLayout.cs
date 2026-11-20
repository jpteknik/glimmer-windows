// Part of the port of Packages/GlimmerKit/Sources/GlimmerCore/TextAndPaths.swift: what the Mac does with Core Text
// (CTFramesetter, CTLine, CTFrameDraw, CTFontCreatePathForGlyph) done with HarfBuzz for shaping and Skia for drawing.
//
// The layout follows the attributed string TextLayer.attributedString() builds on the Mac: each character in its
// run's font and colour, kern = tracking / 1000 × size after every character, every line exactly
// lineSize × lineHeight tall (minimum = maximum line height) with the baseline at the bottom of the line less the
// font's descent, and lineSize the layer's size or, in a paragraph with bigger letters, the biggest size in it.
// Paragraph text wraps at word boundaries and inside a word only when the word alone is too wide. Metrics differ
// from Core Text's, so the results are close to the Mac's, not equal to them.

using System.Globalization;
using System.Runtime.CompilerServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Glimmer.Core;

internal static class TextLayout
{
    /// <summary>Laid-out glyphs of one font, size and colour, positioned relative to their line's start and baseline.</summary>
    internal sealed class GlyphRun
    {
        public required FontFace Face { get; init; }
        public required float Size { get; init; }
        public required SRGBColor Color { get; init; }
        public required ushort[] Glyphs { get; init; }
        /// <summary>Pen x of each glyph from the line's start (offsets included).</summary>
        public required float[] X { get; init; }
        /// <summary>y of each glyph relative to the baseline, positive down.</summary>
        public required float[] Y { get; init; }
        public required float[] Advances { get; init; }
    }

    internal sealed class Line
    {
        public required List<GlyphRun> Runs { get; init; }
        /// <summary>Typographic width without trailing white space.</summary>
        public required double Width { get; init; }
        /// <summary>Typographic width with trailing white space.</summary>
        public required double FullWidth { get; init; }
        public required double Top { get; init; }
        public required double Height { get; init; }
        public required double Descent { get; init; }
        public double Baseline => Top + Height - Descent;
    }

    internal sealed class Layout
    {
        public required List<Line> Lines { get; init; }
        public required double MaxLineWidth { get; init; }
        public required double Height { get; init; }
    }

    /// <summary>Both layouts of a layer: as measured (wrapped at the box width) and as drawn (in its frame).</summary>
    internal sealed class Laid
    {
        private readonly TextLayer text;
        private readonly Lazy<Layout> measured;
        private readonly Lazy<Layout> framed;

        public Laid(TextLayer text)
        {
            this.text = text;
            measured = new Lazy<Layout>(() => Build(text, text.BoxWidth));
            framed = new Lazy<Layout>(() => Build(text, text.BoxWidth is null ? null : LayoutSize.Width));
        }

        /// <summary>
        /// The Mac's <c>layoutSize</c>: <c>CTFramesetterSuggestFrameSizeWithConstraints</c> at the box width (or
        /// unconstrained), then <c>ceil(boxWidth ?? fit.width) + 2</c> by <c>ceil(fit.height) + 2</c>.
        /// </summary>
        public CGSizeLike LayoutSize
        {
            get
            {
                var m = measured.Value;
                return new CGSizeLike(Math.Ceiling(text.BoxWidth ?? m.MaxLineWidth) + 2, Math.Ceiling(m.Height) + 2);
            }
        }

        /// <summary>The layout in the frame <c>render()</c> and <c>glyphOutlinesByColor()</c> draw into (as wide as <see cref="LayoutSize"/>).</summary>
        public Layout Framed => framed.Value;
    }

    private static readonly ConditionalWeakTable<TextLayer, Laid> Cache = new();

    internal static Laid For(TextLayer text) => Cache.GetValue(text, static t => new Laid(t));

    /// <summary>The x of a line's start in a frame of <paramref name="frameWidth"/>, by alignment (trailing white space hangs).</summary>
    private static double AlignedX(TextLayer text, Line line, double frameWidth) => text.Alignment switch
    {
        TextAlignment.Center => (frameWidth - line.Width) / 2,
        TextAlignment.Right => frameWidth - line.Width,
        _ => 0,
    };

    // MARK: Layout

    /// <summary>The characters' styles as runs over the laid-out string (the Mac lays out " " for an empty text).</summary>
    private static (string Text, List<(TextRange Range, TextStyle Style)> Runs, bool Styled) Attributed(TextLayer text)
    {
        bool styled = text.HasStyledRuns && text.String.Length > 0;
        string s = text.String.Length == 0 ? " " : text.String;
        List<(TextRange Range, TextStyle Style)> runs = styled ? [.. text.ResolvedRuns()] : [(new TextRange(0, s.Length), text.BaseStyle)];
        return (s, runs, styled);
    }

    private static bool IsParagraphSeparator(char c) =>
        c is '\n' or '\r' or (char)0x2029 or (char)0x2028 or (char)0x0085;

    internal static Layout Build(TextLayer text, double? wrapWidth)
    {
        var (s, runs, styled) = Attributed(text);
        var lines = new List<Line>();
        double top = 0, maxWidth = 0;
        int start = 0;
        while (start < s.Length)
        {
            // A paragraph runs to its separator; "\r\n" is one separator. A separator at the very end adds no line.
            int end = start;
            while (end < s.Length && !IsParagraphSeparator(s[end])) end++;
            int next = end;
            if (next < s.Length) next += s[next] == '\r' && next + 1 < s.Length && s[next + 1] == '\n' ? 2 : 1;

            double lineSize = text.Size;
            if (styled)
            {
                // Each paragraph's lines are as tall as its largest letters need, so a bigger word doesn't run into the line above.
                var enclosing = new TextRange(start, next - start);
                double largest = 0;
                foreach (var (range, style) in runs)
                    if (TextRange.Intersection(range, enclosing).Length > 0) largest = Math.Max(largest, style.Size);
                if (largest > text.Size) lineSize = largest;
            }
            double lineHeight = lineSize * text.LineHeight;

            var shaped = Shape(s, start, end, runs, text.Tracking);
            foreach (var (from, to) in BreakLines(s, start, end, shaped, wrapWidth))
            {
                var line = MakeLine(s, from, to, shaped, top, lineHeight, runs);
                lines.Add(line);
                maxWidth = Math.Max(maxWidth, line.Width);
                top += lineHeight;
            }
            start = next;
        }
        return new Layout { Lines = lines, MaxLineWidth = maxWidth, Height = top };
    }

    /// <summary>One shaped glyph in logical order.</summary>
    private readonly record struct Glyph(ushort Id, int Cluster, float Advance, float OffsetX, float OffsetY, FontFace Face, float Size,
                                         SRGBColor Color, bool RightToLeft);

    /// <summary>Glyphs for s[start..end), in logical order, with each character in its run's font (or a fallback font for characters it lacks).</summary>
    private static List<Glyph> Shape(string s, int start, int end, List<(TextRange Range, TextStyle Style)> runs, double tracking)
    {
        var glyphs = new List<Glyph>();
        foreach (var (range, style) in runs)
        {
            int lo = Math.Max(range.Location, start), hi = Math.Min(range.End, end);
            if (lo >= hi) continue;
            var primary = FontResolver.Face(style.FontName);
            float size = (float)style.Size;
            float kern = (float)(tracking / 1000 * style.Size);
            // Split the run where a character needs another font.
            int segStart = lo;
            FontFace? segFace = null;
            int i = lo;
            while (i < hi)
            {
                System.Text.Rune.DecodeFromUtf16(s.AsSpan(i, hi - i), out var rune, out int width);
                int cp = rune.Value;
                if (width <= 0) width = 1;
                var face = primary;
                if (!char.IsWhiteSpace(s, i) && !char.IsControl(s, i) && !primary.HasGlyph(cp)
                    && CharUnicodeInfo.GetUnicodeCategory(cp) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format))
                {
                    face = FontResolver.Fallback(primary, cp) ?? primary;
                }
                if (segFace is not null && !ReferenceEquals(face, segFace))
                {
                    ShapeSegment(s, segStart, i, segFace, size, style.Color, kern, glyphs);
                    segStart = i;
                }
                segFace = face;
                i += width;
            }
            if (segFace is not null) ShapeSegment(s, segStart, hi, segFace, size, style.Color, kern, glyphs);
        }
        return glyphs;
    }

    private static void ShapeSegment(string s, int from, int to, FontFace face, float size, SRGBColor color, float kern, List<Glyph> into)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(s, from, to - from);
        buffer.GuessSegmentProperties();
        bool rtl = buffer.Direction == Direction.RightToLeft;
        lock (face.ShapingLock) face.Shaper.Shape(buffer);
        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        float scale = size / FontFace.ShapingScale;
        var segment = new Glyph[infos.Length];
        for (int k = 0; k < infos.Length; k++)
        {
            // Core Text adds the kern after every character; a glyph that starts a new cluster carries it.
            bool clusterStart = k == 0 || infos[k].Cluster != infos[k - 1].Cluster;
            float advance = positions[k].XAdvance * scale + (clusterStart ? kern : 0);
            segment[k] = new Glyph((ushort)infos[k].Codepoint, (int)infos[k].Cluster, advance, positions[k].XOffset * scale,
                                   -positions[k].YOffset * scale, face, size, color, rtl);
        }
        if (rtl) Array.Reverse(segment);   // logical order; placed right to left again when the line is built
        into.AddRange(segment);
    }

    /// <summary>The lines of a paragraph as [from, to) ranges of the string.</summary>
    private static List<(int From, int To)> BreakLines(string s, int start, int end, List<Glyph> glyphs, double? wrapWidth)
    {
        var result = new List<(int, int)>();
        if (start == end)
        {
            result.Add((start, end));
            return result;
        }
        // Advance per UTF-16 index (a ligature's advance counts at its first character).
        var advance = new double[end - start + 1];
        foreach (var g in glyphs) advance[g.Cluster - start] += g.Advance;
        var prefix = new double[end - start + 1];
        for (int i = 0; i < end - start; i++) prefix[i + 1] = prefix[i] + advance[i];
        double Width(int from, int to)
        {
            while (to > from && char.IsWhiteSpace(s[to - 1])) to--;
            return prefix[to - start] - prefix[from - start];
        }

        if (wrapWidth is not { } limit || double.IsInfinity(limit))
        {
            result.Add((start, end));
            return result;
        }

        var breaks = BreakOpportunities(s, start, end);
        var characterStarts = CharacterStarts(s, start, end);
        int lineStart = start;
        while (lineStart < end)
        {
            int lastFit = -1;
            foreach (int bp in breaks)
            {
                if (bp <= lineStart) continue;
                if (Width(lineStart, bp) <= limit + 1e-6) lastFit = bp;
                else break;
            }
            if (lastFit < 0)
            {
                // The first word alone is too wide: break inside it, at least one character a line.
                int fit = -1;
                foreach (int cs in characterStarts)
                {
                    if (cs <= lineStart) continue;
                    if (fit < 0 || Width(lineStart, cs) <= limit + 1e-6) fit = cs;
                    else break;
                }
                lastFit = fit < 0 ? end : fit;
            }
            result.Add((lineStart, lastFit));
            lineStart = lastFit;
        }
        return result;
    }

    /// <summary>
    /// Where a line may end in s[start..end): after a run of spaces, after a hyphen between letters, between CJK
    /// ideographs, and at the end.
    /// </summary>
    private static List<int> BreakOpportunities(string s, int start, int end)
    {
        var result = new List<int>();
        for (int i = start + 1; i < end; i++)
        {
            char prev = s[i - 1], c = s[i];
            bool afterSpace = char.IsWhiteSpace(prev) && !char.IsWhiteSpace(c);
            bool afterHyphen = (prev == '-' || prev == (char)0x2010) && char.IsLetter(c) && i >= start + 2 && char.IsLetter(s[i - 2]);
            bool ideographs = IsIdeograph(prev) || IsIdeograph(c);
            if ((afterSpace || afterHyphen || ideographs) && !char.IsLowSurrogate(c)) result.Add(i);
        }
        result.Add(end);
        return result;
    }

    private static bool IsIdeograph(char c) =>
        c is >= (char)0x3040 and <= (char)0x30FF or >= (char)0x3400 and <= (char)0x4DBF
            or >= (char)0x4E00 and <= (char)0x9FFF or >= (char)0xF900 and <= (char)0xFAFF;

    private static List<int> CharacterStarts(string s, int start, int end)
    {
        var result = new List<int>();
        var e = StringInfo.GetTextElementEnumerator(s[start..end]);
        while (e.MoveNext()) result.Add(start + e.ElementIndex);
        result.Add(end);
        return result;
    }

    private static Line MakeLine(string s, int from, int to, List<Glyph> glyphs, double top, double height,
                                 List<(TextRange Range, TextStyle Style)> runs)
    {
        var inLine = glyphs.Where(g => g.Cluster >= from && g.Cluster < to).ToList();
        // Right-to-left stretches are placed right to left inside their span.
        var x = new float[inLine.Count];
        float pen = 0;
        int k = 0;
        while (k < inLine.Count)
        {
            if (!inLine[k].RightToLeft)
            {
                x[k] = pen + inLine[k].OffsetX;
                pen += inLine[k].Advance;
                k++;
                continue;
            }
            int j = k;
            float span = 0;
            while (j < inLine.Count && inLine[j].RightToLeft && ReferenceEquals(inLine[j].Face, inLine[k].Face))
            {
                span += inLine[j].Advance;
                j++;
            }
            float right = pen + span;
            for (int m = k; m < j; m++)
            {
                right -= inLine[m].Advance;
                x[m] = right + inLine[m].OffsetX;
            }
            pen += span;
            k = j;
        }

        double fullWidth = pen;
        double trailing = 0;
        for (int i = inLine.Count - 1; i >= 0 && char.IsWhiteSpace(s, inLine[i].Cluster); i--) trailing += inLine[i].Advance;

        var lineRuns = new List<GlyphRun>();
        double descent = 0;
        int r = 0;
        while (r < inLine.Count)
        {
            int e = r + 1;
            while (e < inLine.Count && ReferenceEquals(inLine[e].Face, inLine[r].Face) && inLine[e].Size == inLine[r].Size
                   && inLine[e].Color == inLine[r].Color) e++;
            var g0 = inLine[r];
            lineRuns.Add(new GlyphRun
            {
                Face = g0.Face,
                Size = g0.Size,
                Color = g0.Color,
                Glyphs = [.. inLine.Skip(r).Take(e - r).Select(g => g.Id)],
                X = x[r..e],
                Y = [.. inLine.Skip(r).Take(e - r).Select(g => g.OffsetY)],
                Advances = [.. inLine.Skip(r).Take(e - r).Select(g => g.Advance)],
            });
            using var font = g0.Face.CreateFont(g0.Size);
            descent = Math.Max(descent, font.Metrics.Descent);
            r = e;
        }
        if (inLine.Count == 0)
        {
            // An empty line takes its metrics from the style at its position.
            var style = runs.FirstOrDefault(run => run.Range.Location <= from && from < run.Range.End).Style ?? runs[0].Style;
            using var font = FontResolver.Face(style.FontName).CreateFont((float)style.Size);
            descent = font.Metrics.Descent;
        }
        return new Line { Runs = lineRuns, Width = fullWidth - trailing, FullWidth = fullWidth, Top = top, Height = height, Descent = descent };
    }

    // MARK: Drawing

    private const long MaxPixels = 300_000_000;

    internal static TextRaster? Render(TextLayer text)
    {
        if (text.Transform is { } t && t != AffineTransform.Identity)
        {
            // Lay out and draw as usual, then map the picture through the transform.
            var plain = text with { Transform = null };
            if (Render(plain) is not { } image) return null;
            var tb = text.Bounds;
            if (tb.Width <= 0 || tb.Height <= 0 || tb.Area >= MaxPixels) return null;
            return Draw(tb, canvas =>
            {
                using var source = ToImage(image);
                canvas.Translate(-tb.X, -tb.Y);
                var m = new SKMatrix((float)t.A, (float)t.C, (float)t.Tx, (float)t.B, (float)t.D, (float)t.Ty, 0, 0, 1);
                canvas.Concat(in m);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(source, new SKRect(image.X, image.Y, image.X + image.Width, image.Y + image.Height),
                                 new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            });
        }
        var b = text.Bounds;
        if (b.Width <= 0 || b.Height <= 0 || b.Area >= MaxPixels) return null;
        if (text.OnPath is { Length: > 1 } line) return Draw(b, canvas => DrawOnPath(text, line, canvas, b));
        double fx = text.Origin.X - b.X, fy = text.Origin.Y - b.Y;
        var laid = For(text);
        var size = laid.LayoutSize;
        if (text.ActiveWarp is { } warp) return Draw(b, canvas => DrawWarped(text, warp, canvas, fx, fy, size));
        return Draw(b, canvas =>
        {
            foreach (var l in laid.Framed.Lines)
            {
                double x = fx + AlignedX(text, l, size.Width);
                DrawLine(canvas, l, (float)x, (float)(fy + l.Baseline));
            }
        });
    }

    private static TextRaster Draw(PixelRect b, Action<SKCanvas> draw)
    {
        var info = new SKImageInfo(b.Width, b.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var surface = SKSurface.Create(info) ?? throw new GraphicsMemoryException();
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        surface.Canvas.Flush();
        var pixels = new byte[(long)b.Width * b.Height * 4];
        using var pixmap = surface.PeekPixels() ?? throw new InvalidOperationException("The rendered text could not be read back.");
        if (pixmap.RowBytes == b.Width * 4)
        {
            pixmap.GetPixelSpan()[..pixels.Length].CopyTo(pixels);
        }
        else
        {
            for (int y = 0; y < b.Height; y++)
                pixmap.GetPixelSpan(0, y)[..(b.Width * 4)].CopyTo(pixels.AsSpan(y * b.Width * 4, b.Width * 4));
        }
        return new TextRaster(b.X, b.Y, b.Width, b.Height, pixels);
    }

    private static SKImage ToImage(TextRaster raster)
    {
        var info = new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var data = SKData.CreateCopy(raster.Pixels);
        return SKImage.FromPixels(info, data, raster.Width * 4) ?? throw new GraphicsMemoryException();
    }

    private static SKPaint Fill(SRGBColor color) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        ColorF = new SKColorF((float)color.R, (float)color.G, (float)color.B, 1),
    };

    private static void DrawLine(SKCanvas canvas, Line line, float x, float baseline)
    {
        foreach (var run in line.Runs)
        {
            if (run.Glyphs.Length == 0) continue;
            using var font = run.Face.CreateFont(run.Size);
            using var builder = new SKTextBlobBuilder();
            var points = new SKPoint[run.Glyphs.Length];
            for (int i = 0; i < points.Length; i++) points[i] = new SKPoint(x + run.X[i], baseline + run.Y[i]);
            builder.AddPositionedRun(run.Glyphs, font, points);
            using var blob = builder.Build();
            if (blob is null) continue;
            using var paint = Fill(run.Color);
            canvas.DrawText(blob, 0, 0, paint);
        }
    }

    /// <summary>The glyph outlines of the framed layout, grouped by colour in the order colours first appear (text-box coordinates, y down).</summary>
    internal static IReadOnlyList<(SRGBColor Color, CGPathLike Path)> GlyphOutlinesByColor(TextLayer text)
    {
        var laid = For(text);
        var size = laid.LayoutSize;
        var groups = new List<(SRGBColor Color, CGPathLike Path)>();
        foreach (var line in laid.Framed.Lines)
        {
            double x0 = AlignedX(text, line, size.Width);
            foreach (var run in line.Runs)
            {
                int g = groups.FindIndex(p => p.Color == run.Color);
                if (g < 0)
                {
                    groups.Add((run.Color, new CGPathLike()));
                    g = groups.Count - 1;
                }
                var target = groups[g].Path;
                using var font = run.Face.CreateFont(run.Size);
                for (int i = 0; i < run.Glyphs.Length; i++)
                {
                    using var glyph = font.GetGlyphPath(run.Glyphs[i]);
                    if (glyph is null || glyph.IsEmpty) continue;
                    AppendPath(target, glyph, x0 + run.X[i], line.Baseline + run.Y[i]);
                }
            }
        }
        return groups;
    }

    /// <summary>Appends a Skia path (y down) translated by (dx, dy). Conics become quadratics.</summary>
    internal static void AppendPath(CGPathLike target, SKPath path, double dx, double dy)
    {
        Vec2 P(SKPoint p) => new(p.X + dx, p.Y + dy);
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: target.MoveTo(P(pts[0])); break;
                case SKPathVerb.Line: target.LineTo(P(pts[1])); break;
                case SKPathVerb.Quad: target.QuadTo(P(pts[1]), P(pts[2])); break;
                case SKPathVerb.Conic:
                {
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                    for (int q = 1; q + 1 < quads.Length; q += 2) target.QuadTo(P(quads[q]), P(quads[q + 1]));
                    break;
                }
                case SKPathVerb.Cubic: target.CubicTo(P(pts[1]), P(pts[2]), P(pts[3])); break;
                case SKPathVerb.Close: target.Close(); break;
            }
        }
    }

    internal static SKPath ToSkPath(CGPathLike path)
    {
        var sk = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var e in path.Elements)
        {
            switch (e.Verb)
            {
                case CGPathLike.Verb.Move: sk.MoveTo((float)e.P0.X, (float)e.P0.Y); break;
                case CGPathLike.Verb.Line: sk.LineTo((float)e.P0.X, (float)e.P0.Y); break;
                case CGPathLike.Verb.Quad: sk.QuadTo((float)e.P0.X, (float)e.P0.Y, (float)e.P1.X, (float)e.P1.Y); break;
                case CGPathLike.Verb.Cubic:
                    sk.CubicTo((float)e.P0.X, (float)e.P0.Y, (float)e.P1.X, (float)e.P1.Y, (float)e.P2.X, (float)e.P2.Y);
                    break;
                case CGPathLike.Verb.Close: sk.Close(); break;
            }
        }
        return sk;
    }

    /// <summary>Fills the glyph outlines bent through the warp envelope (vector, so edges stay crisp).</summary>
    private static void DrawWarped(TextLayer text, TextWarp warp, SKCanvas canvas, double ox, double oy, CGSizeLike size)
    {
        var place = new AffineTransform(tx: ox, ty: oy);
        foreach (var (color, outlines) in GlyphOutlinesByColor(text))
        {
            var placed = warp.Apply(outlines, size.Width, size.Height).Transformed(place);
            using var path = ToSkPath(placed);
            using var paint = Fill(color);
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>Draws each glyph centred on its arc-length position along <paramref name="line"/>, rotated to the tangent.</summary>
    private static void DrawOnPath(TextLayer text, System.Collections.Immutable.ImmutableArray<Vec2> line, SKCanvas canvas, PixelRect b)
    {
        var lengths = new double[line.Length];
        for (int i = 1; i < line.Length; i++) lengths[i] = lengths[i - 1] + Vec2.Distance(line[i - 1], line[i]);
        double total = lengths[^1];
        if (!(total > 0)) return;
        // One line of the whole text, as CTLineCreateWithAttributedString lays it out.
        var single = SingleLine(text);
        double width = single.FullWidth;
        double start = text.Alignment switch
        {
            TextAlignment.Center => (total - width) / 2,
            TextAlignment.Right => total - width,
            _ => 0,
        };

        (Vec2 Point, double Angle)? PointAt(double s)
        {
            if (s < 0 || s > total) return null;
            int i = 1;
            while (i < lengths.Length - 1 && lengths[i] < s) i++;
            double seg = lengths[i] - lengths[i - 1];
            double t = seg > 0 ? (s - lengths[i - 1]) / seg : 0;
            Vec2 a = line[i - 1], c = line[i];
            return (a + (c - a) * t, Math.Atan2(c.Y - a.Y, c.X - a.X));
        }

        foreach (var run in single.Runs)
        {
            using var font = run.Face.CreateFont(run.Size);
            using var paint = Fill(run.Color);
            for (int k = 0; k < run.Glyphs.Length; k++)
            {
                double mid = start + run.X[k] + run.Advances[k] / 2;
                if (PointAt(mid) is not { } at) continue;
                canvas.Save();
                // Document (y down) → picture (y down).
                canvas.Translate((float)(at.Point.X - b.X), (float)(at.Point.Y - b.Y));
                canvas.RotateRadians((float)at.Angle);
                using var builder = new SKTextBlobBuilder();
                builder.AddPositionedRun([run.Glyphs[k]], font, [new SKPoint(-run.Advances[k] / 2, 0)]);
                using var blob = builder.Build();
                if (blob is not null) canvas.DrawText(blob, 0, 0, paint);
                canvas.Restore();
            }
        }
    }

    /// <summary>The whole text on one line (separators take no room), as a <c>CTLine</c> made from the attributed string.</summary>
    internal static Line SingleLine(TextLayer text)
    {
        var (s, runs, _) = Attributed(text);
        var glyphs = Shape(s, 0, s.Length, runs, text.Tracking)
            .Where(g => !IsParagraphSeparator(s[g.Cluster]))
            .ToList();
        return MakeLine(s, 0, s.Length, glyphs, 0, text.Size * text.LineHeight, runs);
    }
}
