// Ported from Packages/GlimmerKit/Tests/GlimmerHealTests/HealTimingTests.swift
#if GLIMMER_HEAL
using System.Diagnostics;
using Xunit.Abstractions;

namespace Glimmer.Heal.Tests;

/// <summary>A fact that runs only when the environment variable GLIMMER_HEAL_TIMING is set, as in the original.</summary>
public sealed class TimingFactAttribute : FactAttribute
{
    public TimingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GLIMMER_HEAL_TIMING") == null)
            Skip = "Timings for docs and limits; run with GLIMMER_HEAL_TIMING=1 and a Release build.";
    }
}

/// <summary>Timings for docs and limits; run with GLIMMER_HEAL_TIMING=1 and -c Release.</summary>
public class HealTimingTests
{
    readonly ITestOutputHelper output;
    public HealTimingTests(ITestOutputHelper output) => this.output = output;

    [TimingFact]
    public void TextureFillBySize()
    {
        foreach (int across in new[] { 30, 60, 120, 200, 250 })
        {
            int pad = Heal.Padding(across);
            int w = across + 2 * pad, h = w;
            var p = new float[w * h * 4];
            Array.Fill(p, 1f);
            for (int i = 0; i < w * h; i++)
            {
                uint v = HealTestHelpers.Hash(i % w / 3, i / w / 3);
                for (int c = 0; c < 3; c++) p[i * 4 + c] = (float)((v >> (c * 3)) % 256) / 255;
            }
            var hole = new bool[w * h];
            for (int i = 0; i < hole.Length; i++)
            {
                int x = i % w - w / 2, y = i / w - h / 2;
                hole[i] = x * x + y * y <= across * across / 4;
            }
            foreach (var method in new[] { Heal.Method.Smooth, Heal.Method.Texture })
            {
                var sw = Stopwatch.StartNew();
                Heal.Fill(p, w, h, hole, across, method);
                string line = $"TIMING {method} across {across} window {w}: {sw.Elapsed.TotalSeconds:F2} s";
                output.WriteLine(line);
                Console.WriteLine(line);
            }
        }
    }
}
#endif
