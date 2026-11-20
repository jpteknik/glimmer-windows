// Ported from Packages/GlimmerKit/Tests/GlimmerHealTests/HealSourceRulesTests.swift
using System.Runtime.CompilerServices;
using System.Text;

namespace Glimmer.Heal.Tests;

/// <summary>
/// The rules at the top of Heal.cs, as far as source text can show them: the project's
/// code (comments aside) calls nothing random, makes no smaller or resampled copy of the
/// image, transforms no patch, and references nothing beyond the base library. docs/PATENTS.md
/// in the original says why.
/// </summary>
public class HealSourceRulesTests
{
    static readonly (string Rule, string[] Words)[] Banned =
    {
        ("1: nothing randomised", new[] { "random", "arc4", "drand", "shuffle", "RandomNumberGenerator", "Guid" }),
        ("2: no reduced-size copy, no pyramid",
         new[] { "pyramid", "downsample", "downscale", "resample", "resize", "interpolat", "mipmap", "lanczos", "bilinear", "bicubic",
                 "SkiaSharp", "System.Drawing", "Bitmap", "SKImage" }),
        ("3: no transformed patches, no blending of patches",
         new[] { "rotat", "mirror", "flip", "transpose", "Matrix3x2", "Matrix4x4", "Quaternion", "sin(", "cos(", "atan", "warp", "scaled" }),
        ("3b: no gradient-domain seam", new[] { "poisson", "laplacian", "conjugate", "gaussSeidel", "jacobi" }),
        ("5: no differential-equation solve", new[] { "biharmonic", "cahn", "hilliard", "navier" }),
    };

    /// <summary>The repository root: the folder with Glimmer.slnx, found from the test's output folder (or, when that sits outside the repository, from this file's own place).</summary>
    static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) ?? "" })
        {
            for (var dir = start.Length == 0 ? null : new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Glimmer.slnx"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Glimmer.slnx not found above " + AppContext.BaseDirectory);
    }

    static string ProjectFolder() => Path.Combine(RepositoryRoot(), "src", "Glimmer.Heal");

    static List<(string Name, string Code)> Sources() =>
        Directory.GetFiles(ProjectFolder(), "*.cs", SearchOption.TopDirectoryOnly)
                 .OrderBy(p => p, StringComparer.Ordinal)
                 .Select(p => (Path.GetFileName(p), StripComments(File.ReadAllText(p, Encoding.UTF8))))
                 .ToList();

    /// <summary>The source without <c>//</c> comments and string literals.</summary>
    internal static string StripComments(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var sb = new StringBuilder();
            bool inString = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inString && c == '\\') { i++; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                sb.Append(c);
            }
            result.Add(sb.ToString());
        }
        return string.Join("\n", result);
    }

    [Fact]
    public void TheProjectHasItsSourceFiles()
    {
        Assert.Equal(new[] { "ExemplarFill.cs", "FastMarchingFill.cs", "Heal.cs" }, Sources().Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryFileSaysWhereItWasPortedFromAndIsWrappedInTheFeatureSwitch()
    {
        foreach (var path in Directory.GetFiles(ProjectFolder(), "*.cs"))
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.StartsWith("// Ported from Packages/GlimmerKit/Sources/GlimmerHeal/", text);
            Assert.Contains("\n#if GLIMMER_HEAL", text);
            Assert.EndsWith("#endif", text.TrimEnd());
        }
    }

    static List<string> Violations(string name, string code)
    {
        var found = new List<string>();
        var lower = code.ToLowerInvariant();
        foreach (var (rule, words) in Banned)
            foreach (var word in words)
                if (lower.Contains(word.ToLowerInvariant()))
                    found.Add($"{name} contains “{word}”, against rule {rule}");
        return found;
    }

    [Fact]
    public void NothingBannedIsCalled()
    {
        var found = new List<string>();
        foreach (var (name, code) in Sources()) found.AddRange(Violations(name, code));
        Assert.True(found.Count == 0, string.Join("\n", found));
    }

    /// <summary>A check that has never been seen to say no proves nothing: plant one violation of each rule.</summary>
    [Fact]
    public void TheCheckFindsPlantedViolations()
    {
        Assert.NotEmpty(Violations("x", "var r = new System.Random();"));
        Assert.NotEmpty(Violations("x", "var big = Resize(image);"));
        Assert.NotEmpty(Violations("x", "var a = Math.Sin(angle);"));
        Assert.NotEmpty(Violations("x", "SolvePoisson(g);"));
        Assert.NotEmpty(Violations("x", "SolveBiharmonic(g);"));
        Assert.Empty(Violations("x", "int best = Math.Min(a, b);"));
    }

    [Fact]
    public void OnlyTheBaseLibraryIsUsed()
    {
        foreach (var (name, code) in Sources())
        {
            var usings = code.Split('\n').Where(l => l.StartsWith("using ", StringComparison.Ordinal)).ToList();
            foreach (var u in usings)
            {
                var ns = u.Substring("using ".Length).TrimEnd(';', ' ');
                Assert.True(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal), $"{name}: {u}");
            }
        }
        // And the project file references no package and no other project.
        var project = File.ReadAllText(Path.Combine(ProjectFolder(), "Glimmer.Heal.csproj"), Encoding.UTF8);
        Assert.DoesNotContain("PackageReference", project);
        Assert.DoesNotContain("ProjectReference", project);
        Assert.DoesNotContain("Reference Include", project);
    }

    [Fact]
    public void APatchIsOnlyEverCopiedPixelForPixel()
    {
        // The one place the texture fill writes pixels: a plain copy from the same offset in the source patch.
        var code = Sources().First(s => s.Name == "ExemplarFill.cs").Code;
        Assert.Contains("int s = source + dy * w + dx;", code);
        Assert.Contains("outPixels[q * 4 + k] = outPixels[s * 4 + k];", code);
        Assert.Equal(2, code.Split("outPixels[q * 4 + k] =").Length);
    }

    [Fact]
    public void TheCheckItselfSeesThroughComments()
    {
        Assert.Equal("int a = 1; \nvar b = ;", StripComments("int a = 1; // random\nvar b = \"random\";"));
        Assert.Equal("var c = ;", StripComments("var c = \"a \\\" random\";"));
    }
}
