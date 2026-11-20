// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/RequirementsTests.swift
// (not ported: the scan of the app's Swift source for operations that load a model, and the check that
// THIRD_PARTY_NOTICES.md equals the register's tables; that file is not in this repository yet)
using System.Text.Json.Nodes;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

/// The register of what Glimmer may need installed (SIBLINGS.md "Det som kan behöva installeras"):
/// every entry is complete, every operation that can answer `model_missing` has entries.
public class RequirementsTests
{
    /// Licenses that allow commercial use and aren't copyleft: what Glimmer may fetch or bundle.
    private static readonly HashSet<string> Permissive = new() { "MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "ISC", "Zlib" };

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);

    [Fact]
    public void EveryEntryHasSourceVersionChecksumAndLicense()
    {
        foreach (var r in Requirement.All)
        {
            Assert.True(r.Id.Length > 0 && r.Name.Length > 0 && r.NeededBy.Count > 0, $"{r.Id}: id, name and neededBy");
            Assert.StartsWith("https://", r.Source.Url);
            Assert.True(r.Source.Publisher.Length > 0 && r.Source.Version.Length > 0, $"{r.Id}: publisher and version");
            Assert.True(r.Source.ChecksumsFrom.Length > 0, $"{r.Id}: where the checksums were read");
            Assert.True(r.License.Spdx.Length > 0 && r.License.Url.StartsWith("https://"), $"{r.Id}: license and its text");
            Assert.True(r.Location.Length > 0 && r.Removal.Length > 0, $"{r.Id}: where it goes and how it is removed");
            Assert.True(r.Source.Sha256 is not null || r.Source.Sha256Note is not null, $"{r.Id}: a SHA-256, or why there is none");
            if (r.Source.Sha256 is { } sha) Assert.True(sha.Length == 64 && IsHex(sha), $"{r.Id}: SHA-256 form");
            switch (r.Install)
            {
                case RequirementInstall.InApp:
                    Assert.Equal(40, r.Source.Commit?.Length);
                    Assert.NotEmpty(r.Files);
                    foreach (var f in r.Files)
                    {
                        Assert.True(f.Size > 0, $"{r.Id} {f.Path}: size");
                        Assert.True(f.Sha256?.Length == 64 || f.GitBlob?.Length == 40, $"{r.Id} {f.Path}: a SHA-256 or a git blob id");
                    }
                    Assert.Equal(r.SizeBytes, r.Files.Sum(f => f.Size));
                    Assert.Contains(r.License.Spdx, Permissive);   // what Glimmer fetches is never GPL or non-commercial
                    break;
                case RequirementInstall.ByUser byUser:
                    Assert.True(byUser.Steps.Count > 0 && byUser.Verify.Command.Length > 0 && byUser.Verify.MinVersion.Length > 0 && byUser.Verify.MinVersionFrom.Length > 0, $"{r.Id}: steps and verify");
                    Assert.True(byUser.Verify.TeamID is not null || byUser.Verify.Note is not null, $"{r.Id}: a Team ID, or why it isn't read");
                    foreach (var step in byUser.Steps)
                    {
                        Assert.True(!step.Contains("| sh") && !step.Contains("|sh") && !step.Contains("sudo"), $"{r.Id}: no piped installer, no sudo");
                        Assert.True(!step.Contains("xattr") && !step.Contains("spctl --master-disable"), $"{r.Id}: Gatekeeper and quarantine stay");
                    }
                    break;
                case RequirementInstall.Bundled:
                    Assert.Contains(r.License.Spdx, Permissive);   // what Glimmer bundles is never GPL or non-commercial
                    break;
            }
        }
        Assert.Equal(Requirement.All.Count, Requirement.All.Select(r => r.Id).Distinct().Count());
    }

    /// <summary>
    /// The Windows register: the Mac's depth and SAM entries replaced by ONNX conversions under the same ids, the models
    /// Vision stands for on the Mac added, the Mac-only entries kept apart with their reasons. (Until the on-device models
    /// were chosen this test held the register to the Mac's exactly.)
    /// </summary>
    [Fact]
    public void TheRegisterIsTheWindowsRegister()
    {
        Assert.Equal("4472b7362082ad9968fee890ca0f1e5aca36b93d", Requirement.DepthAnything.Source.Commit);
        Assert.Equal("814a066640debee5a91e70aa401fb8e17e030503", Requirement.SegmentAnything.Source.Commit);
        Assert.Single(Requirement.DepthAnything.Files);
        Assert.Equal(4, Requirement.SegmentAnything.Files.Count);
        Assert.Equal(49_642_442, Requirement.DepthAnything.SizeBytes);
        Assert.Equal(61_966_687, Requirement.SegmentAnything.SizeBytes);
        Assert.Equal("2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04", Requirement.DepthAnything.Files[0].Sha256);
        Assert.Equal("ba8dadc63152bb7d554e322aa90ae272a0b8b0cd", Requirement.ImageLabels.Files[1].GitBlob);
        Assert.Equal(new[] { "depth-anything-v2-small", "sam2.1-tiny", "isnet-general-use", "rtdetr-v2-r18", "modnet", "mobilenetv4-conv-small", "ollama", "realesrgan-general-x4v3-onnx" },
                     Requirement.All.Select(r => r.Id));
        Assert.Equal(new[] { "depth-anything-v2-small", "sam2.1-tiny", "isnet-general-use", "rtdetr-v2-r18", "modnet", "mobilenetv4-conv-small", "ollama" },
                     Requirement.Installable.Select(r => r.Id));
        // Every file of every download has a SHA-256 (computed from a download, agreeing with the source's own list).
        Assert.All(Requirement.All.SelectMany(r => r.Files), f => Assert.Equal(64, f.Sha256?.Length));
        // The Mac's own entries stay in the code, with why they don't apply.
        Assert.Equal(new[] { "sparkle", "realesrgan-general-x4v3" }, Requirement.MacOnly.Select(r => r.Id));
        Assert.All(Requirement.MacOnly, r => Assert.False(string.IsNullOrEmpty(r.NotApplicable)));
        Assert.All(Requirement.All, r => Assert.Null(r.NotApplicable));
        Assert.Null(Requirement.Named("sparkle"));
    }

    [Fact]
    public void EveryOnDeviceModelIsAnInAppEntry()
    {
        foreach (var model in OnDeviceModels.AllCases)
        {
            Assert.IsType<RequirementInstall.InApp>(model.Requirement().Install);
            Assert.Equal(Requirement.Named(model.Requirement().Id), model.Requirement());
            Assert.Equal(model, OnDeviceModels.With(model.Requirement().Id));
            Assert.NotEmpty(model.Packages());
        }
        Assert.Equal(new[] { "onnx" }, OnDeviceModel.Depth.Packages());
        Assert.Equal(new[] { "onnx" }, OnDeviceModel.SegmentAnything.Packages());
        Assert.Equal(new[] { "onnx", "config.json" }, OnDeviceModel.ImageLabels.Packages());
        Assert.Equal("onnx-community/sam2.1-hiera-tiny-ONNX", OnDeviceModel.SegmentAnything.Repository());
        Assert.Null(OnDeviceModels.With("nope"));
        Assert.Equal("49.6 MB", OnDeviceModel.Depth.ApproximateSize());
        Assert.Equal("62.0 MB", OnDeviceModel.SegmentAnything.ApproximateSize());
        Assert.Equal("88.2 MB", OnDeviceModel.Subject.ApproximateSize());
    }

    /// An operation that can answer `model_missing` has entries, and each entry says it is needed by that operation.
    [Fact]
    public void TheOperationsThatNeedAModelAreCrossChecked()
    {
        foreach (var (command, ids) in Requirement.ModelsForCommands)
        {
            foreach (var id in ids) Assert.Contains($"run_command {command}", Requirement.Named(id)!.NeededBy);
            Assert.Equal(ids, Requirement.ModelsFor("run_command", command));
        }
        foreach (var (op, ids) in Requirement.ModelsForOps)
        {
            foreach (var id in ids)
            {
                Assert.NotNull(Requirement.Named(id));
                Assert.Contains(op, Requirement.Named(id)!.NeededBy);
            }
            Assert.Equal(ids, Requirement.ModelsFor(op, null));
        }
        Assert.Equal(new[] { "depth-anything-v2-small", "sam2.1-tiny" }, Requirement.ModelsFor("select_sky", null));
        // On Windows the subject and the people need models too (Vision does them on the Mac).
        Assert.Equal(new[] { "isnet-general-use" }, Requirement.ModelsFor("remove_background", null));
        Assert.Equal(new[] { "isnet-general-use" }, Requirement.ModelsFor("run_command", "select.subject"));
        Assert.Equal(new[] { "rtdetr-v2-r18", "modnet" }, Requirement.ModelsFor("select_people", null));
        Assert.Null(Requirement.ModelsFor("crop", null));
        Assert.Null(Requirement.ModelsFor("run_command", "layer.new"));
        // Every command named is one an agent may run.
        foreach (var command in Requirement.ModelsForCommands.Keys)
            Assert.Contains(AgentPlanner.RunnableCommands, d => d.Id.RawValue == command);
    }

    [Fact]
    public void TheJsonHasTheSeriesShape()
    {
        var item = Requirement.DepthAnything.ToJson(false, null);
        foreach (var key in new[] { "id", "name", "kind", "neededBy", "required", "installed", "installedVersion", "verified", "sizeBytes", "license", "source", "install", "where", "remove", "modes", "debugOnly" })
            Assert.True(item.ContainsKey(key), key);
        Assert.Equal("in_app", (string)item["install"]!["how"]!);
        Assert.Equal(Requirement.DepthAnything.Source.Commit, (string)item["source"]!["commit"]!);
        Assert.Equal("model", (string)item["kind"]!);
        Assert.Single(item["files"]!.AsArray());
        Assert.Equal("2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04", (string)item["files"]![0]!["sha256"]!);
        Assert.Null(item["files"]![0]!["gitBlob"]);
        var labels = Requirement.ImageLabels.ToJson(false, null);
        Assert.Equal("ba8dadc63152bb7d554e322aa90ae272a0b8b0cd", (string)labels["files"]![1]!["gitBlob"]!);
        var ollama = Requirement.Ollama.ToJson(null, null);
        // An entry checked with verify has source.minVersion, never source.version (SIBLINGS.md 16878d0).
        Assert.Equal("0.6.4", (string)ollama["source"]!["minVersion"]!);
        Assert.Null(ollama["source"]!["version"]);
        Assert.Null(item["source"]!["minVersion"]);
        foreach (var r in Requirement.Installable)
        {
            var check = r.ToJson(null, null)["install"]!["check"];
            Assert.True(check?["command"] is not null && check["expect"] is not null, $"{r.Id}: install.check");
        }
        Assert.Equal("by_user", (string)ollama["install"]!["how"]!);
        Assert.Equal("0.6.4", (string)ollama["verify"]!["minVersion"]!);
        Assert.Null(ollama["verify"]!["teamID"]);
        Assert.Null(ollama["files"]);
        Assert.Equal("bundled", (string)Requirement.RealESRGANOnnx.ToJson(null, null)["install"]!["how"]!);
        var items = new JsonArray(Requirement.Installable.Select(r => (JsonNode?)r.ToJson(null, null)).ToArray());
        Assert.Equal(7, items.Count);
        Assert.DoesNotContain(Requirement.Installable, r => r.Install is RequirementInstall.Bundled);
    }

    [Fact]
    public void TheVerifiedFieldIsTrueFalseOrNull()
    {
        Assert.False((bool)Requirement.DepthAnything.ToJson(true, null, JsonValue.Create(false))["verified"]!);
        Assert.Null(Requirement.DepthAnything.ToJson(false, null)["verified"]);
        Assert.Null(ModelVerification.NotInstalled.Json());
        Assert.False((bool)ModelVerification.Unverified.Json()!);
        Assert.True((bool)ModelVerification.Verified.Json()!);
    }

    [Fact]
    public void TheGuideListsEveryInstallableEntry()
    {
        var guide = AgentAPI.Guide(AgentAPI.PreferredPort, "0", new FakeCatalog());
        Assert.Contains("## What Glimmer may need installed", guide);
        foreach (var r in Requirement.Installable) Assert.Contains($"`{r.Id}`", guide);
        foreach (var r in Requirement.All.Where(r => r.Install is RequirementInstall.Bundled)) Assert.DoesNotContain($"(`{r.Id}`)", guide);
        Assert.True(guide.Contains("/api/v1/requirements") && guide.Contains("/api/v1/settings/open") && guide.Contains("model_missing"));
        Assert.Contains("- Size: 49,642,442 bytes.", guide);
        Assert.Contains("  - `onnx/model_fp16.onnx`, 49642442 bytes, SHA-256 `2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04`", guide);
        Assert.Contains("Today that is `select_sky` (and `run_command` with `select.sky`), which needs `depth-anything-v2-small` and `sam2.1-tiny`; `remove_background`, which needs `isnet-general-use`;", guide);
        Assert.Contains("`select_people` (and `run_command` with `select.people`), which needs `rtdetr-v2-r18` and `modnet`.", guide);
        Assert.Contains("  1. Download OllamaSetup.exe of version 0.40.16", guide);
        Assert.Contains("Signer: null (not read: not installed on the test computer;", guide);
    }

    [Fact]
    public void TheNoticesTablesComeFromTheRegister()
    {
        var tables = Requirement.NoticesTables();
        Assert.Contains("## Bundled in the app", tables);
        foreach (var r in Requirement.All) Assert.Contains(r.Name, tables);
        Assert.Contains("| [Real-ESRGAN realesr-general-x4v3 (ONNX)](https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0) | v0.2.5.0, realesr-general-x4v3.pth (4,885,111 bytes) | [BSD-3-Clause](https://github.com/xinntao/Real-ESRGAN/blob/v0.2.5.0/LICENSE) | On-device upscaling |", tables);
        Assert.Contains("[onnx-community/sam2.1-hiera-tiny-ONNX](https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX) at `814a066640debee5a91e70aa401fb8e17e030503`", tables);
        // The Mac's own entries are not the Windows app's.
        Assert.DoesNotContain("Sparkle", tables);
        Assert.DoesNotContain("Core ML", tables);
        Assert.Contains("| [Ollama](https://github.com/ollama/ollama) | 0.6.4 or later (0.40.16 checked) |", tables);
    }

    [Fact]
    public void RequirementsCompareByContent()
    {
        var copy = Requirement.Ollama with { NeededBy = Requirement.Ollama.NeededBy.ToArray() };
        Assert.Equal(Requirement.Ollama, copy);
        Assert.NotEqual(Requirement.Ollama, Requirement.DepthAnything);
        Assert.NotEqual(Requirement.Ollama, copy with { Use = "other" });
        Assert.Equal(Requirement.Ollama.Install, copy.Install);
        Assert.Equal(new RequirementInstall.Bundled(), Requirement.Sparkle.Install);
    }
}
