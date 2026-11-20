// Ported from Packages/GlimmerKit/Tests/GlimmerAITests/ModelDownloadTests.swift
// The original served made-up files from a socket on the Mac's loopback; here a fake handler serves
// them, so no socket is opened and no test reaches a network. Each file is checked against the register
// (size, then SHA-256 or git blob id), and a file that differs stops the download with nothing installed.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Glimmer.AI;

namespace Glimmer.AI.Tests;

public class ModelDownloadTests
{
    /// Serves `files` (path → bytes) and records the paths asked for.
    internal sealed class SampleServer : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly Dictionary<string, byte[]> files;
        private readonly List<string> asked = new();

        public SampleServer(Dictionary<string, byte[]> files) { this.files = files; }

        public Uri Base => new("http://sample.invalid/");
        public IReadOnlyList<string> Requests { get { lock (gate) return asked.ToList(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            byte[]? body;
            lock (gate)
            {
                asked.Add(path);
                files.TryGetValue(path, out body);
            }
            var response = new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? Array.Empty<byte>()) };
            return Task.FromResult(response);
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string GitBlob(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"blob {data.Length}\0").Concat(data).ToArray()));

    [Fact]
    public void GitBlobIdIsGitsOwn()
    {
        // `printf 'hello\n' | git hash-object --stdin`
        using var folder = new TempFolder();
        var path = folder.Sub("hello");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello\n"));
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", FileChecksum.GitBlob(path, 6));
        Assert.Equal("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", FileChecksum.Sha256(path));
    }

    [Fact]
    public async Task MatchingFilesArriveAndAreChecked()
    {
        var manifest = Encoding.UTF8.GetBytes(@"{""fileFormatVersion"": ""1.0.0""}");
        var weights = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        var server = new SampleServer(new()
        {
            ["/repo/resolve/abc123/Pkg.mlpackage/Manifest.json"] = manifest,
            ["/repo/resolve/abc123/Pkg.mlpackage/weights.bin"] = weights,
        });
        var files = new[]
        {
            new RequirementFile("Pkg.mlpackage/Manifest.json", manifest.Length, null, GitBlob(manifest)),
            new RequirementFile("Pkg.mlpackage/weights.bin", weights.Length, Sha256(weights), null),
        };
        using var staging = new TempFolder();
        var seen = new List<double>();
        using var client = new HttpClient(server);
        await ModelStore.FetchVerifiedAsync(files, new Uri(server.Base, "repo/resolve/abc123"), staging.Path, "Sample", client, seen.Add);
        Assert.Equal(weights, File.ReadAllBytes(Path.Combine(staging.Path, "Pkg.mlpackage", "weights.bin")));
        Assert.Equal(1.0, seen[^1]);
        Assert.Equal(2, seen.Count);
        Assert.Equal(new[] { "/repo/resolve/abc123/Pkg.mlpackage/Manifest.json", "/repo/resolve/abc123/Pkg.mlpackage/weights.bin" }, server.Requests);
    }

    [Fact]
    public async Task AFileWithTheWrongChecksumStopsTheDownload()
    {
        var weights = Enumerable.Repeat((byte)7, 4096).ToArray();
        var tampered = Enumerable.Repeat((byte)8, 4096).ToArray();   // same size, other bytes
        var server = new SampleServer(new() { ["/r/a.bin"] = tampered, ["/r/b.bin"] = weights });
        var files = new[]
        {
            new RequirementFile("a.bin", weights.Length, Sha256(weights), null),
            new RequirementFile("b.bin", weights.Length, Sha256(weights), null),
        };
        using var staging = new TempFolder();
        using var client = new HttpClient(server);
        await Assert.ThrowsAsync<ModelChecksumError>(() => ModelStore.FetchVerifiedAsync(files, new Uri(server.Base, "r"), staging.Path, "Sample", client, null));
        Assert.Equal(new[] { "/r/a.bin" }, server.Requests);   // nothing more is fetched after a file that doesn't match
    }

    [Fact]
    public async Task AWrongManifestBlobOrSizeStopsTheDownload()
    {
        var manifest = Encoding.UTF8.GetBytes("{}");
        var server = new SampleServer(new() { ["/r/Manifest.json"] = Encoding.UTF8.GetBytes("{ }"), ["/r/short.bin"] = new byte[10] });
        using var staging = new TempFolder();
        using var client = new HttpClient(server);
        var blob = new[] { new RequirementFile("Manifest.json", 3, null, GitBlob(manifest)) };
        var wrongBlob = await Assert.ThrowsAsync<ModelChecksumError>(() => ModelStore.FetchVerifiedAsync(blob, new Uri(server.Base, "r"), staging.Path, "Sample", client, null));
        Assert.StartsWith("git blob", wrongBlob.Reason);
        var size = new[] { new RequirementFile("short.bin", 11, new string('0', 64), null) };
        var wrongSize = await Assert.ThrowsAsync<ModelChecksumError>(() => ModelStore.FetchVerifiedAsync(size, new Uri(server.Base, "r"), staging.Path, "Sample", client, null));
        Assert.Equal("10 bytes, not 11", wrongSize.Reason);
        var none = new[] { new RequirementFile("short.bin", 10, null, null) };
        var nothing = await Assert.ThrowsAsync<ModelChecksumError>(() => ModelStore.FetchVerifiedAsync(none, new Uri(server.Base, "r"), staging.Path, "Sample", client, null));
        Assert.Equal("the register lists no checksum for it", nothing.Reason);
    }

    [Fact]
    public async Task AServerThatDoesNotHaveTheFileIsADownloadErrorNotAChecksumError()
    {
        var server = new SampleServer(new());
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models"), server.Base, handler: server) { TestModels = null };
        var error = await Assert.ThrowsAsync<ModelDownloadError>(() => store.DownloadAsync(OnDeviceModel.Depth));
        Assert.Contains("This needs the model “Depth (Depth Anything V2 Small)”, which Glimmer downloads once, and the download didn't work: The server answered HTTP 404. Connect to the internet and try again.", error.Message);
        Assert.False(store.IsInstalled(OnDeviceModel.Depth));
    }

    /// The real download path against the sample server: it asks for the pinned commit, never
    /// `main`, and made-up files leave nothing installed and nothing in the staging folder.
    [Fact]
    public async Task ADownloadOfMadeUpFilesInstallsNothing()
    {
        var model = OnDeviceModel.Depth;
        var commit = model.Requirement().Source.Commit!;
        var files = model.Requirement().Files.ToDictionary(f => $"/{model.Repository()}/resolve/{commit}/{f.Path}", f => new byte[f.Size]);
        var server = new SampleServer(files);
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models"), server.Base, handler: server) { TestModels = null };
        await Assert.ThrowsAsync<ModelChecksumError>(() => store.DownloadAsync(model));
        Assert.False(store.IsInstalled(model));
        Assert.Single(server.Requests);
        Assert.All(server.Requests, r => Assert.True(r.Contains($"/resolve/{commit}/") && !r.Contains("/resolve/main/")));
        Assert.False(Directory.Exists(root.Sub("models")) && Directory.EnumerateFileSystemEntries(root.Sub("models")).Any(), "nothing installed");
    }

    /// The whole path with a register of the test's own: fetched, checked, installed, receipt written, removed again.
    [Fact]
    public async Task ADownloadThatMatchesTheRegisterIsInstalledWithAReceipt()
    {
        var manifest = Encoding.UTF8.GetBytes("{}");
        var weights = Enumerable.Range(0, 5000).Select(i => (byte)(i % 13)).ToArray();
        var entry = Requirement.DepthAnything with
        {
            Files = new[]
            {
                new RequirementFile("P.mlpackage/Manifest.json", manifest.Length, null, GitBlob(manifest)),
                new RequirementFile("P.mlpackage/Data/w.bin", weights.Length, Sha256(weights), null),
            },
            SizeBytes = manifest.Length + weights.Length,
        };
        var commit = entry.Source.Commit!;
        var repo = "onnx-community/depth-anything-v2-small";
        var server = new SampleServer(new()
        {
            [$"/{repo}/resolve/{commit}/P.mlpackage/Manifest.json"] = manifest,
            [$"/{repo}/resolve/{commit}/P.mlpackage/Data/w.bin"] = weights,
        });
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models"), server.Base, handler: server) { RequirementFor = _ => entry };
        var progress = new List<double>();
        await store.DownloadAsync(OnDeviceModel.Depth, progress.Add);
        Assert.True(store.IsInstalled(OnDeviceModel.Depth));
        Assert.Equal(ModelVerification.Verified, store.Verification(OnDeviceModel.Depth));
        Assert.False(store.NeedsDownload(OnDeviceModel.Depth));
        // Each model has a folder of its own, named by its register id (the port's layout; the Mac's is flat).
        var folder = Path.Combine(root.Sub("models"), entry.Id);
        Assert.Equal(weights, File.ReadAllBytes(Path.Combine(folder, "P.mlpackage", "Data", "w.bin")));
        Assert.Equal(1.0, progress[^1]);
        var receipt = File.ReadAllText(store.ReceiptPath(OnDeviceModel.Depth));
        Assert.Contains("\"how\": \"download\"", receipt);
        Assert.Contains($"\"commit\": \"{commit}\"", receipt);
        Assert.Matches("\"checkedAt\": \"\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ\"", receipt);
        // Nothing is left of the staging folder's copy, and Locate finds it without any request.
        var requestsBefore = server.Requests.Count;
        Assert.Equal(Path.Combine(folder, "P.mlpackage"), await store.LocateAsync("P.mlpackage", OnDeviceModel.Depth));
        Assert.Equal(requestsBefore, server.Requests.Count);
        store.Remove(OnDeviceModel.Depth);
        Assert.Equal(ModelVerification.NotInstalled, store.Verification(OnDeviceModel.Depth));
        Assert.False(File.Exists(store.ReceiptPath(OnDeviceModel.Depth)));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task TwoClicksAtOnceShareOneDownload()
    {
        var manifest = Encoding.UTF8.GetBytes("{}");
        var entry = Requirement.DepthAnything with
        {
            Files = new[] { new RequirementFile("P.mlpackage/Manifest.json", manifest.Length, null, GitBlob(manifest)) },
            SizeBytes = manifest.Length,
        };
        var gate = new TaskCompletionSource();
        var server = new SlowServer(gate.Task, new() { [$"/onnx-community/depth-anything-v2-small/resolve/{entry.Source.Commit}/P.mlpackage/Manifest.json"] = manifest });
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models"), server.Base, handler: server) { RequirementFor = _ => entry };
        var first = store.DownloadAsync(OnDeviceModel.Depth);
        var second = store.DownloadAsync(OnDeviceModel.Depth);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, server.Count);
        Assert.True(store.IsInstalled(OnDeviceModel.Depth));
    }

    private sealed class SlowServer : HttpMessageHandler
    {
        private readonly Task release;
        private readonly Dictionary<string, byte[]> files;
        private int count;
        public int Count => count;
        public Uri Base => new("http://sample.invalid/");

        public SlowServer(Task release, Dictionary<string, byte[]> files) { this.release = release; this.files = files; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            await release;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(files[request.RequestUri!.AbsolutePath]) };
        }
    }

    /// Loading a model that isn't on this machine asks nothing of any server: it says what is missing.
    [Fact]
    public async Task LocatingAMissingModelNeverDownloads()
    {
        var server = new SampleServer(new());
        using var root = new TempFolder();
        var store = new ModelStore(root.Sub("models"), server.Base, handler: server) { TestModels = null };
        var error = await Assert.ThrowsAsync<ModelMissingError>(() => store.LocateAsync(OnDeviceModel.SegmentAnything.Packages()[0], OnDeviceModel.SegmentAnything));
        Assert.Equal(new[] { OnDeviceModel.SegmentAnything }, error.Missing);
        Assert.Equal("This needs the model “Magic Select (SAM 2.1 Tiny)”, which isn't downloaded yet. Download it in Glimmer › Settings › Models, then try again.", error.Message);
        Assert.Empty(server.Requests);
        Assert.Equal(OnDeviceModels.AllCases, store.Missing(OnDeviceModels.AllCases));
        Assert.Equal("This needs the models “Depth (Depth Anything V2 Small)” and “Magic Select (SAM 2.1 Tiny)”, which aren't downloaded yet. Download them in Glimmer › Settings › Models, then try again.",
                     new ModelMissingError(new[] { OnDeviceModel.Depth, OnDeviceModel.SegmentAnything }).Message);
    }

    // MARK: Receipts and installs from 0.3.0

    /// A model "installed" as its folders, the way 0.3.0 left it: no receipt. (In the port's layout: under the model's own folder.)
    private static void EarlierInstall(OnDeviceModel model, string root)
    {
        foreach (var package in model.Packages()) Directory.CreateDirectory(Path.Combine(root, model.Requirement().Id, package));
    }

    [Fact]
    public async Task AnInstallWithoutAReceiptIsUnverifiedAndKeepsWorking()
    {
        var server = new SampleServer(new());
        using var root = new TempFolder();
        using var downloads = new TempFolder();
        var models = root.Sub("models");
        var store = new ModelStore(models, server.Base, downloads.Path, server);
        Assert.Equal(ModelVerification.NotInstalled, store.Verification(OnDeviceModel.Depth));
        EarlierInstall(OnDeviceModel.Depth, models);
        Assert.True(store.IsInstalled(OnDeviceModel.Depth), "it still counts as installed: nothing stops working");
        Assert.Equal(ModelVerification.Unverified, store.Verification(OnDeviceModel.Depth));
        Assert.True(store.NeedsDownload(OnDeviceModel.Depth));
        // 0.3.0's downloaded files are gone: it stays unverified, and nothing is fetched.
        Assert.Equal(ModelVerification.Unverified, store.CheckEarlierInstall(OnDeviceModel.Depth));
        Assert.Empty(server.Requests);
        Assert.False((bool)ModelVerification.Unverified.Json()!);
        Assert.Null(ModelVerification.NotInstalled.Json());
        await Task.CompletedTask;
    }

    [Fact]
    public void AReceiptVouchesOnlyForWhatTheRegisterListsNow()
    {
        using var root = new TempFolder();
        var models = root.Sub("models");
        var store = new ModelStore(models);
        EarlierInstall(OnDeviceModel.SegmentAnything, models);
        var good = new ModelReceipt(Requirement.SegmentAnything, "download");
        File.WriteAllText(store.ReceiptPath(OnDeviceModel.SegmentAnything), good.ToJsonText());
        Assert.Equal(ModelVerification.Verified, store.Verification(OnDeviceModel.SegmentAnything));
        Assert.False(store.NeedsDownload(OnDeviceModel.SegmentAnything));
        // A receipt for another commit (or other files) vouches for nothing.
        var text = File.ReadAllText(store.ReceiptPath(OnDeviceModel.SegmentAnything)).Replace(Requirement.SegmentAnything.Source.Commit!, new string('0', 40));
        File.WriteAllText(store.ReceiptPath(OnDeviceModel.SegmentAnything), text);
        Assert.Equal(ModelVerification.Unverified, store.Verification(OnDeviceModel.SegmentAnything));
        // So does one that isn't a receipt at all.
        File.WriteAllText(store.ReceiptPath(OnDeviceModel.SegmentAnything), "not json");
        Assert.Equal(ModelVerification.Unverified, store.Verification(OnDeviceModel.SegmentAnything));
    }

    [Fact]
    public void TheReceiptIsWrittenAsTheMacWroteIt()
    {
        var receipt = new ModelReceipt(Requirement.DepthAnything, "download");
        var text = receipt.ToJsonText();
        // Sorted keys, a missing checksum left out (every Windows model file has a SHA-256, so no git blob is written).
        Assert.True(text.IndexOf("\"checkedAt\"", StringComparison.Ordinal) < text.IndexOf("\"commit\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"commit\"", StringComparison.Ordinal) < text.IndexOf("\"files\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"how\"", StringComparison.Ordinal) < text.IndexOf("\"requirement\"", StringComparison.Ordinal));
        Assert.Contains("\"requirement\": \"depth-anything-v2-small\"", text);
        Assert.DoesNotContain("gitBlob", text);
        var parsed = ModelReceipt.Parse(text)!;
        Assert.True(parsed.Covers(Requirement.DepthAnything));
        Assert.False(parsed.Covers(Requirement.SegmentAnything));
        Assert.Single(parsed.Files);
        Assert.Null(parsed.Files[0].GitBlob);
        Assert.Equal(Requirement.DepthAnything.Files[0].Sha256, parsed.Files[0].Sha256);
        Assert.Null(ModelReceipt.Parse(@"{""requirement"":""x""}"));
    }

    [Fact]
    public void RemovingAModelRemovesItsReceipt()
    {
        using var root = new TempFolder();
        var models = root.Sub("models");
        var store = new ModelStore(models);
        EarlierInstall(OnDeviceModel.Depth, models);
        Directory.CreateDirectory(models);
        File.WriteAllText(store.ReceiptPath(OnDeviceModel.Depth), new ModelReceipt(Requirement.DepthAnything, "download").ToJsonText());
        store.Remove(OnDeviceModel.Depth);
        Assert.Equal(ModelVerification.NotInstalled, store.Verification(OnDeviceModel.Depth));
        Assert.False(File.Exists(store.ReceiptPath(OnDeviceModel.Depth)));
    }

    /// 0.3.0's staging folders: a package whose files all match is found; a tampered one isn't.
    [Fact]
    public void EarlierDownloadedFilesAreCheckedBeforeTheyCount()
    {
        using var downloads = new TempFolder();
        var manifest = Encoding.UTF8.GetBytes("{}");
        var weights = Enumerable.Repeat((byte)3, 2048).ToArray();
        var files = new[]
        {
            new RequirementFile("P.mlpackage/Manifest.json", manifest.Length, null, GitBlob(manifest)),
            new RequirementFile("P.mlpackage/w.bin", weights.Length, Sha256(weights), null),
        };
        void Stage(string name, byte[] w)
        {
            var dir = Path.Combine(downloads.Path, name, "P.mlpackage");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "Manifest.json"), manifest);
            File.WriteAllBytes(Path.Combine(dir, "w.bin"), w);
        }
        Stage("A", Enumerable.Repeat((byte)4, 2048).ToArray());
        Assert.Null(ModelStore.EarlierCopy("P.mlpackage", files, downloads.Path));
        Stage("B", weights);
        Assert.Equal("B", Path.GetFileName(ModelStore.EarlierCopy("P.mlpackage", files, downloads.Path)));
        Assert.Null(ModelStore.EarlierCopy("P.mlpackage", files, Path.Combine(downloads.Path, "nowhere")));
    }

    [Fact]
    public void AnEarlierInstallWhoseFilesAreStillThereGetsItsReceipt()
    {
        using var root = new TempFolder();
        using var downloads = new TempFolder();
        var manifest = Encoding.UTF8.GetBytes("{}");
        var entry = Requirement.DepthAnything with
        {
            Files = new[] { new RequirementFile("P.mlpackage/Manifest.json", manifest.Length, null, GitBlob(manifest)) },
            SizeBytes = manifest.Length,
        };
        var staged = Path.Combine(downloads.Path, "X", "P.mlpackage");
        Directory.CreateDirectory(staged);
        File.WriteAllBytes(Path.Combine(staged, "Manifest.json"), manifest);
        var models = root.Sub("models");
        var store = new ModelStore(models, earlierDownloads: downloads.Path) { RequirementFor = _ => entry };
        Directory.CreateDirectory(Path.Combine(models, entry.Id, "P.mlpackage"));
        Assert.Equal(ModelVerification.Unverified, store.Verification(OnDeviceModel.Depth));
        Assert.Equal(ModelVerification.Verified, store.CheckEarlierInstall(OnDeviceModel.Depth));
        Assert.Contains("\"how\": \"earlier download\"", File.ReadAllText(store.ReceiptPath(OnDeviceModel.Depth)));
    }

    [Fact]
    public async Task ThePackageInstallerMovesFilesAndFolders()
    {
        using var folder = new TempFolder();
        var installer = new MovePackageInstaller();
        var dir = folder.Sub("staged-dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "new");
        var destination = folder.Sub("models/P.mlpackage");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.txt"), "old");
        await installer.InstallAsync(dir, destination, default);
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
        var single = folder.Sub("model.onnx");
        File.WriteAllText(single, "onnx");
        await installer.InstallAsync(single, folder.Sub("models/model.onnx"), default);
        Assert.Equal("onnx", File.ReadAllText(folder.Sub("models/model.onnx")));
        Assert.Equal("model.onnx", installer.InstalledName("model.onnx"));
    }

    [Fact]
    public void TheSourceIsHuggingFaceUnlessSettingsNamesAMirror()
    {
        Assert.Equal("https://huggingface.co/", ModelStore.SourceFrom(null).ToString());
        Assert.Equal("https://huggingface.co/", ModelStore.SourceFrom("ftp://mirror.example").ToString());
        Assert.Equal("https://mirror.example/hf", ModelStore.SourceFrom("  https://mirror.example/hf ").ToString());
        Assert.Equal("models.source", ModelStore.SourceKey);
    }

    [Fact]
    public void TheDownloadErrorKeepsWhatFollowsTheSystemsFiller()
    {
        Assert.Equal("This needs the model “M”, which Glimmer downloads once, and the download didn't work: (NSURLErrorDomain error -1009.). Connect to the internet and try again. Everything that doesn't need this model keeps working offline.",
                     new ModelDownloadError("M", "The operation couldn’t be completed. (NSURLErrorDomain error -1009.)").Message);
        Assert.Contains("didn't work: no connection. ", new ModelDownloadError("M", "  ").Message);
        Assert.Equal("The model “Depth (Depth Anything V2 Small)” is installed but couldn't be loaded (bad header). Remove it in Glimmer › Settings › Models and download it again.",
                     new ModelUnavailableError(OnDeviceModel.Depth, "bad header").Message);
        Assert.Equal("The download of “M” didn't match its published checksum (a.bin: 1 bytes, not 2), so Glimmer didn't install it. Nothing of it was kept. Try again later; if it keeps happening, check Settings › Models › Download from.",
                     new ModelChecksumError("M", "a.bin", "1 bytes, not 2").Message);
    }
}
