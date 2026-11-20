// New in the port, no Swift file of its own: the classic image analysis that replaces three Vision requests the Mac
// uses, written for this port.
//
// - Horizon (Vision's VNDetectHorizonRequest): the longest strong straight edge within 25° of level, found with a Hough
//   transform on the edges, its angle then fitted to the edge pixels by least squares.
// - Saliency (VNGenerateAttentionBasedSaliencyImageRequest): the spectral residual of Hou and Zhang ("Saliency
//   Detection: A Spectral Residual Approach", CVPR 2007) on the luminance and two colour-opponent channels.
// - Document (VNDetectDocumentSegmentationRequest with VNDetectRectanglesRequest as reserve): the quadrilateral of four
//   straight edges (Hough peaks) with the most edge support whose sides each keep one polarity along their length and
//   whose inside differs from its surroundings; then the perspective correction CIPerspectiveCorrection does, as a
//   homography.
//
// Vision's horizon and document detectors are learned models; these are not, so they find what has straight, strong
// edges and miss what doesn't (a horizon in haze, a sheet on a white table).
using System.Numerics;
using Glimmer.Core;

namespace Glimmer.AI;

/// <summary>Pixels in the document's own format: premultiplied, linear, extended sRGB RGBA floats, row-major.</summary>
public sealed record LinearImage(int Width, int Height, float[] Pixels)
{
    public bool Equals(LinearImage? other) =>
        other is not null && Width == other.Width && Height == other.Height && Pixels.AsSpan().SequenceEqual(other.Pixels);

    public override int GetHashCode() => HashCode.Combine(Width, Height, Pixels.Length);
}

internal static class ClassicVision
{
    // MARK: Grey working copies

    /// <summary>The light of <paramref name="image"/> at w × h (sRGB-encoded luminance, 0…1, colour over black).</summary>
    public static float[] Luminance(RGBAImage image, int w, int h)
    {
        var pre = PixelSampling.ResamplePremultiplied(image, w, h);
        var l = new float[w * h];
        for (int i = 0; i < l.Length; i++)
            l[i] = (0.2126f * pre[i * 4] + 0.7152f * pre[i * 4 + 1] + 0.0722f * pre[i * 4 + 2]) / 255;
        return l;
    }

    /// <summary>Separable Gaussian blur with edges extended.</summary>
    public static float[] Blur(float[] v, int w, int h, double sigma) => Imaging.Gaussian(v, w, h, sigma);

    /// <summary>Sobel gradients (edges extended).</summary>
    public static (float[] Gx, float[] Gy, float[] Magnitude) Sobel(float[] v, int w, int h)
    {
        var gx = new float[w * h];
        var gy = new float[w * h];
        var m = new float[w * h];
        Imaging.ForRows(h, (long)w * h, y =>
        {
            int y0 = Math.Max(y - 1, 0), y1 = Math.Min(y + 1, h - 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(x - 1, 0), x1 = Math.Min(x + 1, w - 1);
                float a = v[y0 * w + x0], b = v[y0 * w + x], c = v[y0 * w + x1];
                float d = v[y * w + x0], f = v[y * w + x1];
                float g = v[y1 * w + x0], hh = v[y1 * w + x], k = v[y1 * w + x1];
                float sx = (c + 2 * f + k) - (a + 2 * d + g);
                float sy = (g + 2 * hh + k) - (a + 2 * b + c);
                int i = y * w + x;
                gx[i] = sx;
                gy[i] = sy;
                m[i] = MathF.Sqrt(sx * sx + sy * sy);
            }
        });
        return (gx, gy, m);
    }

    /// <summary>Edge pixels: the gradient magnitude's local maxima across the edge, above <paramref name="threshold"/>.</summary>
    public static bool[] ThinEdges(float[] gx, float[] gy, float[] m, int w, int h, float threshold)
    {
        var edge = new bool[w * h];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float v = m[i];
                if (v < threshold) continue;
                // Neighbours along the gradient, quantised to 4 directions.
                float ax = MathF.Abs(gx[i]), ay = MathF.Abs(gy[i]);
                int dx, dy;
                if (ay < ax * 0.4142f) { dx = 1; dy = 0; }
                else if (ax < ay * 0.4142f) { dx = 0; dy = 1; }
                else { dx = 1; dy = Math.Sign(gx[i]) == Math.Sign(gy[i]) ? 1 : -1; }
                if (v >= m[i + dy * w + dx] && v > m[i - dy * w - dx]) edge[i] = true;
            }
        }
        return edge;
    }

    private static float Percentile(float[] values, double p)
    {
        var copy = (float[])values.Clone();
        Array.Sort(copy);
        return copy[Math.Clamp((int)(p * (copy.Length - 1)), 0, copy.Length - 1)];
    }

    // MARK: Horizon

    /// <summary>
    /// The tilt of the horizon in degrees, positive when it rises to the right (the sign Glimmer uses; Vision's own angle
    /// has the other sign). Null when the picture has no long, strong edge within 25° of level.
    /// </summary>
    public static double? HorizonDegrees(RGBAImage image)
    {
        var (w, h) = PixelSampling.Fit(image.Width, image.Height, 512);
        if (w < 16 || h < 16) return null;
        var lum = Blur(Luminance(image, w, h), w, h, 1.0);
        var (gx, gy, m) = Sobel(lum, w, h);
        var edges = ThinEdges(gx, gy, m, w, h, 0.12f);
        const double maxAngle = 25, step = 0.1, window = 3;
        int angles = (int)Math.Round(2 * maxAngle / step) + 1;
        double rhoMin = -w * Math.Sin(maxAngle * Math.PI / 180) - 2, rhoMax = h + w * Math.Sin(maxAngle * Math.PI / 180) + 2;
        int rhos = (int)Math.Ceiling(rhoMax - rhoMin) + 1;
        var acc = new float[angles * rhos];
        var sin = new double[angles];
        var cos = new double[angles];
        for (int a = 0; a < angles; a++)
        {
            double t = (-maxAngle + a * step) * Math.PI / 180;
            sin[a] = Math.Sin(t);
            cos[a] = Math.Cos(t);
        }
        var points = new List<(int X, int Y, double Angle, float Weight)>();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!edges[i]) continue;
                // The edge runs across the gradient: its angle (image coordinates, y down) is the gradient's minus 90°.
                double edgeAngle = Math.Atan2(gy[i], gx[i]) * 180 / Math.PI - 90;
                while (edgeAngle <= -90) edgeAngle += 180;
                while (edgeAngle > 90) edgeAngle -= 180;
                if (Math.Abs(edgeAngle) > maxAngle + window) continue;
                points.Add((x, y, edgeAngle, m[i]));
                int a0 = Math.Max(0, (int)Math.Floor((edgeAngle - window + maxAngle) / step));
                int a1 = Math.Min(angles - 1, (int)Math.Ceiling((edgeAngle + window + maxAngle) / step));
                for (int a = a0; a <= a1; a++)
                {
                    // A line at angle θ (slope tan θ, y down) through (x, y): ρ = y cos θ − x sin θ.
                    double rho = y * cos[a] - x * sin[a];
                    acc[a * rhos + (int)Math.Round(rho - rhoMin)] += m[i];
                }
            }
        }
        int best = 0;
        for (int i = 1; i < acc.Length; i++) if (acc[i] > acc[best]) best = i;
        if (acc[best] <= 0) return null;
        int ba = best / rhos;
        double theta = (-maxAngle + ba * step) * Math.PI / 180, bestRho = best % rhos + rhoMin;
        // The pixels on that line (within 1.5 px, edge running along it), and how much of the width they cover.
        double st = Math.Sin(theta), ct = Math.Cos(theta);
        var onLine = new List<(int X, int Y, float Weight)>();
        var columns = new bool[w];
        foreach (var p in points)
        {
            if (Math.Abs(p.Angle - theta * 180 / Math.PI) > window) continue;
            double d = p.Y * ct - p.X * st - bestRho;
            if (Math.Abs(d) > 1.5) continue;
            onLine.Add((p.X, p.Y, p.Weight));
            columns[p.X] = true;
        }
        int covered = columns.Count(c => c);
        // A horizon crosses most of the picture, as one strong line.
        if (covered < w * 0.5 || onLine.Count < w * 0.4) return null;
        double mean = onLine.Average(p => (double)p.Weight);
        if (mean < 0.2) return null;
        // Least-squares slope of the supporting pixels (weighted by edge strength).
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y, wt) in onLine)
        {
            sw += wt; sx += wt * x; sy += wt * y; sxx += wt * x * x; sxy += wt * x * y;
        }
        double denom = sw * sxx - sx * sx;
        double slope = Math.Abs(denom) > 1e-9 ? (sw * sxy - sx * sy) / denom : Math.Tan(theta);
        // y grows downward: a horizon rising to the right has a negative slope here.
        return -Math.Atan(slope) * 180 / Math.PI;
    }

    // MARK: Saliency

    /// <summary>
    /// The union of the salient regions, normalised (0…1, y down), or null when nothing stands out: the spectral
    /// residual (Hou and Zhang 2007) of a 64 × 64 copy, thresholded at three times its mean.
    /// </summary>
    public static CGRectLike? SalientRect(RGBAImage image)
    {
        const int n = 64;
        var pre = PixelSampling.ResamplePremultiplied(image, n, n);
        var channels = new float[3][];
        for (int c = 0; c < 3; c++) channels[c] = new float[n * n];
        for (int i = 0; i < n * n; i++)
        {
            float r = pre[i * 4] / 255, g = pre[i * 4 + 1] / 255, b = pre[i * 4 + 2] / 255;
            channels[0][i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            channels[1][i] = r - g;
            channels[2][i] = b - (r + g) / 2;
        }
        var map = new float[n * n];
        foreach (var channel in channels)
        {
            var s = SpectralResidual(channel, n);
            for (int i = 0; i < map.Length; i++) map[i] += s[i];
        }
        map = Blur(map, n, n, 2.5);
        float max = map.Max(), meanValue = map.Average();
        if (!(max > 0) || max < meanValue * 2) return null;
        float threshold = Math.Min(meanValue * 3, max * 0.6f);
        // Connected regions above the threshold; the strong ones together are the salient part.
        var label = new int[n * n];
        var regions = new List<(int MinX, int MinY, int MaxX, int MaxY, double Mass, int Area)>();
        var stack = new Stack<int>();
        for (int i = 0; i < n * n; i++)
        {
            if (map[i] < threshold || label[i] != 0) continue;
            int id = regions.Count + 1, minX = n, minY = n, maxX = -1, maxY = -1, area = 0;
            double mass = 0;
            label[i] = id;
            stack.Push(i);
            while (stack.Count > 0)
            {
                int j = stack.Pop(), x = j % n, y = j / n;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                mass += map[j] - threshold;
                area++;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                        int k = yy * n + xx;
                        if (label[k] != 0 || map[k] < threshold) continue;
                        label[k] = id;
                        stack.Push(k);
                    }
                }
            }
            regions.Add((minX, minY, maxX, maxY, mass, area));
        }
        if (regions.Count == 0) return null;
        double strongest = regions.Max(r => r.Mass);
        CGRectLike? union = null;
        foreach (var r in regions.Where(r => r.Mass >= strongest * 0.2 && r.Area >= 2))
            union = Rects.Union(union, new CGRectLike(r.MinX, r.MinY, r.MaxX + 1, r.MaxY + 1));
        if (union is not { } u) return null;
        return new CGRectLike(u.MinX / n, u.MinY / n, u.MaxX / n, u.MaxY / n);
    }

    /// <summary>|IFFT(exp(R + iP))|² of one channel (R: log amplitude minus its 3 × 3 mean, P: phase).</summary>
    private static float[] SpectralResidual(float[] channel, int n)
    {
        var re = new double[n * n];
        var im = new double[n * n];
        for (int i = 0; i < re.Length; i++) re[i] = channel[i];
        Fft2(re, im, n, inverse: false);
        var logAmp = new double[n * n];
        var phase = new double[n * n];
        for (int i = 0; i < re.Length; i++)
        {
            logAmp[i] = Math.Log(Math.Sqrt(re[i] * re[i] + im[i] * im[i]) + 1e-9);
            phase[i] = Math.Atan2(im[i], re[i]);
        }
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                double avg = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        avg += logAmp[((y + dy + n) % n) * n + (x + dx + n) % n];
                double residual = logAmp[y * n + x] - avg / 9;
                double e = Math.Exp(residual);
                re[y * n + x] = e * Math.Cos(phase[y * n + x]);
                im[y * n + x] = e * Math.Sin(phase[y * n + x]);
            }
        }
        Fft2(re, im, n, inverse: true);
        var s = new float[n * n];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(re[i] * re[i] + im[i] * im[i]);
        return s;
    }

    /// <summary>In-place 2-D FFT of an n × n complex array (n a power of two); the inverse is scaled by 1/n².</summary>
    private static void Fft2(double[] re, double[] im, int n, bool inverse)
    {
        var rowRe = new double[n];
        var rowIm = new double[n];
        for (int y = 0; y < n; y++)
        {
            Array.Copy(re, y * n, rowRe, 0, n);
            Array.Copy(im, y * n, rowIm, 0, n);
            Fft(rowRe, rowIm, inverse);
            Array.Copy(rowRe, 0, re, y * n, n);
            Array.Copy(rowIm, 0, im, y * n, n);
        }
        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++) { rowRe[y] = re[y * n + x]; rowIm[y] = im[y * n + x]; }
            Fft(rowRe, rowIm, inverse);
            for (int y = 0; y < n; y++) { re[y * n + x] = rowRe[y]; im[y * n + x] = rowIm[y]; }
        }
        if (inverse)
        {
            double k = 1.0 / (n * n);
            for (int i = 0; i < re.Length; i++) { re[i] *= k; im[i] *= k; }
        }
    }

    /// <summary>Iterative radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }

    // MARK: Documents

    /// <summary>A straight edge in normal form: x cos θ + y sin θ = ρ (θ in radians, 0…π).</summary>
    private readonly record struct Line(double Theta, double Rho, float Votes);

    /// <summary>
    /// The document, sign, whiteboard or screen: corners in the working copy's pixels scaled to the image, and how well
    /// its sides are supported (0…1). Null when no quadrilateral of four supported edges covers 8 % of the picture.
    /// </summary>
    public static (Vec2 TopLeft, Vec2 TopRight, Vec2 BottomRight, Vec2 BottomLeft, double Support)? DocumentQuad(RGBAImage image)
    {
        var (w, h) = PixelSampling.Fit(image.Width, image.Height, 512);
        if (w < 32 || h < 32) return null;
        var lum = Blur(Luminance(image, w, h), w, h, 1.2);
        var (gx, gy, m) = Sobel(lum, w, h);
        float threshold = Math.Max(0.12f, 0.3f * Percentile(m, 0.99));
        var edges = ThinEdges(gx, gy, m, w, h, threshold);
        var lines = HoughLines(edges, gx, gy, m, w, h);
        if (lines.Count < 4) return null;
        double minSide = Math.Min(w, h) * 0.12;
        var middle = new Vec2(w / 2.0, h / 2.0);
        (Vec2, Vec2, Vec2, Vec2, double, double)? best = null;
        for (int a = 0; a < lines.Count; a++)
        {
            for (int b = a + 1; b < lines.Count; b++)
            {
                // Two opposite sides: alike in angle, apart.
                if (AngleDifference(lines[a].Theta, lines[b].Theta) > 40 * Math.PI / 180) continue;
                if (!Apart(lines[a], lines[b], minSide, middle)) continue;
                for (int c = 0; c < lines.Count; c++)
                {
                    if (c == a || c == b) continue;
                    if (AngleDifference(lines[a].Theta, lines[c].Theta) < 50 * Math.PI / 180) continue;
                    for (int d = c + 1; d < lines.Count; d++)
                    {
                        if (d == a || d == b) continue;
                        if (AngleDifference(lines[c].Theta, lines[d].Theta) > 40 * Math.PI / 180) continue;
                        if (AngleDifference(lines[a].Theta, lines[d].Theta) < 50 * Math.PI / 180) continue;
                        if (!Apart(lines[c], lines[d], minSide, middle)) continue;
                        if (Corners(lines[a], lines[b], lines[c], lines[d], w, h) is not { } q) continue;
                        if (Judge(q, lum, gx, gy, edges, w, h) is not { } score) continue;
                        if (best is null || score.Score > best.Value.Item6)
                            best = (q.Item1, q.Item2, q.Item3, q.Item4, score.Support, score.Score);
                    }
                }
            }
        }
        if (best is not { } found) return null;
        double kx = (double)image.Width / w, ky = (double)image.Height / h;
        Vec2 S(Vec2 p) => new((p.X + 0.5) * kx, (p.Y + 0.5) * ky);
        return (S(found.Item1), S(found.Item2), S(found.Item3), S(found.Item4), found.Item5);
    }

    private static double AngleDifference(double a, double b)
    {
        double d = Math.Abs(a - b) % Math.PI;
        return Math.Min(d, Math.PI - d);
    }

    /// <summary>
    /// Whether two nearly parallel lines lie at least <paramref name="distance"/> apart, measured at
    /// <paramref name="centre"/> (the picture's middle): the difference of its signed distances to them, with the second
    /// line's normal turned to agree with the first's.
    /// </summary>
    private static bool Apart(Line p, Line q, double distance, Vec2 centre)
    {
        double dp = centre.X * Math.Cos(p.Theta) + centre.Y * Math.Sin(p.Theta) - p.Rho;
        double dq = centre.X * Math.Cos(q.Theta) + centre.Y * Math.Sin(q.Theta) - q.Rho;
        if (Math.Cos(p.Theta - q.Theta) < 0) dq = -dq;
        return Math.Abs(dp - dq) >= distance;
    }

    /// <summary>The strong straight edges: Hough peaks (1° × 1 px), each pixel voting only near its own edge direction.</summary>
    private static List<Line> HoughLines(bool[] edges, float[] gx, float[] gy, float[] m, int w, int h)
    {
        const int thetas = 180;
        int diag = (int)Math.Ceiling(Math.Sqrt(w * w + h * h));
        int rhos = 2 * diag + 1;
        var acc = new float[thetas * rhos];
        var cos = new double[thetas];
        var sin = new double[thetas];
        for (int t = 0; t < thetas; t++)
        {
            cos[t] = Math.Cos(t * Math.PI / 180);
            sin[t] = Math.Sin(t * Math.PI / 180);
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!edges[i]) continue;
                // The line's normal is the gradient's direction (mod 180°).
                double normal = Math.Atan2(gy[i], gx[i]) * 180 / Math.PI;
                if (normal < 0) normal += 180;
                if (normal >= 180) normal -= 180;
                for (int dt = -5; dt <= 5; dt++)
                {
                    int t = ((int)Math.Round(normal) + dt + thetas) % thetas;
                    double rho = x * cos[t] + y * sin[t];
                    acc[t * rhos + (int)Math.Round(rho) + diag] += m[i];
                }
            }
        }
        float top = acc.Max();
        if (!(top > 0)) return new List<Line>();
        // Peaks: local maxima over ±4° and ±6 px, at least 15 % of the strongest; the 14 strongest.
        var peaks = new List<Line>();
        for (int t = 0; t < thetas; t++)
        {
            for (int r = 0; r < rhos; r++)
            {
                float v = acc[t * rhos + r];
                if (v < top * 0.15f) continue;
                bool isMax = true;
                for (int dt = -4; dt <= 4 && isMax; dt++)
                {
                    int tt = t + dt, rr0 = r;
                    if (tt < 0 || tt >= thetas)
                    {
                        // θ wraps around with ρ mirrored.
                        tt = (tt + thetas) % thetas;
                        rr0 = 2 * diag - r;
                    }
                    for (int dr = -6; dr <= 6; dr++)
                    {
                        int rr = rr0 + dr;
                        if (rr < 0 || rr >= rhos || (dt == 0 && dr == 0)) continue;
                        float o = acc[tt * rhos + rr];
                        if (o > v || (o == v && (tt * rhos + rr) < t * rhos + r)) { isMax = false; break; }
                    }
                }
                if (isMax) peaks.Add(new Line(t * Math.PI / 180, r - diag, v));
            }
        }
        return peaks.OrderByDescending(p => p.Votes).Take(14).ToList();
    }

    private static Vec2? Intersect(Line p, Line q)
    {
        double a1 = Math.Cos(p.Theta), b1 = Math.Sin(p.Theta), a2 = Math.Cos(q.Theta), b2 = Math.Sin(q.Theta);
        double det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-6) return null;
        return new Vec2((p.Rho * b2 - q.Rho * b1) / det, (a1 * q.Rho - a2 * p.Rho) / det);
    }

    /// <summary>The corners of the quadrilateral two pairs of lines make, in order (top-left first), when it is a fair one.</summary>
    private static (Vec2, Vec2, Vec2, Vec2)? Corners(Line a, Line b, Line c, Line d, int w, int h)
    {
        var pts = new List<Vec2>();
        foreach (var (p, q) in new[] { (a, c), (a, d), (b, c), (b, d) })
        {
            if (Intersect(p, q) is not { } v) return null;
            if (v.X < -0.03 * w || v.Y < -0.03 * h || v.X > 1.03 * w || v.Y > 1.03 * h) return null;
            pts.Add(new Vec2(Math.Clamp(v.X, 0, w - 1), Math.Clamp(v.Y, 0, h - 1)));
        }
        // Order around the centre, then start at the corner nearest the top-left.
        var centre = new Vec2(pts.Average(p => p.X), pts.Average(p => p.Y));
        var ordered = pts.OrderBy(p => Math.Atan2(p.Y - centre.Y, p.X - centre.X)).ToList();
        int start = 0;
        for (int i = 1; i < 4; i++) if (ordered[i].X + ordered[i].Y < ordered[start].X + ordered[start].Y) start = i;
        var o = Enumerable.Range(0, 4).Select(i => ordered[(start + i) % 4]).ToArray();
        // With y down, atan2 order runs clockwise on screen: top-left, top-right, bottom-right, bottom-left.
        // Convex, sides of fair length, corners not too far from square (Vision allows 30° either way; a sheet in
        // perspective needs a little more).
        double area = 0;
        for (int i = 0; i < 4; i++)
        {
            Vec2 p0 = o[(i + 3) % 4], p1 = o[i], p2 = o[(i + 1) % 4];
            Vec2 u = p0 - p1, v = p2 - p1;
            double cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
            if (cross <= 0) return null;
            if (u.Length < Math.Min(w, h) * 0.12 || v.Length < Math.Min(w, h) * 0.12) return null;
            double angle = Math.Acos(Math.Clamp(Vec2.Dot(u, v) / (u.Length * v.Length), -1, 1)) * 180 / Math.PI;
            if (angle < 50 || angle > 130) return null;
            area += o[i].X * o[(i + 1) % 4].Y - o[(i + 1) % 4].X * o[i].Y;
        }
        if (Math.Abs(area) / 2 < w * h * 0.08) return null;
        return (o[0], o[1], o[2], o[3]);
    }

    /// <summary>
    /// How well a quadrilateral is drawn in the picture: every side has edge pixels along at least 60 % of its length,
    /// keeps one polarity (a checkerboard's lines change polarity at every square; a sheet's edge doesn't), and the
    /// inside differs in light from the band just outside. The score is the supported length.
    /// </summary>
    private static (double Score, double Support)? Judge((Vec2, Vec2, Vec2, Vec2) q, float[] lum, float[] gx, float[] gy, bool[] edges, int w, int h)
    {
        var corners = new[] { q.Item1, q.Item2, q.Item3, q.Item4 };
        var centre = new Vec2(corners.Average(p => p.X), corners.Average(p => p.Y));
        double supported = 0, weakest = 1;
        double inside = 0, outside = 0;
        int inCount = 0, outCount = 0;
        for (int s = 0; s < 4; s++)
        {
            Vec2 p0 = corners[s], p1 = corners[(s + 1) % 4];
            Vec2 dir = p1 - p0;
            double length = dir.Length;
            if (length < 1) return null;
            dir /= length;
            var normal = new Vec2(-dir.Y, dir.X);
            if (Vec2.Dot(centre - p0, normal) > 0) normal = -normal;  // pointing out of the quadrilateral
            int samples = (int)length, hits = 0, outward = 0, inward = 0;
            // Leave the corners out: another side's edge passes there.
            for (int k = (int)(samples * 0.05); k < samples * 0.95; k++)
            {
                var p = p0 + dir * k;
                bool hit = false;
                for (int o = -2; o <= 2 && !hit; o++)
                {
                    var e = p + normal * o;
                    int x = (int)Math.Round(e.X), y = (int)Math.Round(e.Y);
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int i = y * w + x;
                    if (!edges[i]) continue;
                    double gn = gx[i] * normal.X + gy[i] * normal.Y, gl = Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
                    if (Math.Abs(gn) < gl * 0.9) continue;  // the edge must run along the side
                    hit = true;
                    if (gn > 0) outward++; else inward++;
                }
                if (hit) hits++;
                var pin = p - normal * 4;
                var pout = p + normal * 4;
                int xi = (int)Math.Round(pin.X), yi = (int)Math.Round(pin.Y), xo = (int)Math.Round(pout.X), yo = (int)Math.Round(pout.Y);
                if (xi >= 0 && yi >= 0 && xi < w && yi < h) { inside += lum[yi * w + xi]; inCount++; }
                if (xo >= 0 && yo >= 0 && xo < w && yo < h) { outside += lum[yo * w + xo]; outCount++; }
            }
            int counted = Math.Max(1, (int)(samples * 0.95) - (int)(samples * 0.05));
            double fraction = (double)hits / counted;
            if (fraction < 0.6) return null;
            if (Math.Max(outward, inward) < 0.85 * (outward + inward)) return null;
            weakest = Math.Min(weakest, fraction);
            supported += hits;
        }
        if (inCount == 0 || outCount == 0) return null;
        if (Math.Abs(inside / inCount - outside / outCount) < 0.06) return null;
        return (supported, weakest);
    }

    // MARK: Perspective

    /// <summary>
    /// The size of the flattened rectangle: the shape of the real rectangle behind the quadrilateral (its width over
    /// height from a pinhole camera with the principal point at <paramref name="centre"/>; Zhang and He, "Whiteboard
    /// scanning and image enhancement", Digital Signal Processing 17(2), 2007), at least as large as the quadrilateral's
    /// longer edges, which is what Core Image's perspective correction gives on the Mac ("at least as big as the edges,
    /// not exactly them"). When the corners don't fit a camera, the longer of each pair of opposite edges.
    /// </summary>
    internal static (int Width, int Height) RectifiedSize(Vec2 tl, Vec2 tr, Vec2 br, Vec2 bl, Vec2 centre)
    {
        double w0 = Math.Max(Vec2.Distance(tl, tr), Vec2.Distance(bl, br)), h0 = Math.Max(Vec2.Distance(tl, bl), Vec2.Distance(tr, br));
        static int R(double v) => Math.Max(1, (int)Math.Round(v, MidpointRounding.AwayFromZero));
        if (Aspect(tl - centre, tr - centre, br - centre, bl - centre) is not { } a || !(a > 0.1 && a < 10)) return (R(w0), R(h0));
        return w0 / h0 > a ? (R(w0), R(w0 / a)) : (R(h0 * a), R(h0));
    }

    /// <summary>Width over height of the rectangle whose picture (corners centred on the principal point) is the quadrilateral.</summary>
    private static double? Aspect(Vec2 tl, Vec2 tr, Vec2 br, Vec2 bl)
    {
        var m1 = new Vec3(tl, 1);
        var m2 = new Vec3(tr, 1);
        var m3 = new Vec3(bl, 1);
        var m4 = new Vec3(br, 1);
        static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        double d2 = Vec3.Dot(Cross(m2, m4), m3), d3 = Vec3.Dot(Cross(m3, m4), m2);
        if (Math.Abs(d2) < 1e-12 || Math.Abs(d3) < 1e-12) return null;
        double k2 = Vec3.Dot(Cross(m1, m4), m3) / d2, k3 = Vec3.Dot(Cross(m1, m4), m2) / d3;
        var n2 = m2 * k2 - m1;
        var n3 = m3 * k3 - m1;
        double planar(Vec3 n) => n.X * n.X + n.Y * n.Y;
        double z = n2.Z * n3.Z;
        if (Math.Abs(z) < 1e-12) return planar(n3) > 0 ? Math.Sqrt(planar(n2) / planar(n3)) : null;  // no perspective: an affine view
        double f2 = -(n2.X * n3.X + n2.Y * n3.Y) / z;
        if (!(f2 > 0)) return planar(n3) > 0 ? Math.Sqrt(planar(n2) / planar(n3)) : null;
        double top = planar(n2) / f2 + n2.Z * n2.Z, side = planar(n3) / f2 + n3.Z * n3.Z;
        return side > 0 ? Math.Sqrt(top / side) : null;
    }

    /// <summary>The 3 × 3 homography (row-major) that takes the unit square's corners (0,0), (1,0), (1,1), (0,1) to <paramref name="p"/>.</summary>
    internal static double[] SquareToQuad(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
        // Heckbert, "Fundamentals of Texture Mapping and Image Warping" (1989), §2.2.3.
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y;
        double sx = p0.X - p1.X + p2.X - p3.X, sy = p0.Y - p1.Y + p2.Y - p3.Y;
        double g, hh;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
        {
            g = 0;
            hh = 0;
        }
        else
        {
            double det = dx1 * dy2 - dx2 * dy1;
            g = (sx * dy2 - dx2 * sy) / det;
            hh = (dx1 * sy - sx * dy1) / det;
        }
        double a = p1.X - p0.X + g * p1.X, b = p3.X - p0.X + hh * p3.X, c = p0.X;
        double d = p1.Y - p0.Y + g * p1.Y, e = p3.Y - p0.Y + hh * p3.Y, f = p0.Y;
        return [a, b, c, d, e, f, g, hh, 1];
    }

    /// <summary>
    /// The quadrilateral flattened to a <paramref name="outWidth"/> × <paramref name="outHeight"/> rectangle: every output
    /// pixel's centre mapped through the homography and sampled bilinearly, in the image's own (linear, premultiplied)
    /// values. Outside the picture is transparent.
    /// </summary>
    public static LinearImage Warp(ReadOnlySpan<float> rgba, int width, int height, Vec2 tl, Vec2 tr, Vec2 br, Vec2 bl, int outWidth, int outHeight)
    {
        var m = SquareToQuad(tl, tr, br, bl);
        var src = rgba.ToArray();
        var o = new float[(long)outWidth * outHeight * 4];
        Imaging.ForRows(outHeight, (long)outWidth * outHeight, y =>
        {
            double v = (y + 0.5) / outHeight;
            for (int x = 0; x < outWidth; x++)
            {
                double u = (x + 0.5) / outWidth;
                double z = m[6] * u + m[7] * v + m[8];
                double px = (m[0] * u + m[1] * v + m[2]) / z - 0.5, py = (m[3] * u + m[4] * v + m[5]) / z - 0.5;
                int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
                float fx = (float)(px - x0), fy = (float)(py - y0);
                var c = Vector4.Zero;
                for (int k = 0; k < 4; k++)
                {
                    int sx = x0 + (k & 1), sy = y0 + (k >> 1);
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                    float wgt = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
                    int s = (sy * width + sx) * 4;
                    c += new Vector4(src[s], src[s + 1], src[s + 2], src[s + 3]) * wgt;
                }
                int d = (y * outWidth + x) * 4;
                o[d] = c.X; o[d + 1] = c.Y; o[d + 2] = c.Z; o[d + 3] = c.W;
            }
        });
        return new LinearImage(outWidth, outHeight, o);
    }
}
