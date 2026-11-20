// Ported from Packages/GlimmerKit/Tests/GlimmerRenderTests/CanvasRendererTests.swift and FilterEngineTests.swift (the
// timings their PerformanceTests print), extended to the hot paths here (run with GLIMMER_BENCH=1). Not assertions:
// numbers for the report and for tuning.
using System.Diagnostics;
using System.Numerics;
using Glimmer.Core;
using Xunit.Abstractions;

namespace Glimmer.Render.Tests;

public class Benchmarks(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("GLIMMER_BENCH") == "1";

    /// <summary>Runs <paramref name="body"/> after a warm-up and reports the fastest and the median run (the machine may be shared).</summary>
    private double Time(string name, int runs, Action body)
    {
        body();  // warm up
        var times = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            long start = Stopwatch.GetTimestamp();
            body();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(times);
        output.WriteLine($"{name}: min {times[0]:F2} ms, median {times[runs / 2]:F2} ms");
        return times[0];
    }

    [Fact]
    public void HalfConversions()
    {
        if (!Enabled) return;
        var halves = new Half[1024];
        var floats = new float[1024];
        var rng = new Random(2);
        for (int i = 0; i < halves.Length; i++) halves[i] = (Half)rng.NextDouble();
        const int rows = 100000;
        Time($"Half → float, {rows} rows of 1024 values", 3, () =>
        {
            for (int r = 0; r < rows; r++) System.Numerics.Tensors.TensorPrimitives.ConvertToSingle(halves, floats);
        });
        Time($"float → Half, {rows} rows of 1024 values", 3, () =>
        {
            for (int r = 0; r < rows; r++) System.Numerics.Tensors.TensorPrimitives.ConvertToHalf(floats, halves);
        });
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++) table[i] = (float)BitConverter.UInt16BitsToHalf((ushort)i);
        var bits = System.Runtime.InteropServices.MemoryMarshal.Cast<Half, ushort>(halves.AsSpan()).ToArray();
        float sink = 0;
        Time($"Half → float one at a time (cast), {rows / 10} rows of 1024 values", 3, () =>
        {
            float acc = 0;
            for (int r = 0; r < rows / 10; r++)
                for (int i = 0; i < 1024; i++) acc += (float)halves[i];
            sink += acc;
        });
        Time($"Half → float one at a time (table), {rows / 10} rows of 1024 values", 3, () =>
        {
            float acc = 0;
            for (int r = 0; r < rows / 10; r++)
                for (int i = 0; i < 1024; i++) acc += table[bits[i]];
            sink += acc;
        });
        output.WriteLine($"(sink {sink})");
    }

    [Fact]
    public void OneTileTwentyLayers()
    {
        if (!Enabled) return;
        var tiles = Enumerable.Range(0, 4).Select(i => TestSupport.TextureTile(new Vector4(i * 0.1f, 0.2f, 0.3f, 0.5f))).ToArray();
        var layers = new List<Layer>();
        for (int i = 0; i < 20; i++)
            layers.Add(new Layer($"L{i}", new RasterContent(TestSupport.Grid((new TileCoord(0, 0), tiles[i % 4]))),
                                 blendMode: BlendMode.PixelModes[i % 18]));
        var state = new DocumentState(256, 256, layers: layers);
        Time("one tile, 20 layers (modes as the performance test), single thread", 20, () =>
            new DocumentCompositor().Update(state, null, [new TileCoord(0, 0)]));
        foreach (var mode in new[] { BlendMode.Normal, BlendMode.Multiply, BlendMode.Hue })
        {
            var same = state with { Layers = [.. state.Layers.Select(l => l with { BlendMode = mode })] };
            Time($"one tile, 20 layers, all {mode}", 20, () => new DocumentCompositor().Update(same, null, [new TileCoord(0, 0)]));
        }
        output.WriteLine("Processor count: " + Environment.ProcessorCount);
    }

    /// <summary>The canvas pass over an 8k document: 100 %, 25 % (detail level 2), rotated, and a fitted view.</summary>
    [Fact]
    public void CanvasViewports()
    {
        if (!Enabled) return;
        const int size = 8192;
        var rng = new Random(4);
        Tile Photo(float shade, float alpha)
        {
            var px = new Half[TileGeometry.FloatCount];
            for (int i = 0; i < px.Length; i += 4)
            {
                float v = shade * (0.5f + 0.5f * (float)rng.NextDouble());
                px[i] = (Half)(v * alpha); px[i + 1] = (Half)(v * 0.8f * alpha); px[i + 2] = (Half)(v * 0.6f * alpha); px[i + 3] = (Half)alpha;
            }
            return Tile.FromHalf(px);
        }
        foreach (float alpha in new[] { 1f, 0.6f })
        {
            var tiles = Enumerable.Range(0, 4).Select(i => Photo(0.3f + i * 0.15f, alpha)).ToArray();
            var b = new TileGrid.Builder();
            var all = new PixelRect(0, 0, size, size).TileCoords;
            for (int n = 0; n < all.Length; n++) b[all[n]] = tiles[n % 4];
            var state = new DocumentState(size, size, layers: [new Layer("photo", new RasterContent(b.ToImmutable()))]);
            foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
            {
                var views = new (string Name, ViewTransform View)[]
                {
                    ("100 %", new ViewTransform(scale: 1, translation: new Vec2(-3000, -3000))),
                    ("25 %", new ViewTransform(scale: 0.25, translation: new Vec2(-200, -100))),
                    ("100 % rotated 30°", new ViewTransform(scale: 1, rotation: Math.PI / 6, translation: new Vec2(-2000, -4000))),
                    ("fitted (17 %), rotated 10°", ViewTransform.Fitting(new Vec2(size, size), new Vec2(w, h), rotation: Math.PI / 18)),
                };
                foreach (var (name, view) in views)
                {
                    var frame = new CanvasFrame(view, new Vec2(w, h), 1, state.Size, new Vector4(0.1f, 0.1f, 0.12f, 1));
                    var visible = view.VisibleDocumentRect(new Vec2(w, h)).Intersection(state.Bounds);
                    var coords = visible.TileCoords;
                    var compositor = new DocumentCompositor();
                    compositor.Update(state, null, coords, detailLevel: DocumentCompositor.DetailLevel(view.Scale));
                    var renderer = new CanvasRenderer();
                    var target = new CanvasTarget(new uint[w * h], w, h);
                    Time($"canvas {w}×{h}, {name}, alpha {alpha} ({coords.Length} tiles)", 20,
                         () => renderer.Encode(frame, target, coords, compositor.Texture));
                    var single = new CanvasRenderer { MaxThreads = 1 };
                    double one = Time($"  same on one thread", 5, () => single.Encode(frame, target, coords, compositor.Texture));
                    output.WriteLine($"  → {one * 1e6 / (w * h):F1} ns per drawable pixel on one core; {Environment.ProcessorCount} logical cores");
                }
            }
        }
    }

    [Fact]
    public void ManyTilesInParallel()
    {
        if (!Enabled) return;
        var tiles = Enumerable.Range(0, 4).Select(i => TestSupport.TextureTile(new Vector4(i * 0.1f, 0.2f, 0.3f, 0.5f))).ToArray();
        var bounds = new PixelRect(0, 0, 8192, 8192);
        var coords = bounds.TileCoords;
        foreach (int layerCount in new[] { 1, 20 })
        {
            var layers = new List<Layer>();
            for (int i = 0; i < layerCount; i++)
            {
                var b = new TileGrid.Builder();
                for (int n = 0; n < coords.Length; n++) b[coords[n]] = tiles[(n + i) % 4];
                layers.Add(new Layer($"L{i}", new RasterContent(b.ToImmutable()), blendMode: BlendMode.PixelModes[i % 18]));
            }
            var state = new DocumentState(8192, 8192, layers: layers);
            int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
            var sw = Stopwatch.StartNew();
            new DocumentCompositor().Update(state, null, coords);
            output.WriteLine($"{coords.Length} tiles × {layerCount} layers: {sw.Elapsed.TotalMilliseconds:F0} ms, " +
                             $"GC gen0 {GC.CollectionCount(0) - gen0}, gen2 {GC.CollectionCount(2) - gen2}");
        }
    }

    [Fact]
    public void BlendRowsAgainstThePerPixelFormula()
    {
        if (!Enabled) return;
        var rng = new Random(1);
        var src = new Vector4[256];
        var dst0 = new Vector4[256];
        var k = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float a = (float)rng.NextDouble(), b = (float)rng.NextDouble();
            src[i] = new Vector4((float)rng.NextDouble() * a, (float)rng.NextDouble() * a, (float)rng.NextDouble() * a, a);
            dst0[i] = new Vector4((float)rng.NextDouble() * b, (float)rng.NextDouble() * b, (float)rng.NextDouble() * b, b);
            k[i] = 1;
        }
        var dst = new Vector4[256];
        foreach (int mode in new[] { 0, 1, 3, 9, 14 })
        {
            const int rows = 20000;
            Time($"mode {mode}: per-pixel BlendOver, {rows} rows of 256", 3, () =>
            {
                for (int r = 0; r < rows; r++)
                {
                    dst0.CopyTo(dst, 0);
                    for (int x = 0; x < 256; x++) dst[x] = CompositeShaders.BlendOver(dst[x], src[x] * k[x], mode, false);
                }
            });
            Time($"mode {mode}: BlendRows, {rows} rows of 256", 3, () =>
            {
                for (int r = 0; r < rows; r++)
                {
                    dst0.CopyTo(dst, 0);
                    BlendRows.Blend(dst, src, k, mode, false);
                }
            });
        }
    }
}
