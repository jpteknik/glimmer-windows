// Ported from Packages/GlimmerKit/Sources/GlimmerHeal/FastMarchingFill.swift
#if GLIMMER_HEAL
using System.Numerics;

namespace Glimmer.Heal;

/// <summary>
/// The smooth fill: fast marching, after A. Telea, "An Image Inpainting Technique Based on
/// the Fast Marching Method" (Journal of Graphics Tools, 2004). The hole is filled from its
/// edge inwards in the order of distance to the edge; each pixel is a weighted average of
/// the already-known pixels around it, each extended along its own gradient.
/// </summary>
internal static class FastMarchingFill
{
    /// <summary>How far around a pixel known pixels are averaged.</summary>
    internal const int Radius = 5;

    const byte KnownFlag = 0, Band = 1, Inside = 2;

    internal static float[] Run(float[] pixels, int w, int h, bool[] hole, Func<bool> isCancelled)
    {
        var output = (float[])pixels.Clone();
        var flag = new byte[hole.Length];
        for (int i = 0; i < flag.Length; i++) flag[i] = hole[i] ? Inside : KnownFlag;
        // Distance to the hole's edge: 0 on it, counted negative outside as the paper does.
        var t = new float[w * h];
        float[] outside = Heal.Distance(hole, w, h);
        for (int i = 0; i < t.Length; i++) t[i] = hole[i] ? 1e6f : -Math.Max(0f, outside[i] - 1);

        var heap = new MinHeap();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (hole[i]) continue;
                if ((x > 0 && hole[i - 1]) || (x < w - 1 && hole[i + 1]) || (y > 0 && hole[i - w]) || (y < h - 1 && hole[i + w]))
                {
                    flag[i] = Band;
                    t[i] = 0;
                    heap.Push(0, i);
                }
            }
        }

        // One quadrant of the upwind solution of |∇T| = 1.
        float Solve(int x1, int y1, int x2, int y2)
        {
            float s = 1e6f;
            bool in1 = x1 >= 0 && x1 < w && y1 >= 0 && y1 < h, in2 = x2 >= 0 && x2 < w && y2 >= 0 && y2 < h;
            int i1 = y1 * w + x1, i2 = y2 * w + x2;
            if (in1 && flag[i1] == KnownFlag)
            {
                if (in2 && flag[i2] == KnownFlag)
                {
                    float a = t[i1], b = t[i2];
                    float r = MathF.Sqrt(2 - (a - b) * (a - b));
                    float v = (a + b - r) / 2;
                    if (v >= a && v >= b)
                    {
                        s = v;
                    }
                    else
                    {
                        v += r;
                        if (v >= a && v >= b) s = v;
                    }
                }
                else
                {
                    s = 1 + t[i1];
                }
            }
            else if (in2 && flag[i2] == KnownFlag)
            {
                s = 1 + t[i2];
            }
            return s;
        }

        // A central difference of channel `c` of `data` (interleaved with `stride` floats a
        // pixel) over the two neighbours that aren't inside the hole; -1 for "no neighbour".
        float Slope(float[] data, int stride, int c, int i, int lower, int upper)
        {
            int lo = lower >= 0 && flag[lower] != Inside ? lower : -1;
            int hi = upper >= 0 && flag[upper] != Inside ? upper : -1;
            if (lo >= 0 && hi >= 0) return (data[hi * stride + c] - data[lo * stride + c]) / 2;
            if (lo >= 0) return data[i * stride + c] - data[lo * stride + c];
            if (hi >= 0) return data[hi * stride + c] - data[i * stride + c];
            return 0;
        }

        void Inpaint(int x, int y)
        {
            int i = y * w + x;
            float gtx = Slope(t, 1, 0, i, x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1);
            float gty = Slope(t, 1, 0, i, y > 0 ? i - w : -1, y < h - 1 ? i + w : -1);
            Vector4 sum = Vector4.Zero;
            float weights = 0;
            Vector4 plain = Vector4.Zero;
            float plainCount = 0;
            var lowest = new Vector4(float.PositiveInfinity);
            var highest = new Vector4(float.NegativeInfinity);
            for (int ky = Math.Max(0, y - Radius); ky <= Math.Min(h - 1, y + Radius); ky++)
            {
                for (int kx = Math.Max(0, x - Radius); kx <= Math.Min(w - 1, x + Radius); kx++)
                {
                    int k = ky * w + kx;
                    if (flag[k] == Inside) continue;
                    float rx = (float)(x - kx), ry = (float)(y - ky);
                    float length2 = rx * rx + ry * ry;
                    if (!(length2 > 0 && length2 <= (float)(Radius * Radius))) continue;
                    var value = new Vector4(output[k * 4], output[k * 4 + 1], output[k * 4 + 2], output[k * 4 + 3]);
                    plain += value;
                    plainCount += 1;
                    lowest = Vector4.Min(lowest, value);
                    highest = Vector4.Max(highest, value);
                    float direction = MathF.Abs(rx * gtx + ry * gty) / MathF.Sqrt(length2);
                    if (direction < 1e-6f) direction = 1e-6f;
                    float weight = direction * (1 / length2) * (1 / (1 + MathF.Abs(t[k] - t[i])));
                    // A pixel of the picture carries its value over along its own gradient. A
                    // pixel that was itself filled in is taken as it is: its gradient is only
                    // an echo of the others', and feeding it back makes the fill run away.
                    Vector4 extended = value;
                    if (!hole[k])
                    {
                        int left = kx > 0 ? k - 1 : -1, right = kx < w - 1 ? k + 1 : -1;
                        int up = ky > 0 ? k - w : -1, down = ky < h - 1 ? k + w : -1;
                        extended.X += Slope(output, 4, 0, k, left, right) * rx + Slope(output, 4, 0, k, up, down) * ry;
                        extended.Y += Slope(output, 4, 1, k, left, right) * rx + Slope(output, 4, 1, k, up, down) * ry;
                        extended.Z += Slope(output, 4, 2, k, left, right) * rx + Slope(output, 4, 2, k, up, down) * ry;
                        extended.W += Slope(output, 4, 3, k, left, right) * rx + Slope(output, 4, 3, k, up, down) * ry;
                    }
                    sum += extended * weight;
                    weights += weight;
                }
            }
            Vector4 result = weights > 0 ? sum / weights : (plainCount > 0 ? plain / plainCount : Vector4.Zero);
            // Never brighter or darker than the pixels it was made from.
            if (plainCount > 0) result = Vector4.Max(lowest, Vector4.Min(highest, result));
            output[i * 4] = result.X; output[i * 4 + 1] = result.Y; output[i * 4 + 2] = result.Z; output[i * 4 + 3] = result.W;
        }

        int steps = 0;
        while (heap.TryPop(out _, out int i))
        {
            steps++;
            if (steps % 4096 == 0 && isCancelled()) throw new OperationCanceledException();
            flag[i] = KnownFlag;
            int x = i % w, y = i / w;
            for (int dir = 0; dir < 4; dir++)
            {
                int nx = dir == 0 ? x - 1 : dir == 2 ? x + 1 : x;
                int ny = dir == 1 ? y - 1 : dir == 3 ? y + 1 : y;
                if (!(nx >= 0 && nx < w && ny >= 0 && ny < h)) continue;
                int n = ny * w + nx;
                if (flag[n] == KnownFlag) continue;
                float distance = Math.Min(Math.Min(Solve(nx - 1, ny, nx, ny - 1), Solve(nx + 1, ny, nx, ny - 1)),
                                          Math.Min(Solve(nx - 1, ny, nx, ny + 1), Solve(nx + 1, ny, nx, ny + 1)));
                if (flag[n] == Inside)
                {
                    t[n] = distance;
                    // Still "inside" while it is worked out, so that no neighbour's gradient
                    // reads the value this pixel is about to lose.
                    Inpaint(nx, ny);
                    flag[n] = Band;
                    heap.Push(distance, n);
                }
                else if (distance < t[n] && hole[n])
                {
                    t[n] = distance;
                    heap.Push(distance, n);
                }
            }
        }
        return output;
    }
}

/// <summary>
/// A binary heap of (distance, pixel), smallest distance first; equal distances come out
/// in pixel order, so the fill is the same every time.
/// </summary>
internal sealed class MinHeap
{
    float[] keys = new float[256];
    int[] values = new int[256];
    int count;

    static bool Less(float ak, int av, float bk, int bv) => ak < bk || (ak == bk && av < bv);

    void Swap(int a, int b)
    {
        (keys[a], keys[b]) = (keys[b], keys[a]);
        (values[a], values[b]) = (values[b], values[a]);
    }

    public void Push(float key, int value)
    {
        if (count == keys.Length)
        {
            var nk = new float[count * 2];
            var nv = new int[count * 2];
            Array.Copy(keys, nk, count);
            Array.Copy(values, nv, count);
            keys = nk;
            values = nv;
        }
        keys[count] = key;
        values[count] = value;
        int i = count++;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (!Less(keys[i], values[i], keys[parent], values[parent])) break;
            Swap(i, parent);
            i = parent;
        }
    }

    public bool TryPop(out float key, out int value)
    {
        if (count == 0)
        {
            key = 0;
            value = 0;
            return false;
        }
        key = keys[0];
        value = values[0];
        count--;
        if (count > 0)
        {
            keys[0] = keys[count];
            values[0] = values[count];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1;
                int m = i;
                if (l < count && Less(keys[l], values[l], keys[m], values[m])) m = l;
                if (r < count && Less(keys[r], values[r], keys[m], values[m])) m = r;
                if (m == i) break;
                Swap(i, m);
                i = m;
            }
        }
        return true;
    }
}
#endif
