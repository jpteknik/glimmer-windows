// Ported from Packages/GlimmerKit/Sources/GlimmerAI/ModelStore.swift
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Glimmer.AI;

/// On-device models Glimmer downloads when the person asks (ONNX conversions on Hugging Face, at
/// a pinned commit, every file checked), then installs and keeps in the models folder. A model
/// that isn't on this machine yet couldn't be fetched (offline, or the server didn't answer).
///
/// What differs from the Mac: the models are ONNX files run by ONNX Runtime, so there is no Core
/// ML compile step; `IModelInstaller` only moves the verified files into place. And each model has
/// a folder of its own under the models folder (`<models>\<register id>\<package>`): the ONNX
/// repositories all keep their files in a folder called `onnx`, which the Mac's flat layout (one
/// uniquely named `.mlpackage` per package) would let collide.
public sealed class ModelDownloadError : Exception, IEquatable<ModelDownloadError>
{
    public string Model { get; }
    public string Underlying { get; }

    public ModelDownloadError(string model, string underlying)
    {
        Model = model;
        Underlying = underlying;
    }

    public override string Message
    {
        get
        {
            var why = Underlying.Trim(' ', '\t');
            // The system's "The operation couldn’t be completed." says nothing; keep what follows it.
            foreach (var filler in new[] { "The operation couldn’t be completed.", "The operation couldn't be completed." })
            {
                if (why.StartsWith(filler, StringComparison.Ordinal)) why = why[filler.Length..].Trim(' ', '\t');
            }
            if (why.Length == 0) why = "no connection";
            while (why.EndsWith('.')) why = why[..^1];
            return $"This needs the model “{Model}”, which Glimmer downloads once, and the download didn't work: {why}. "
                + "Connect to the internet and try again. Everything that doesn't need this model keeps working offline.";
        }
    }

    public bool Equals(ModelDownloadError? other) => other is not null && Model == other.Model && Underlying == other.Underlying;
    public override bool Equals(object? obj) => Equals(obj as ModelDownloadError);
    public override int GetHashCode() => HashCode.Combine(Model, Underlying);
}

public enum OnDeviceModel
{
    Depth,
    SegmentAnything,
    /// Remove Background and Select Subject (Vision's foreground instance mask on the Mac).
    Subject,
    /// Select People, step one: where the people are (Vision's person segmentation on the Mac).
    PersonDetector,
    /// Select People, step two: their outline, hair included.
    PersonMatting,
    /// Smart layer names (Vision's image classification on the Mac).
    ImageLabels,
}

public static class OnDeviceModels
{
    public static IReadOnlyList<OnDeviceModel> AllCases { get; } = new[]
    {
        OnDeviceModel.Depth, OnDeviceModel.SegmentAnything, OnDeviceModel.Subject, OnDeviceModel.PersonDetector,
        OnDeviceModel.PersonMatting, OnDeviceModel.ImageLabels,
    };

    public static string Title(this OnDeviceModel model) => model switch
    {
        OnDeviceModel.Depth => "Depth (Depth Anything V2 Small)",
        OnDeviceModel.SegmentAnything => "Magic Select (SAM 2.1 Tiny)",
        OnDeviceModel.Subject => "Subject (IS-Net)",
        OnDeviceModel.PersonDetector => "People (RT-DETRv2)",
        OnDeviceModel.PersonMatting => "People's outlines (MODNet)",
        _ => "Layer names (MobileNetV4)",
    };

    /// Its entry in the register: source, pinned commit, every file's checksum, license.
    public static Requirement Requirement(this OnDeviceModel model) => model switch
    {
        OnDeviceModel.Depth => Glimmer.AI.Requirement.DepthAnything,
        OnDeviceModel.SegmentAnything => Glimmer.AI.Requirement.SegmentAnything,
        OnDeviceModel.Subject => Glimmer.AI.Requirement.SubjectSegmentation,
        OnDeviceModel.PersonDetector => Glimmer.AI.Requirement.PersonDetection,
        OnDeviceModel.PersonMatting => Glimmer.AI.Requirement.PersonMatting,
        _ => Glimmer.AI.Requirement.ImageLabels,
    };

    public static OnDeviceModel? With(string requirementId) => AllCases.Cast<OnDeviceModel?>().FirstOrDefault(m => m!.Value.Requirement().Id == requirementId);

    /// "onnx-community/sam2.1-hiera-tiny-ONNX"
    internal static string Repository(this OnDeviceModel model) => RepositoryOf(model.Requirement());

    internal static string RepositoryOf(Requirement r) => r.Source.Url.Replace("https://huggingface.co/", "");

    /// The packages (directories, or single files) this model consists of: the first path
    /// component of each of its files, in register order. On the Mac they are the .mlpackage
    /// directories; here the repositories' `onnx` folder (and `config.json` for the labels).
    public static IReadOnlyList<string> Packages(this OnDeviceModel model) => PackagesOf(model.Requirement());

    internal static IReadOnlyList<string> PackagesOf(Requirement r)
    {
        var seen = new List<string>();
        foreach (var file in r.Files)
        {
            var package = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!seen.Contains(package)) seen.Add(package);
        }
        return seen;
    }

    /// An approximation of `ByteCountFormatter` with the file style (powers of 1000; KB without
    /// decimals, MB with one, GB with two).
    public static string ApproximateSize(this OnDeviceModel model) => FormatBytes(model.Requirement().SizeBytes ?? 0);

    internal static string FormatBytes(long bytes)
    {
        var ci = CultureInfo.InvariantCulture;
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        if (bytes < 1_000_000) return $"{Math.Round(bytes / 1000.0).ToString("0", ci)} KB";
        if (bytes < 1_000_000_000) return $"{(bytes / 1_000_000.0).ToString("0.0", ci)} MB";
        return $"{(bytes / 1_000_000_000.0).ToString("0.00", ci)} GB";
    }
}

/// A model a feature needs isn't on this machine. Nothing was downloaded: the person downloads it
/// in Glimmer's window (the question sheet, or Settings › Models), never an API call.
public sealed class ModelMissingError : Exception, IEquatable<ModelMissingError>
{
    public IReadOnlyList<OnDeviceModel> Missing { get; }

    public ModelMissingError(IEnumerable<OnDeviceModel> missing) { Missing = missing.ToList(); }

    public override string Message
    {
        get
        {
            var names = string.Join(" and ", Missing.Select(m => $"“{m.Title()}”"));
            var one = Missing.Count == 1;
            return $"This needs {(one ? "the model" : "the models")} {names}, which {(one ? "isn't" : "aren't")} downloaded yet. "
                + $"Download {(one ? "it" : "them")} in Glimmer › Settings › Models, then try again.";
        }
    }

    public bool Equals(ModelMissingError? other) => other is not null && Missing.SequenceEqual(other.Missing);
    public override bool Equals(object? obj) => Equals(obj as ModelMissingError);
    public override int GetHashCode() => Missing.Count;
}

/// A model is installed but can't be loaded (its files are damaged).
public sealed class ModelUnavailableError : Exception, IEquatable<ModelUnavailableError>
{
    public OnDeviceModel Model { get; }
    public string Underlying { get; }

    public ModelUnavailableError(OnDeviceModel model, string underlying)
    {
        Model = model;
        Underlying = underlying;
    }

    public override string Message =>
        $"The model “{Model.Title()}” is installed but couldn't be loaded ({Underlying}). Remove it in Glimmer › Settings › Models and download it again.";

    public bool Equals(ModelUnavailableError? other) => other is not null && Model == other.Model && Underlying == other.Underlying;
    public override bool Equals(object? obj) => Equals(obj as ModelUnavailableError);
    public override int GetHashCode() => HashCode.Combine(Model, Underlying);
}

/// A downloaded file isn't the one the register lists: the model is not installed.
public sealed class ModelChecksumError : Exception, IEquatable<ModelChecksumError>
{
    public string Model { get; }
    public string File { get; }
    public string Reason { get; }

    public ModelChecksumError(string model, string file, string reason)
    {
        Model = model;
        File = file;
        Reason = reason;
    }

    public override string Message =>
        $"The download of “{Model}” didn't match its published checksum ({File}: {Reason}), so Glimmer didn't install it. "
        + "Nothing of it was kept. Try again later; if it keeps happening, check Settings › Models › Download from.";

    public bool Equals(ModelChecksumError? other) => other is not null && Model == other.Model && File == other.File && Reason == other.Reason;
    public override bool Equals(object? obj) => Equals(obj as ModelChecksumError);
    public override int GetHashCode() => HashCode.Combine(Model, File, Reason);
}

/// Written beside a model when Glimmer has checked its files against the register: which entry,
/// at which commit, which files with which checksums, and how. A model without one was installed
/// by Glimmer 0.3.0, which checked nothing.
public sealed class ModelReceipt
{
    public sealed record File(string Path, int Size, string? Sha256, string? GitBlob);

    public string Requirement { get; }
    public string Commit { get; }
    public IReadOnlyList<File> Files { get; }
    public string CheckedAt { get; }
    /// "download", or "earlier download" (0.3.0's files found and checked).
    public string How { get; }

    private ModelReceipt(string requirement, string commit, IReadOnlyList<File> files, string checkedAt, string how)
    {
        Requirement = requirement;
        Commit = commit;
        Files = files;
        CheckedAt = checkedAt;
        How = how;
    }

    internal ModelReceipt(Requirement r, string how)
        : this(r.Id, r.Source.Commit ?? "", r.Files.Select(f => new File(f.Path, f.Size, f.Sha256, f.GitBlob)).ToList(),
               DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), how)
    {
    }

    /// True when it vouches for exactly what the register lists now.
    internal bool Covers(Requirement r) =>
        Requirement == r.Id && Commit == (r.Source.Commit ?? "") && r.Source.Commit is not null
        && Files.SequenceEqual(r.Files.Select(f => new File(f.Path, f.Size, f.Sha256, f.GitBlob)));

    /// The file as Swift wrote it: sorted keys, indented, a missing checksum left out.
    internal string ToJsonText()
    {
        var files = new JsonArray();
        foreach (var f in Files)
        {
            var o = new JsonObject { ["path"] = f.Path, ["size"] = f.Size };
            if (f.Sha256 is { } sha) o["sha256"] = sha;
            if (f.GitBlob is { } blob) o["gitBlob"] = blob;
            files.Add(o);
        }
        var root = new JsonObject
        {
            ["requirement"] = Requirement,
            ["commit"] = Commit,
            ["files"] = files,
            ["checkedAt"] = CheckedAt,
            ["how"] = How,
        };
        return Json.SerializeSortedIndented(root);
    }

    /// Null when the text isn't a receipt (every field but the checksums must be there).
    internal static ModelReceipt? Parse(string text)
    {
        if (Json.ParseObject(text) is not { } root) return null;
        if (Json.Str(root["requirement"]) is not { } requirement || Json.Str(root["commit"]) is not { } commit
            || Json.Str(root["checkedAt"]) is not { } checkedAt || Json.Str(root["how"]) is not { } how
            || Json.Objects(root["files"]) is not { } list)
            return null;
        var files = new List<File>();
        foreach (var f in list)
        {
            if (Json.Str(f["path"]) is not { } path || Json.Int(f["size"]) is not { } size) return null;
            files.Add(new File(path, size, Json.Str(f["sha256"]), Json.Str(f["gitBlob"])));
        }
        return new ModelReceipt(requirement, commit, files, checkedAt, how);
    }
}

/// Whether an installed model's files were checked (its receipt covers the register).
public enum ModelVerification
{
    NotInstalled,
    Verified,
    /// Installed by an earlier version, which checked nothing. It keeps working until the person chooses Download Again.
    Unverified,
}

public static class ModelVerifications
{
    /// For `GET /api/v1/requirements`: true, false, or null when it isn't installed.
    public static JsonNode? Json(this ModelVerification v) => v == ModelVerification.NotInstalled ? null : JsonValue.Create(v == ModelVerification.Verified);
}

/// The platform's step between "the files arrived and were checked" and "the model is installed".
/// On the Mac that is compiling the Core ML package (`MLModel.compileModel`), which does not exist
/// here: the default `MovePackageInstaller` puts the verified files where the store keeps them.
/// The ONNX models need nothing more; an ONNX Runtime session is made from the installed files by
/// the code that uses them (`OnnxModels`, from `ModelStore.LocateAsync`).
public interface IModelInstaller
{
    /// The name a package has under the models folder once installed (the Mac: ".mlpackage" becomes ".mlmodelc").
    string InstalledName(string package);

    /// Puts the verified package (a directory or a file) at `destination`, replacing what is there.
    Task InstallAsync(string stagedPackage, string destination, CancellationToken ct);
}

/// No compile step: moves the verified package into place under its own name.
public sealed class MovePackageInstaller : IModelInstaller
{
    public string InstalledName(string package) => package;

    public Task InstallAsync(string stagedPackage, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        else if (System.IO.File.Exists(destination)) System.IO.File.Delete(destination);
        if (Directory.Exists(stagedPackage)) Directory.Move(stagedPackage, destination);
        else System.IO.File.Move(stagedPackage, destination);
        return Task.CompletedTask;
    }
}

public sealed class ModelStore
{
    private static ModelStore? shared;
    private static readonly object SharedGate = new();

    /// The store the app uses: the default folder (`%LOCALAPPDATA%\Glimmer\Models`), unless the app set another.
    public static ModelStore Shared
    {
        get { lock (SharedGate) return shared ??= new ModelStore(); }
        set { lock (SharedGate) shared = value; }
    }

    /// Where installed models live. Tests can point at pre-downloaded packages with
    /// the GLIMMER_MODELS_DIR environment variable.
    internal string Root { get; }
    /// GLIMMER_MODELS_DIR, read when the store is made; a test about installing sets it to null so models found there
    /// don't count as installed.
    internal string? TestModels { get => sourceOverride; init => sourceOverride = value; }
    private readonly string? sourceOverride;

    /// Where this store downloads from; null means `ModelStore.Source` (Settings › Models). Tests name a sample server.
    private readonly Uri? downloadSource;
    /// Where Glimmer 0.3.0 left the files it downloaded (it never removed its staging folders).
    private readonly string earlierDownloads;
    private readonly HttpClient http;
    private readonly IModelInstaller installer;
    private readonly object gate = new();
    /// Models whose earlier install has been looked into in this run (once each).
    private readonly HashSet<OnDeviceModel> lookedAtEarlier = new();
    private readonly Dictionary<OnDeviceModel, Task> inFlight = new();

    /// The register entry of a model; tests replace it to run the whole download with made-up files.
    internal Func<OnDeviceModel, Requirement> RequirementFor { get; set; } = m => m.Requirement();

    private IReadOnlyList<string> PackagesOf(OnDeviceModel model) => OnDeviceModels.PackagesOf(RequirementFor(model));

    private string RepositoryOf(OnDeviceModel model) => OnDeviceModels.RepositoryOf(RequirementFor(model));

    /// `root`: the models folder (the app passes %LOCALAPPDATA%\Glimmer\Models; tests a temp folder).
    /// `handler`: the transport (tests pass a fake one); downloads follow redirects, as Hugging Face sends them to its storage.
    public ModelStore(string? root = null, Uri? source = null, string? earlierDownloads = null,
                      HttpMessageHandler? handler = null, IModelInstaller? installer = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer", "Models");
        var overrideDir = Environment.GetEnvironmentVariable("GLIMMER_MODELS_DIR");
        sourceOverride = string.IsNullOrEmpty(overrideDir) ? null : overrideDir;
        downloadSource = source;
        this.earlierDownloads = earlierDownloads ?? Path.Combine(Path.GetTempPath(), "glimmer-models");
        this.installer = installer ?? new MovePackageInstaller();
        http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// Where models are downloaded from: Hugging Face (the ONNX conversions the register names)
    /// unless Settings › Models names a mirror with the same paths (<repository>/resolve/<commit>/<file>).
    public const string DefaultSource = "https://huggingface.co";
    public const string SourceKey = "models.source";

    /// The mirror named in the setting (the app reads `SourceKey` and passes the text), or Hugging Face
    /// when it is empty or not an http(s) URL.
    public static Uri SourceFrom(string? setting)
    {
        var text = (setting ?? "").Trim(' ', '\t');
        if (Uri.TryCreate(text, UriKind.Absolute, out var url)
            && (url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && url.Host.Length > 0)
            return url;
        return new Uri(DefaultSource);
    }

    private static bool Exists(string path) => Directory.Exists(path) || System.IO.File.Exists(path);

    /// True when every package of `model` is installed on this machine (or in GLIMMER_MODELS_DIR for tests).
    public bool IsInstalled(OnDeviceModel model) =>
        PackagesOf(model).All(p => Exists(InstalledPath(model, p)))
        || (sourceOverride is not null && PackagesOf(model).All(p => OverridePath(model, p) is not null));

    /// A package in GLIMMER_MODELS_DIR, laid out as the models folder is (`<id>\<package>`), when it is there.
    private string? OverridePath(OnDeviceModel model, string package) =>
        sourceOverride is { } dir && Path.Combine(dir, RequirementFor(model).Id, package) is var p && Exists(p) ? p : null;

    /// Those of `models` that aren't on this machine.
    public IReadOnlyList<OnDeviceModel> Missing(IEnumerable<OnDeviceModel> models) => models.Where(m => !IsInstalled(m)).ToList();

    internal string ReceiptPath(OnDeviceModel model) => Path.Combine(Root, $"{RequirementFor(model).Id}.receipt.json");

    internal ModelReceipt? Receipt(OnDeviceModel model)
    {
        try { return ModelReceipt.Parse(System.IO.File.ReadAllText(ReceiptPath(model))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Whether `model` is installed, and if so whether a receipt vouches for its files. Reads no model file.
    public ModelVerification Verification(OnDeviceModel model)
    {
        var installed = PackagesOf(model).All(p => Exists(InstalledPath(model, p)));
        if (!installed) return ModelVerification.NotInstalled;
        return Receipt(model)?.Covers(RequirementFor(model)) == true ? ModelVerification.Verified : ModelVerification.Unverified;
    }

    /// Not installed, or installed without a receipt: what the Download (Again) button is for.
    public bool NeedsDownload(OnDeviceModel model) => !IsInstalled(model) || Verification(model) == ModelVerification.Unverified;

    /// An install without a receipt (Glimmer 0.3.0's): if the files it downloaded are still in its
    /// staging folders, they are checked against the register, once per run, and the receipt is
    /// written. Otherwise the model stays as it is, unverified and working. Downloads nothing.
    public ModelVerification CheckEarlierInstall(OnDeviceModel model)
    {
        var now = Verification(model);
        lock (gate)
        {
            if (now != ModelVerification.Unverified || lookedAtEarlier.Contains(model)) return now;
            lookedAtEarlier.Add(model);
        }
        var r = RequirementFor(model);
        foreach (var package in PackagesOf(model))
        {
            var files = r.Files.Where(f => f.Path.StartsWith(package + "/", StringComparison.Ordinal)).ToList();
            if (EarlierCopy(package, files, earlierDownloads) is null) return now;
        }
        try { WriteReceipt(new ModelReceipt(r, "earlier download"), model); } catch (IOException) { /* `try?` */ }
        return Verification(model);
    }

    /// The folder in `downloads` holding `package` with every one of `files` as the register lists it.
    internal static string? EarlierCopy(string package, IReadOnlyList<RequirementFile> files, string downloads)
    {
        string[] folders;
        try { folders = Directory.GetDirectories(downloads); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        foreach (var folder in folders)
        {
            if (!Exists(Path.Combine(folder, package))) continue;
            var all = true;
            foreach (var file in files)
            {
                string? problem;
                try { problem = FileChecksum.Mismatch(Path.Combine(folder, file.Path), file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { all = false; break; }
                if (problem is not null) { all = false; break; }
            }
            if (all) return folder;
        }
        return null;
    }

    private void WriteReceipt(ModelReceipt receipt, OnDeviceModel model)
    {
        Directory.CreateDirectory(Root);
        var path = ReceiptPath(model);
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, receipt.ToJsonText(), new UTF8Encoding(false));
        System.IO.File.Move(temp, path, overwrite: true);
    }

    /// Where a package of a model is once installed: in the model's own folder, named by its register id.
    internal string InstalledPath(OnDeviceModel model, string package) =>
        Path.Combine(Root, RequirementFor(model).Id, installer.InstalledName(package));

    /// Where `package` of `model` is on this machine, for the code that loads it (on Windows an
    /// ONNX Runtime session; the Mac's `load` made an `MLModel`). Never downloads: a model that
    /// isn't here throws `ModelMissingError`, and the window asks the person (see `DownloadAsync`).
    /// A package found only in GLIMMER_MODELS_DIR is used where it is (the Mac compiled a copy of
    /// it; moving the files would empty the tests' folder).
    public Task<string> LocateAsync(string package, OnDeviceModel model, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var installed = InstalledPath(model, package);
        if (!Exists(installed))
            return Task.FromResult(OverridePath(model, package) ?? throw new ModelMissingError(new[] { model }));
        CheckEarlierInstall(model);
        return Task.FromResult(installed);
    }

    /// Downloads `model` at its pinned commit, checks every file against the register, then
    /// installs it. Only the person's click in Glimmer's window calls this. A file whose
    /// size or checksum is wrong stops it: nothing is installed and nothing is kept.
    public async Task DownloadAsync(OnDeviceModel model, Action<double>? progress = null, CancellationToken ct = default)
    {
        Task running;
        var mine = false;
        lock (gate)
        {
            if (!inFlight.TryGetValue(model, out running!))
            {
                running = Task.Run(() => PerformDownloadAsync(model, progress, ct), CancellationToken.None);
                inFlight[model] = running;
                mine = true;
            }
        }
        try
        {
            await running.ConfigureAwait(false);
        }
        finally
        {
            if (mine) lock (gate) inFlight.Remove(model);
        }
    }

    private async Task PerformDownloadAsync(OnDeviceModel model, Action<double>? progress, CancellationToken ct)
    {
        var r = RequirementFor(model);
        if (r.Source.Commit is not { } commit) throw new ModelDownloadError(model.Title(), "The address is not usable.");
        var baseUrl = Http.Append(downloadSource ?? SourceFrom(null), $"{RepositoryOf(model)}/resolve/{commit}");
        var staging = Path.Combine(Path.GetTempPath(), "glimmer-models", Guid.NewGuid().ToString().ToUpperInvariant());
        try
        {
            try
            {
                await FetchVerifiedAsync(r.Files, baseUrl, staging, model.Title(), http, progress, ct).ConfigureAwait(false);
            }
            catch (ModelChecksumError)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ModelDownloadError(model.Title(), Http.Describe(error));
            }
            // Every package from these checked files, replacing an unverified copy (Download Again).
            try { System.IO.File.Delete(ReceiptPath(model)); } catch (IOException) { /* `try?` */ }
            OnnxModels.Release(Path.Combine(Root, r.Id));
            foreach (var package in PackagesOf(model))
                await installer.InstallAsync(Path.Combine(staging, package), InstalledPath(model, package), ct).ConfigureAwait(false);
            WriteReceipt(new ModelReceipt(r, "download"), model);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { /* `try?` */ }
        }
    }

    /// Removes a downloaded model.
    public void Remove(OnDeviceModel model)
    {
        // Open sessions hold the files (Windows won't delete a file in use).
        OnnxModels.Release(Path.Combine(Root, RequirementFor(model).Id));
        foreach (var package in PackagesOf(model))
        {
            var path = InstalledPath(model, package);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch (IOException) { /* `try?` */ }
        }
        // The model's own folder too, once it is empty.
        try
        {
            var folder = Path.Combine(Root, RequirementFor(model).Id);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { /* `try?` */ }
        try { System.IO.File.Delete(ReceiptPath(model)); } catch (IOException) { /* `try?` */ }
    }

    /// Fetches `files` from `base/<path>` into `staging/<path>`, each checked as it arrives:
    /// its size, then its SHA-256 or (for a file kept in git) its git blob id. The first file that
    /// differs throws `ModelChecksumError`, and the caller drops the whole folder.
    internal static async Task FetchVerifiedAsync(IReadOnlyList<RequirementFile> files, Uri baseUrl, string staging, string model,
                                                  HttpClient client, Action<double>? progress, CancellationToken ct = default)
    {
        var total = (double)Math.Max(files.Sum(f => f.Size), 1);
        long done = 0;
        foreach (var file in files)
        {
            var dest = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var response = await client.GetAsync(Http.Append(baseUrl, file.Path), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if ((int)response.StatusCode != 200)
                    throw new HttpRequestException($"The server answered HTTP {(int)response.StatusCode}.");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = System.IO.File.Create(dest);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            if (FileChecksum.Mismatch(dest, file) is { } problem)
                throw new ModelChecksumError(model, file.Path, problem);
            done += file.Size;
            progress?.Invoke(done / total);
        }
    }
}

/// SHA-256 and git blob ids of files, read in pieces (a model's weights are tens of MB).
internal static class FileChecksum
{
    /// Why the file at `path` isn't `expected`, or null when it is.
    public static string? Mismatch(string path, RequirementFile expected)
    {
        var size = new FileInfo(path).Length;
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException("No such file.", path);
        if (size != expected.Size) return $"{size} bytes, not {expected.Size}";
        if (expected.Sha256 is { } sha)
        {
            var found = Sha256(path);
            return found == sha ? null : $"SHA-256 {found}, not {sha}";
        }
        if (expected.GitBlob is { } blob)
        {
            var found = GitBlob(path, size);
            return found == blob ? null : $"git blob {found}, not {blob}";
        }
        return "the register lists no checksum for it";
    }

    public static string Sha256(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Read(path, chunk => hash.AppendData(chunk));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// git's id of a file: SHA-1 of "blob <size>\0" followed by the bytes.
    public static string GitBlob(string path, long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.UTF8.GetBytes($"blob {size}\0"));
        Read(path, chunk => hash.AppendData(chunk));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Read(string path, Action<ReadOnlySpan<byte>> body)
    {
        using var stream = System.IO.File.OpenRead(path);
        var buffer = new byte[1 << 20];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) body(buffer.AsSpan(0, count));
    }
}
