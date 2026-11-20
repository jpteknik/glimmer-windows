// Ported from Packages/GlimmerKit/Sources/GlimmerHeal/ExemplarFill.swift
#if GLIMMER_HEAL
namespace Glimmer.Heal;

/// <summary>
/// The texture fill: exemplar-based, after A. Criminisi, P. Pérez and K. Toyama, "Region
/// Filling and Object Removal by Exemplar-Based Image Inpainting" (IEEE Transactions on
/// Image Processing, 2004). The patch on the fill front with the highest priority is filled
/// from the best-matching whole patch of the surrounding image; then the next.
///
/// The priority is the paper's, unchanged: P(p) = C(p) · D(p), with the confidence C(p) the
/// mean confidence over the patch and the data term D(p) = |∇I⊥ · n| / α at the front pixel
/// (α = 255). The paper leaves two things to whoever writes it, and they are done the plain
/// way here: the image gradient "at" a front pixel, which has no value yet, is the strongest
/// gradient among the patch's known pixels; and equal priorities go to the higher
/// confidence, then to the first pixel in reading order.
///
/// The search is the paper's too: every candidate patch in the window is compared, by the
/// sum of squared differences over the pixels already known. See the rules in Heal.cs.
/// </summary>
internal static class ExemplarFill
{
    static readonly int Half = Heal.PatchSide / 2;

    internal static float[] Run(float[] pixels, int w, int h, bool[] hole, Func<bool> isCancelled)
    {
        var outPixels = (float[])pixels.Clone();
        var match = new MatchImage(pixels, w, h);
        var filled = new bool[hole.Length];
        var confidence = new float[hole.Length];
        for (int i = 0; i < hole.Length; i++)
        {
            filled[i] = !hole[i];
            confidence[i] = hole[i] ? 0f : 1f;
        }
        int side = Heal.PatchSide;
        float area = (float)(side * side);
        int half = Half;

        // Patches that may be copied from: wholly inside the window, no hole pixel, nothing transparent.
        var candidateList = new List<int>();
        {
            var blocked = new int[(w + 1) * (h + 1)];  // summed-area table
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (hole[i] || pixels[i * 4 + 3] <= 0) row++;
                    blocked[(y + 1) * (w + 1) + x + 1] = blocked[y * (w + 1) + x + 1] + row;
                }
            }
            if (w >= side && h >= side)
            {
                for (int y = half; y < h - half; y++)
                {
                    for (int x = half; x < w - half; x++)
                    {
                        int x0 = x - half, y0 = y - half, x1 = x + half + 1, y1 = y + half + 1;
                        int n = blocked[y1 * (w + 1) + x1] - blocked[y0 * (w + 1) + x1] - blocked[y1 * (w + 1) + x0] + blocked[y0 * (w + 1) + x0];
                        if (n == 0) candidateList.Add(y * w + x);
                    }
                }
            }
        }
        if (candidateList.Count == 0)
        {
            throw new Heal.Refusal("nothing_to_sample",
                                   $"There is no untouched {side} × {side} patch of image around the selection to fill it from.");
        }
        int[] candidates = candidateList.ToArray();

        bool IsFront(int x, int y)
        {
            int i = y * w + x;
            if (filled[i]) return false;
            return (x > 0 && filled[i - 1]) || (x < w - 1 && filled[i + 1]) || (y > 0 && filled[i - w]) || (y < h - 1 && filled[i + w]);
        }

        float Known(int x, int y, int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            return nx >= 0 && nx < w && ny >= 0 && ny < h && filled[ny * w + nx] ? 1f : 0f;
        }

        // C(p) and P(p) for the front pixel at (x, y).
        (float Confidence, float Priority) Priority(int x, int y)
        {
            float c = 0;
            float strongest = -1, ix = 0, iy = 0;
            for (int py = Math.Max(0, y - half); py <= Math.Min(h - 1, y + half); py++)
            {
                for (int px = Math.Max(0, x - half); px <= Math.Min(w - 1, x + half); px++)
                {
                    int q = py * w + px;
                    if (!filled[q]) continue;
                    c += confidence[q];
                    if (!(px > 0 && px < w - 1 && py > 0 && py < h - 1
                          && filled[q - 1] && filled[q + 1] && filled[q - w] && filled[q + w])) continue;
                    float gx = (float)(match.Luma(q + 1) - match.Luma(q - 1)) / 2, gy = (float)(match.Luma(q + w) - match.Luma(q - w)) / 2;
                    float strength = gx * gx + gy * gy;
                    if (strength > strongest) { strongest = strength; ix = gx; iy = gy; }
                }
            }
            c /= area;
            // The front's normal: the gradient of "is filled" around p.
            float nx = Known(x, y, 1, 0) - Known(x, y, -1, 0), ny = Known(x, y, 0, 1) - Known(x, y, 0, -1);
            float length = MathF.Sqrt(nx * nx + ny * ny);
            if (length > 0) { nx /= length; ny /= length; }
            // The isophote is the gradient turned a quarter: (-iy, ix).
            float data = MathF.Abs(-iy * nx + ix * ny) / 255;
            return (c, c * data);
        }

        var onFront = new bool[w * h];
        var frontPriority = new float[w * h];
        var frontConfidence = new float[w * h];
        var front = new List<int>();
        int remaining = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!hole[y * w + x]) continue;
                remaining++;
                if (IsFront(x, y))
                {
                    int i = y * w + x;
                    onFront[i] = true;
                    (frontConfidence[i], frontPriority[i]) = Priority(x, y);
                    front.Add(i);
                }
            }
        }

        var offsets = new int[side * side];  // of the target's known pixels, from its centre
        var values = new byte[side * side * 3];
        int chunks = Math.Max(1, Math.Min(Environment.ProcessorCount, candidates.Length / 2048));
        int steps = 0;
        while (remaining > 0)
        {
            steps++;
            if (steps % 16 == 0 && isCancelled()) throw new OperationCanceledException();
            // The front pixel with the highest priority.
            int best = -1;
            int live = 0;
            for (int n = 0; n < front.Count; n++)
            {
                int i = front[n];
                if (!onFront[i]) continue;
                live++;
                if (best < 0 || frontPriority[i] > frontPriority[best]
                    || (frontPriority[i] == frontPriority[best] && (frontConfidence[i] > frontConfidence[best]
                        || (frontConfidence[i] == frontConfidence[best] && i < best))))
                {
                    best = i;
                }
            }
            if (best < 0) break;  // a hole pixel with no known pixel anywhere near can't happen once the front exists
            if (front.Count > 4 * live + 64) front.RemoveAll(i => !onFront[i]);
            int x = best % w, y = best / w;

            // What is known of the target patch.
            int known = 0;
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (!(px >= 0 && px < w && py >= 0 && py < h && filled[py * w + px])) continue;
                    int q = py * w + px;
                    offsets[known] = dy * w + dx;
                    values[known * 3] = match.Bytes[q * 4]; values[known * 3 + 1] = match.Bytes[q * 4 + 1]; values[known * 3 + 2] = match.Bytes[q * 4 + 2];
                    known++;
                }
            }

            // Every candidate, compared in full. Each core takes a stretch of the list; the
            // smallest difference wins, the earliest candidate among equals.
            int source = Search(candidates, chunks, match.Bytes, offsets, values, known);

            // Copy the pixels the target lacks, as they are.
            float c = frontConfidence[best];
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (!(px >= 0 && px < w && py >= 0 && py < h)) continue;
                    int q = py * w + px;
                    if (filled[q]) continue;
                    int s = source + dy * w + dx;
                    for (int k = 0; k < 4; k++)
                    {
                        outPixels[q * 4 + k] = outPixels[s * 4 + k];
                        match.Bytes[q * 4 + k] = match.Bytes[s * 4 + k];
                    }
                    filled[q] = true;
                    confidence[q] = c;
                    onFront[q] = false;
                    remaining--;
                }
            }
            // The front and its priorities change only near what was just filled.
            int reach = 2 * half + 1;
            for (int py = Math.Max(0, y - reach); py <= Math.Min(h - 1, y + reach); py++)
            {
                for (int px = Math.Max(0, x - reach); px <= Math.Min(w - 1, x + reach); px++)
                {
                    int q = py * w + px;
                    if (filled[q]) continue;
                    if (IsFront(px, py))
                    {
                        if (!onFront[q]) { onFront[q] = true; front.Add(q); }
                        (frontConfidence[q], frontPriority[q]) = Priority(px, py);
                    }
                }
            }
        }
        return outPixels;
    }

    /// <summary>The centre of the candidate patch that differs least from the target's known pixels.</summary>
    static unsafe int Search(int[] candidates, int chunks, byte[] bytes, int[] offsets, byte[] values, int known)
    {
        var leastOf = new int[chunks];
        var atOf = new int[chunks];
        int per = (candidates.Length + chunks - 1) / chunks;
        int total = candidates.Length;

        fixed (int* cand = candidates)
        fixed (byte* img = bytes)
        fixed (int* offs = offsets)
        fixed (byte* vals = values)
        {
            int* candP = cand, offsP = offs;
            byte* imgP = img, valsP = vals;

            void Work(int chunk)
            {
                int least = int.MaxValue, at = -1;
                int end = Math.Min(total, (chunk + 1) * per);
                int n = chunk * per;
                while (n < end)
                {
                    int centre = candP[n];
                    int sum = 0;
                    int k = 0;
                    while (k < known)
                    {
                        int q = (centre + offsP[k]) * 4;
                        int dr = (int)imgP[q] - (int)valsP[k * 3];
                        int dg = (int)imgP[q + 1] - (int)valsP[k * 3 + 1];
                        int db = (int)imgP[q + 2] - (int)valsP[k * 3 + 2];
                        sum += dr * dr + dg * dg + db * db;
                        if (sum >= least) break;
                        k++;
                    }
                    if (sum < least) { least = sum; at = candP[n]; }
                    n++;
                }
                leastOf[chunk] = least;
                atOf[chunk] = at;
            }

            if (chunks == 1) Work(0);
            else Parallel.For(0, chunks, Work);
        }

        int bestLeast = int.MaxValue, bestAt = -1;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            if (atOf[chunk] >= 0 && leastOf[chunk] < bestLeast)
            {
                bestLeast = leastOf[chunk];
                bestAt = atOf[chunk];
            }
        }
        return bestAt;
    }
}
#endif
