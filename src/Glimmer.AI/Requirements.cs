// Ported from Packages/GlimmerKit/Sources/GlimmerAI/Requirements.swift
using System.Globalization;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

public enum RequirementKind { Model, Library, Plugin, Tool }

public sealed record RequirementLicense(
    /// The SPDX identifier.
    string Spdx,
    /// The license text at the source.
    string Url);

/// One file of a download, with what its source publishes about it.
public sealed record RequirementFile(
    /// The path inside the repository at `commit`.
    string Path,
    int Size,
    /// SHA-256 of the file, as the source lists it. Null when the source publishes none.
    string? Sha256,
    /// The git blob id (SHA-1 of "blob <size>\0" + the bytes) at `commit`, for a file the
    /// source keeps in git rather than in large-file storage, which lists no SHA-256.
    string? GitBlob);

public sealed record RequirementSource(
    string Publisher,
    string Url,
    string Version,
    /// The git commit everything is fetched at, for a repository.
    string? Commit,
    /// SHA-256 of the one file a person downloads (a by-user tool), or of what is bundled.
    string? Sha256,
    /// Why `Sha256` is null, when it is.
    string? Sha256Note,
    /// Where the checksums above were read, and when.
    string ChecksumsFrom);

/// How a by-user program is checked after it is installed: its signature and its version.
public sealed record RequirementVerify(
    string Command,
    /// The Developer ID team that signs it. Null until it has been read from a signature.
    string? TeamID,
    string? Note,
    string MinVersion,
    /// Where `MinVersion` comes from (the source's own documentation or changelog), and when it was read.
    string MinVersionFrom);

public abstract record RequirementInstall
{
    private RequirementInstall() { }

    /// Downloaded by Glimmer after the person clicks in its window; Glimmer checks every checksum.
    public sealed record InApp(string Where) : RequirementInstall;

    /// Installed by the person outside Glimmer, with these exact steps.
    public sealed record ByUser(IReadOnlyList<string> Steps, RequirementVerify Verify) : RequirementInstall
    {
        public bool Equals(ByUser? other) => other is not null && Steps.SequenceEqual(other.Steps) && Verify == other.Verify;
        public override int GetHashCode() => HashCode.Combine(Steps.Count, Verify);
    }

    /// In the app already; listed for its license only.
    public sealed record Bundled : RequirementInstall;
}

/// Everything Glimmer uses that it didn't write: what it downloads itself, what the person may
/// install beside it, and what is bundled. One register (the series' rule, SIBLINGS.md "Det som
/// kan behöva installeras"): Settings › Models, `GET /api/v1/requirements`, the agent guide's
/// "What Glimmer may need installed" and the tables in THIRD_PARTY_NOTICES.md are all made from it.
///
/// Checksums: each file's SHA-256 was computed from a download at the pinned commit and agrees with
/// the source's own published list (Hugging Face's large-file SHA-256, or the git blob id of a file
/// it keeps in git); the address and the date are in `ChecksumsFrom`.
///
/// This is the Windows register. The Mac's Core ML depth and SAM entries are replaced by ONNX
/// conversions of the same models, under the same ids, so `model_missing` names the same things on
/// both. Remove Background, Select Subject, Select People and smart layer names, which Vision does
/// on the Mac, need models of their own here. The Mac's Sparkle and its Core ML Real-ESRGAN don't
/// apply on Windows; they are kept in `MacOnly`, each with the reason, not in `All`.
public sealed record Requirement(
    string Id,
    string Name,
    RequirementKind Kind,
    /// The operations (`steps` ops) and features that need it.
    IReadOnlyList<string> NeededBy,
    bool Required,
    RequirementLicense License,
    RequirementSource Source,
    IReadOnlyList<RequirementFile> Files,
    /// The download size, or the size in the app.
    int? SizeBytes,
    /// Where it ends up on this computer.
    string Location,
    /// How it is removed.
    string Removal,
    RequirementInstall Install,
    /// What it does in Glimmer, for THIRD_PARTY_NOTICES.md and the guide.
    string Use,
    /// Why an entry of the Mac register doesn't apply on Windows; null for every entry that does.
    string? NotApplicable = null)
{
    public bool IsInstallable => Install is not RequirementInstall.Bundled;

    /// Glimmer has one mode: everything runs in the app on this machine (no server mode as Flimmer has).
    public IReadOnlyList<string> Modes => new[] { "local" };
    /// No entry is for Debug builds only.
    public bool DebugOnly => false;

    /// The command that shows the install went right, and what it should answer.
    public (string Command, string Expect)? Check
    {
        get
        {
            switch (Install)
            {
                case RequirementInstall.InApp:
                    return ("curl -s http://127.0.0.1:9001/api/v1/requirements",
                            $"the item with \"id\": \"{Id}\" has \"installed\": true and \"verified\": true");
                case RequirementInstall.ByUser byUser:
                    return ("ollama --version", $"a version of {byUser.Verify.MinVersion} or later (\"ollama version is 0.40.16\" for the release the steps name)");
                default:
                    return null;
            }
        }
    }

    // Structural equality that compares the lists by content.
    public bool Equals(Requirement? other) =>
        other is not null && Id == other.Id && Name == other.Name && Kind == other.Kind && NeededBy.SequenceEqual(other.NeededBy)
        && Required == other.Required && License == other.License && Source == other.Source && Files.SequenceEqual(other.Files)
        && SizeBytes == other.SizeBytes && Location == other.Location && Removal == other.Removal && Install.Equals(other.Install) && Use == other.Use
        && NotApplicable == other.NotApplicable;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, Required, Source, SizeBytes, Location);

    // MARK: The register

    public const string ModelsFolder = @"%LOCALAPPDATA%\Glimmer\Models";
    private const string HuggingFaceChecksums = "the Hugging Face file list of the repository at that commit (https://huggingface.co/api/models/<repository>/tree/<commit>), read 2026-11-19; each SHA-256 also computed from a download at that commit on 2026-11-19, and the two agree";
    private const string FilesNote = "Each file's SHA-256 is under files.";
    private const string ModelRemoval = "Glimmer › Settings › Models › Remove.";

    // Every on-device model: the smallest precision that gives the full-precision model's result on the sample pictures
    // (measured 2026-11-19, ONNX Runtime 1.31 on the CPU): depth float16 (mean difference 0.05–0.31 of 255; the 8-bit
    // one 1–9), SAM 8-bit (masks 99.6–100 % the same; float16 is the same but slower here), IS-Net float16 (the same
    // to 0.0002 as rembg's float32 file; the 8-bit copies leak, 63–88 % the same), RT-DETRv2 8-bit (person scores
    // within 0.03 of float16), MODNet float16 (98–100 %; the 8-bit one fails), MobileNetV4 float16 (the same labels as
    // float32; the 8-bit one answers "knee pad" to everything).

    public static readonly Requirement DepthAnything = new(
        Id: "depth-anything-v2-small", Name: "Depth Anything V2 Small (ONNX)", Kind: RequirementKind.Model,
        NeededBy: new[] { "select_sky", "run_command select.sky", "Select Sky", "Lens Blur (AI panel)" }, Required: false,
        License: new RequirementLicense("Apache-2.0", "https://huggingface.co/onnx-community/depth-anything-v2-small/blob/4472b7362082ad9968fee890ca0f1e5aca36b93d/README.md"),
        Source: new RequirementSource("ONNX Community, converted from depth-anything/Depth-Anything-V2-Small (Apache-2.0)",
                                      "https://huggingface.co/onnx-community/depth-anything-v2-small",
                                      "onnx/model_fp16.onnx (float16)", "4472b7362082ad9968fee890ca0f1e5aca36b93d",
                                      null, FilesNote, HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/model_fp16.onnx", 49_642_442, "2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04", null),
        },
        SizeBytes: 49_642_442, Location: ModelsFolder + @"\depth-anything-v2-small",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Lens Blur depth map, Select Sky");

    public static readonly Requirement SegmentAnything = new(
        Id: "sam2.1-tiny", Name: "SAM 2.1 Tiny (ONNX, 8-bit)", Kind: RequirementKind.Model,
        NeededBy: new[] { "select_sky", "run_command select.sky", "Magic Select", "Select Sky" }, Required: false,
        // The ONNX repository states no license of its own; the weights' is Meta's, on the model's own card.
        License: new RequirementLicense("Apache-2.0", "https://huggingface.co/facebook/sam2.1-hiera-tiny/blob/de431c4043854a71d8101e17995dfe596bf101a5/README.md"),
        Source: new RequirementSource("ONNX Community, converted from facebook/sam2.1-hiera-tiny (Apache-2.0)",
                                      "https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX",
                                      "vision_encoder_quantized and prompt_encoder_mask_decoder_quantized (8-bit)",
                                      "814a066640debee5a91e70aa401fb8e17e030503", null, FilesNote, HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/vision_encoder_quantized.onnx", 441_167, "8800fdd04b9045cb6060ba8962b83c78ce9bb0976da8f030afa073467a888524", null),
            new RequirementFile("onnx/vision_encoder_quantized.onnx_data", 52_573_088, "ea1f677596dc82cef0dc317d78135d892f4b26e20a4439f899aa58d84965b324", null),
            new RequirementFile("onnx/prompt_encoder_mask_decoder_quantized.onnx", 290_416, "a5f31dba5a7f03203dc3a130fdd7766f0fea952eb4aac1f55458549170eb51ff", null),
            new RequirementFile("onnx/prompt_encoder_mask_decoder_quantized.onnx_data", 8_662_016, "fdb3179a32388267a944a416c0f992c77a138b69a4bfd94f0bbf7089430d7489", null),
        },
        SizeBytes: 61_966_687, Location: ModelsFolder + @"\sam2.1-tiny",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Magic Select, Select Sky");

    public static readonly Requirement SubjectSegmentation = new(
        Id: "isnet-general-use", Name: "IS-Net general use (ONNX)", Kind: RequirementKind.Model,
        NeededBy: new[] { "remove_background", "select_subject", "run_command select.subject", "Remove Background", "Select Subject" }, Required: false,
        // IMG.LY publishes this conversion under MIT; the weights are Xuebin Qin et al.'s IS-Net general-use
        // (github.com/xuebinqin/DIS, Apache-2.0). Its output equals rembg's float32 file of the same weights
        // (SHA-256 60920e99…, rembg release v0.0.0) to 0.0002 on nine pictures.
        License: new RequirementLicense("MIT", "https://huggingface.co/imgly/isnet-general-onnx/blob/440dea96dd4a3b06bbbf5abec3e26569dd7ec49f/README.md"),
        Source: new RequirementSource("IMG.LY, converted from xuebinqin/DIS isnet-general-use (Apache-2.0)",
                                      "https://huggingface.co/imgly/isnet-general-onnx",
                                      "onnx/model_fp16.onnx (float16)", "440dea96dd4a3b06bbbf5abec3e26569dd7ec49f",
                                      null, FilesNote, HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/model_fp16.onnx", 88_152_708, "2eb4b5dda7ec41c617e59706e5aafa1f978c9a5f983d2518d9f0ae4d6eb04f20", null),
        },
        SizeBytes: 88_152_708, Location: ModelsFolder + @"\isnet-general-use",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Remove Background, Select Subject");

    public static readonly Requirement PersonDetection = new(
        Id: "rtdetr-v2-r18", Name: "RT-DETRv2 R18 (ONNX, 8-bit)", Kind: RequirementKind.Model,
        NeededBy: new[] { "select_people", "run_command select.people", "Select People" }, Required: false,
        License: new RequirementLicense("Apache-2.0", "https://huggingface.co/onnx-community/rtdetr_v2_r18vd-ONNX/blob/936f90b6a476c6da4dfe053fc521af55285976ba/README.md"),
        Source: new RequirementSource("ONNX Community, converted from PekingU/rtdetr_v2_r18vd (Apache-2.0)",
                                      "https://huggingface.co/onnx-community/rtdetr_v2_r18vd-ONNX",
                                      "onnx/model_int8.onnx (8-bit)", "936f90b6a476c6da4dfe053fc521af55285976ba",
                                      null, FilesNote, HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/model_int8.onnx", 20_991_219, "fed736d2593cf2ab099f665eeeb6d315d909783eea830f80a807fc1ac1c1b2ec", null),
        },
        SizeBytes: 20_991_219, Location: ModelsFolder + @"\rtdetr-v2-r18",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Select People (where the people are)");

    public static readonly Requirement PersonMatting = new(
        Id: "modnet", Name: "MODNet (ONNX)", Kind: RequirementKind.Model,
        NeededBy: new[] { "select_people", "run_command select.people", "Select People" }, Required: false,
        License: new RequirementLicense("Apache-2.0", "https://huggingface.co/Xenova/modnet/blob/fa2fa546052fba4c08921230a26cc69a333fca12/README.md"),
        Source: new RequirementSource("Xenova, converted from ZHKKKe/MODNet (Apache-2.0)", "https://huggingface.co/Xenova/modnet",
                                      "onnx/model_fp16.onnx (float16)", "fa2fa546052fba4c08921230a26cc69a333fca12",
                                      null, FilesNote, HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/model_fp16.onnx", 12_984_781, "25f165da9bfd30830a575f1f0490f1acd995975cb349bc02f3d79332e1fe5cf6", null),
        },
        SizeBytes: 12_984_781, Location: ModelsFolder + @"\modnet",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Select People (their outlines, hair included)");

    public static readonly Requirement ImageLabels = new(
        Id: "mobilenetv4-conv-small", Name: "MobileNetV4 Conv Small (ONNX)", Kind: RequirementKind.Model,
        NeededBy: new[] { "Smart layer names" }, Required: false,
        // The ONNX repository states no license of its own; the weights' is on timm's card of the same model.
        License: new RequirementLicense("Apache-2.0", "https://huggingface.co/timm/mobilenetv4_conv_small.e2400_r224_in1k/blob/331fb803779522b685cf942e15f914fb6741c1eb/README.md"),
        Source: new RequirementSource("ONNX Community, converted from timm/mobilenetv4_conv_small.e2400_r224_in1k (Apache-2.0)",
                                      "https://huggingface.co/onnx-community/mobilenetv4_conv_small.e2400_r224_in1k",
                                      "onnx/model_fp16.onnx (float16) and the ImageNet labels in config.json",
                                      "3ba07f12712fa58fd6b3d661f9909c9e332c5005", null, FilesNote + " config.json is kept in git; its git blob id is listed too.",
                                      HuggingFaceChecksums),
        Files: new[]
        {
            new RequirementFile("onnx/model_fp16.onnx", 7_563_434, "e1fabc0bc040d8b04ccbaf41354e2a987ea7de7f9188b881436466e191dcff57", null),
            new RequirementFile("config.json", 69_895, "ae99506e66f8e19bc73809e5b4c3ae31fc7c2bca53752dc7cb89c1e47831a335", "ba8dadc63152bb7d554e322aa90ae272a0b8b0cd"),
        },
        SizeBytes: 7_633_329, Location: ModelsFolder + @"\mobilenetv4-conv-small",
        Removal: ModelRemoval, Install: new RequirementInstall.InApp("Settings › Models"),
        Use: "Names for placed and pasted images");

    public static readonly Requirement Ollama = new(
        Id: "ollama", Name: "Ollama", Kind: RequirementKind.Tool,
        NeededBy: new[] { "The Assistant and text features, when Ollama is chosen in Settings › AI" }, Required: false,
        License: new RequirementLicense("MIT", "https://github.com/ollama/ollama/blob/v0.40.16/LICENSE"),
        Source: new RequirementSource("Ollama", "https://github.com/ollama/ollama", "0.40.16", null,
                                      "84db4f756ddc9e091142a85cdc0f7ae5db68bc9b3e075f114388b326f8ed912d",
                                      "The SHA-256 is of OllamaSetup.exe 0.40.16, the release the steps name; any version from minVersion on works.",
                                      "the release's asset list, OllamaSetup.exe digest (https://api.github.com/repos/ollama/ollama/releases/tags/v0.40.16), read 2026-11-19"),
        Files: Array.Empty<RequirementFile>(),
        SizeBytes: 1_857_663_944, Location: @"%LOCALAPPDATA%\Programs\Ollama, and the models it pulls in %USERPROFILE%\.ollama\models",
        Removal: @"Quit Ollama (in the notification area), uninstall it in Settings › Apps, and delete %USERPROFILE%\.ollama if its models should go too.",
        Install: new RequirementInstall.ByUser(new[]
        {
            "Download OllamaSetup.exe of version 0.40.16 from https://github.com/ollama/ollama/releases/tag/v0.40.16.",
            @"Check it: `(Get-FileHash $env:USERPROFILE\Downloads\OllamaSetup.exe -Algorithm SHA256).Hash` must print 84DB4F756DDC9E091142A85CDC0F7AE5DB68BC9B3E075F114388B326F8ED912D (PowerShell prints it in capitals).",
            "Run the installer. Windows checks its signature: if Windows warns that it cannot verify the publisher, stop; don't override the warning.",
            @"Check the signature and the version: `Get-AuthenticodeSignature $env:LOCALAPPDATA\Programs\Ollama\ollama.exe` must say Valid, and `ollama --version` must be 0.6.4 or later.",
            "Pull a model that can call tools, one the person chooses (`ollama pull <model>`; Ollama's library marks such models \"tools\").",
            "In Glimmer › Settings › AI, choose Ollama as the text service and pick the model.",
        }, new RequirementVerify(@"Get-AuthenticodeSignature $env:LOCALAPPDATA\Programs\Ollama\ollama.exe; ollama --version", null,
                                 "not read: not installed on the test computer; the signer is read from the signature after the user installs it",
                                 "0.6.4",
                                 "Glimmer reads a model's tools and vision from `capabilities` in /api/show, which Ollama added in commit e172f095ba4af2c98d7744ce4ffcf4cd3a8e123c (\"api: return model capabilities from the show endpoint (#10066)\", docs/api.md, 2025-04-01); the first release with it is v0.6.4 (https://github.com/ollama/ollama/releases/tag/v0.6.4, 2025-04-02). Read 2026-11-19.")),
        Use: "Text and the Assistant on a local model (optional)");

    public static readonly Requirement Sparkle = new(
        Id: "sparkle", Name: "Sparkle", Kind: RequirementKind.Library, NeededBy: new[] { "Glimmer › Check for Updates…" }, Required: true,
        License: new RequirementLicense("MIT", "https://github.com/sparkle-project/Sparkle/blob/2.10.1/LICENSE"),
        Source: new RequirementSource("Sparkle Project", "https://github.com/sparkle-project/Sparkle", "2.10.1",
                                      "ab7d71291238df6e0492143b120c9b696a35f603",
                                      "4c2e48a3e50ebcc2c88a58a4c7264a1a99c3c31857ce754357afb9cdb323022a", null,
                                      "the release's asset list, Sparkle-for-Swift-Package-Manager.zip digest (https://api.github.com/repos/sparkle-project/Sparkle/releases/tags/2.10.1), read 2026-11-19; the commit is the one Package.resolved pins"),
        Files: Array.Empty<RequirementFile>(), SizeBytes: null, Location: "Glimmer.app/Contents/Frameworks/Sparkle.framework",
        Removal: "Part of the app.", Install: new RequirementInstall.Bundled(), Use: "Software updates",
        NotApplicable: "Sparkle is a macOS framework; the Windows app is not built with it.");

    public static readonly Requirement RealESRGAN = new(
        Id: "realesrgan-general-x4v3", Name: "Real-ESRGAN realesr-general-x4v3 (Core ML)", Kind: RequirementKind.Model,
        NeededBy: new[] { "upscale", "Upscale (Real-ESRGAN on this computer)" }, Required: false,
        License: new RequirementLicense("BSD-3-Clause", "https://github.com/xinntao/Real-ESRGAN/blob/v0.2.5.0/LICENSE"),
        Source: new RequirementSource("Xintao Wang (xinntao)", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
                                      "v0.2.5.0, realesr-general-x4v3.pth (4,885,111 bytes)", null,
                                      "7a6088b2ee537938f380b9049fae2b85dad57b251d047ab8f5afd256417838ec",
                                      "The SHA-256 is of weights/weight.bin in Glimmer's own conversion (GlimmerAI/Resources/RealESRGAN.mlmodelc), which is in the repository. The original release publishes no digest for the .pth file. Converted for Glimmer on 2026-11-13 by scripts/convert-models/realesrgan.py with coremltools 9.0 and torch 2.15.0 (the model's own metadata says so).",
                                      "Glimmer's repository (our own file)"),
        Files: Array.Empty<RequirementFile>(), SizeBytes: 2_437_344, Location: "Glimmer.app (GlimmerKit_GlimmerAI.bundle/RealESRGAN.mlmodelc)",
        Removal: "Part of the app.", Install: new RequirementInstall.Bundled(), Use: "On-device upscaling",
        NotApplicable: "Core ML runs only on Apple's systems; on Windows the same weights are bundled as ONNX (realesrgan-general-x4v3-onnx).");

    public static readonly Requirement RealESRGANOnnx = new(
        Id: "realesrgan-general-x4v3-onnx", Name: "Real-ESRGAN realesr-general-x4v3 (ONNX)", Kind: RequirementKind.Model,
        NeededBy: new[] { "upscale", "Upscale (Real-ESRGAN on this computer)" }, Required: false,
        License: new RequirementLicense("BSD-3-Clause", "https://github.com/xinntao/Real-ESRGAN/blob/v0.2.5.0/LICENSE"),
        Source: new RequirementSource("Xintao Wang (xinntao)", "https://github.com/xinntao/Real-ESRGAN/releases/tag/v0.2.5.0",
                                      "v0.2.5.0, realesr-general-x4v3.pth (4,885,111 bytes)", null,
                                      "c3e12b6634671a5168d5a5133761d5416d452df249c58a7531cf1a324b2b3b01",
                                      "The SHA-256 is of Glimmer's own conversion (Glimmer.AI/OnDevice/Resources/RealESRGAN.onnx, embedded in Glimmer.AI.dll), which is in the repository: made on 2026-11-19 by OnDevice/Resources/realesrgan_onnx.py with torch 2.15.0 and onnx 1.23.2, weights stored as float16. The release publishes no digest for the .pth file; the one converted had SHA-256 8dc7edb9ac80ccdc30c3a5dca6616509367f05fbc184ad95b731f05bece96292.",
                                      "Glimmer's repository (our own file)"),
        Files: Array.Empty<RequirementFile>(), SizeBytes: 2_448_168, Location: "Glimmer.AI.dll (embedded resource Glimmer.AI.RealESRGAN.onnx)",
        Removal: "Part of the app.", Install: new RequirementInstall.Bundled(), Use: "On-device upscaling");

    /// The whole register: installable entries first.
    public static readonly IReadOnlyList<Requirement> All = new[]
    {
        DepthAnything, SegmentAnything, SubjectSegmentation, PersonDetection, PersonMatting, ImageLabels, Ollama, RealESRGANOnnx,
    };

    /// Entries of the Mac register that don't apply on Windows, kept with the reason (`NotApplicable`) so the two
    /// registers can be read side by side. Nothing here is shown, installed or listed in the notices.
    public static readonly IReadOnlyList<Requirement> MacOnly = new[] { Sparkle, RealESRGAN };

    /// What may need installing: the in-app downloads and the by-user tools.
    public static IReadOnlyList<Requirement> Installable => All.Where(r => r.IsInstallable).ToList();

    public static Requirement? Named(string id) => All.FirstOrDefault(r => r.Id == id);

    /// The `steps` operations that can answer `model_missing`, and the entries each needs. On the Mac only Select Sky
    /// needs a download; Vision does the subject and the people there.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ModelsForOps = new Dictionary<string, IReadOnlyList<string>>
    {
        ["select_sky"] = new[] { DepthAnything.Id, SegmentAnything.Id },
        ["remove_background"] = new[] { SubjectSegmentation.Id },
        ["select_subject"] = new[] { SubjectSegmentation.Id },
        ["select_people"] = new[] { PersonDetection.Id, PersonMatting.Id },
    };

    /// The same for `run_command` ids.
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ModelsForCommands = new Dictionary<string, IReadOnlyList<string>>
    {
        ["select.sky"] = new[] { DepthAnything.Id, SegmentAnything.Id },
        ["select.subject"] = new[] { SubjectSegmentation.Id },
        ["select.people"] = new[] { PersonDetection.Id, PersonMatting.Id },
    };

    /// What one step needs downloaded: by its op, or by its command for `run_command`.
    public static IReadOnlyList<string>? ModelsFor(string op, string? command)
    {
        if (ModelsForOps.TryGetValue(op, out var ids)) return ids;
        if (op == "run_command" && command is not null && ModelsForCommands.TryGetValue(command, out var byCommand)) return byCommand;
        return null;
    }

    // MARK: JSON, guide and notices

    /// One item of `GET /api/v1/requirements`. `installed` comes from the app (ModelStore).
    /// `verified`: for a model Glimmer downloads, whether a receipt vouches for its files (null when not installed).
    public JsonObject ToJson(bool? installed, string? installedVersion, JsonNode? verified = null)
    {
        var source = new JsonObject
        {
            ["publisher"] = Source.Publisher,
            ["url"] = Source.Url,
            ["sha256"] = Source.Sha256,
            ["checksumsFrom"] = Source.ChecksumsFrom,
        };
        // An entry checked with `verify` has minVersion and never version (SIBLINGS.md).
        if (Install is RequirementInstall.ByUser byUser) source["minVersion"] = byUser.Verify.MinVersion;
        else source["version"] = Source.Version;
        if (Source.Commit is { } commit) source["commit"] = commit;
        if (Source.Sha256Note is { } note) source["sha256Note"] = note;
        var item = new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["kind"] = KindRaw(Kind),
            ["neededBy"] = new JsonArray(NeededBy.Select(n => (JsonNode?)n).ToArray()),
            ["required"] = Required,
            ["installed"] = installed,
            ["installedVersion"] = installedVersion,
            ["verified"] = verified,
            ["sizeBytes"] = SizeBytes,
            ["license"] = new JsonObject { ["spdx"] = License.Spdx, ["url"] = License.Url },
            ["source"] = source,
            ["where"] = Location,
            ["remove"] = Removal,
            ["use"] = Use,
            ["modes"] = new JsonArray(Modes.Select(m => (JsonNode?)m).ToArray()),
            ["debugOnly"] = DebugOnly,
        };
        if (Files.Count > 0)
        {
            var files = new JsonArray();
            foreach (var f in Files)
            {
                var d = new JsonObject { ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 };
                if (f.GitBlob is { } blob) d["gitBlob"] = blob;
                files.Add(d);
            }
            item["files"] = files;
        }
        JsonNode? check = Check is { } c ? new JsonObject { ["command"] = c.Command, ["expect"] = c.Expect } : null;
        switch (Install)
        {
            case RequirementInstall.InApp inApp:
                item["install"] = new JsonObject { ["how"] = "in_app", ["where"] = inApp.Where, ["check"] = check };
                break;
            case RequirementInstall.ByUser user:
                item["install"] = new JsonObject
                {
                    ["how"] = "by_user",
                    ["steps"] = new JsonArray(user.Steps.Select(s => (JsonNode?)s).ToArray()),
                    ["check"] = check,
                };
                item["verify"] = new JsonObject
                {
                    ["command"] = user.Verify.Command,
                    ["teamID"] = user.Verify.TeamID,
                    ["note"] = user.Verify.Note,
                    ["minVersion"] = user.Verify.MinVersion,
                    ["minVersionFrom"] = user.Verify.MinVersionFrom,
                };
                break;
            default:
                item["install"] = new JsonObject { ["how"] = "bundled" };
                break;
        }
        return item;
    }

    /// The `kind` as the JSON has it.
    public static string KindRaw(RequirementKind kind) => kind switch
    {
        RequirementKind.Model => "model",
        RequirementKind.Library => "library",
        RequirementKind.Plugin => "plugin",
        _ => "tool",
    };

    /// The agent guide's section, from the register.
    public static string GuideSection()
    {
        var s = """
## What Glimmer may need installed

Everything else Glimmer needs is in the app. These are the only things that may have to be added, and none of them is needed for most work. `GET /api/v1/requirements` gives the same list as JSON, with `installed` for each, and for a model `verified`: true when Glimmer checked its files against the checksums below, false when Glimmer 0.3.0 installed it without checking (it keeps working until the person chooses **Download Again** in Settings › Models), null when it isn't installed.

What you may do about them:

- Explain what is missing, and show its source and license from this list.
- Open the right Settings pane for the person with `POST /api/v1/settings/open` and `{"pane": "models"}` (or `"ai"`). It only opens the pane; it changes nothing.
- The API never starts a download. A model is downloaded only when the person clicks **Download** in Glimmer's own window; Glimmer then checks the SHA-256 of every file against the list below, and refuses a model whose checksum doesn't match.
- A tool marked "installed by the person" is installed outside Glimmer. Show the person the steps, and run them only after they say yes. Use the versions given; never pipe a download into a shell, never turn Gatekeeper off and never remove the quarantine flag.
- Paid services are not installed: the person adds a key in Glimmer › Settings › AI (`providerKeys` in `GET /api/v1/status` says which keys are there).

A step that needs a model that isn't downloaded is refused before any step of the request runs: `422` with `{"error": {"code": "model_missing", "requirement": "<id>", "requirements": ["<id>", …], "step": <index>}}`. Today that is @@NEEDS@@. A model that is installed but can't be loaded (its files damaged) answers `503` with `{"error": {"code": "model_unavailable", "requirement": "<id>", …}}`: the person removes it and downloads it again in Settings › Models.
""" + "\n";
        // "`select_sky` (and `run_command` with `select.sky`), which needs `a` and `b`; …", from the two tables.
        static string Ids(IReadOnlyList<string> ids) => ids.Count == 1 ? $"`{ids[0]}`" : string.Join(", ", ids.Take(ids.Count - 1).Select(i => $"`{i}`")) + $" and `{ids[^1]}`";
        var needs = ModelsForOps.Select(op =>
        {
            // The command of the same name: `select_sky` and `select.sky`.
            var command = op.Key.Replace('_', '.');
            var also = ModelsForCommands.ContainsKey(command) ? $" (and `run_command` with `{command}`)" : "";
            return $"`{op.Key}`{also}, which needs {Ids(op.Value)}";
        });
        s = s.Replace("@@NEEDS@@", string.Join("; ", needs));
        // The en_US grouping ("49,819,122"); the invariant culture groups the same way and needs no ICU data.
        var en = CultureInfo.InvariantCulture;
        foreach (var r in Installable)
        {
            s += $"\n### {r.Name} (`{r.Id}`)\n\n";
            s += $"- Kind: {KindRaw(r.Kind)}, {(r.Required ? "required" : "optional")}. Needed by: {string.Join(", ", r.NeededBy)}.\n";
            if (r.Install is RequirementInstall.ByUser byUser)
                s += $"- Publisher: {r.Source.Publisher}. Source: {r.Source.Url}, minimum version {byUser.Verify.MinVersion} (the steps below install {r.Source.Version})";
            else
                s += $"- Publisher: {r.Source.Publisher}. Source: {r.Source.Url}, version {r.Source.Version}";
            if (r.Source.Commit is { } commit) s += $", commit `{commit}`";
            s += ".\n";
            s += $"- License: {r.License.Spdx} ({r.License.Url}).\n";
            if (r.SizeBytes is { } size) s += $"- Size: {size.ToString("N0", en)} bytes.\n";
            if (r.Source.Sha256 is { } sha) s += $"- SHA-256: `{sha}`.\n";
            if (r.Files.Count > 0)
            {
                s += "- Files, checked by Glimmer after the download:\n";
                foreach (var f in r.Files)
                    s += $"  - `{f.Path}`, {f.Size} bytes, " + (f.Sha256 is { } fileSha ? $"SHA-256 `{fileSha}`" : $"git blob `{f.GitBlob ?? ""}`") + "\n";
            }
            if (r.Source.Sha256Note is { } note && r.Files.Count == 0) s += $"- {note}\n";
            s += $"- Checksums read from {r.Source.ChecksumsFrom}.\n";
            s += $"- Where it goes: {r.Location}. Remove: {r.Removal}\n";
            if (r.Check is { } check) s += $"- Check that it went right: `{check.Command}` → {check.Expect}.\n";
            switch (r.Install)
            {
                case RequirementInstall.InApp inApp:
                    s += $"- Installed in Glimmer: {inApp.Where} › Download, or the question Glimmer asks the first time a feature needs it.\n";
                    break;
                case RequirementInstall.ByUser user:
                    s += "- Installed by the person, outside Glimmer:\n";
                    for (var i = 0; i < user.Steps.Count; i++) s += $"  {i + 1}. {user.Steps[i]}\n";
                    s += $"- Check: `{user.Verify.Command}`. Signer: {(user.Verify.TeamID is { } team ? $"`{team}`" : $"null ({user.Verify.Note ?? ""})")}. Minimum version {user.Verify.MinVersion}: {user.Verify.MinVersionFrom}\n";
                    break;
            }
        }
        return s;
    }

    /// The two tables of THIRD_PARTY_NOTICES.md, between its markers (a test keeps the file in step).
    public static string NoticesTables()
    {
        var s = "## Bundled in the app\n\n| Component | Version | License | Use |\n|---|---|---|---|\n";
        foreach (var r in All.Where(r => r.Install is RequirementInstall.Bundled))
            s += $"| [{r.Name}]({r.Source.Url}) | {r.Source.Version} | [{r.License.Spdx}]({r.License.Url}) | {r.Use} |\n";
        s += "\n## Downloaded by Glimmer when you click Download (not bundled)\n\n";
        s += "Glimmer downloads these at the pinned commit and checks every file's checksum before it uses them.\n\n";
        s += "| Model | Source | License | Use |\n|---|---|---|---|\n";
        foreach (var r in All.Where(r => r.Install is RequirementInstall.InApp))
            s += $"| {r.Name} | [{r.Source.Url.Replace("https://huggingface.co/", "")}]({r.Source.Url}) at `{r.Source.Commit ?? ""}` | [{r.License.Spdx}]({r.License.Url}) | {r.Use} |\n";
        s += "\n## Installed by you, if you want it\n\n| Tool | Version | License | Use |\n|---|---|---|---|\n";
        foreach (var r in All)
            if (r.Install is RequirementInstall.ByUser v)
                s += $"| [{r.Name}]({r.Source.Url}) | {v.Verify.MinVersion} or later ({r.Source.Version} checked) | [{r.License.Spdx}]({r.License.Url}) | {r.Use} |\n";
        return s;
    }
}
