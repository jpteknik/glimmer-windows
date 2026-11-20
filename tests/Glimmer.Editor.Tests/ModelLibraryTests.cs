// Tests for ModelLibrary.cs (the Models pane of App/SettingsView.swift and App/ModelDownloadSheet.swift over Glimmer.AI's
// ModelStore). No test reaches the network: downloads go to a fake transport.
using System.Net;
using Glimmer.AI;

namespace Glimmer.Editor.Tests;

[Collection(SharedModelStoreCollection.Name)]
public class ModelLibraryTests
{
    /// <summary>Answers every request with <paramref name="status"/>, and counts them.</summary>
    private sealed class FakeTransport(HttpStatusCode status) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });
        }
    }

    private static Harness WithTransport(FakeTransport transport, out EditorEnvironment environment)
    {
        var h = new Harness();
        environment = new EditorEnvironment(h.Settings, h.Scheduler, h.Host, h.Dialogs, h.Clipboard, h.Sound, h.PathIn("temp"), h.PathIn("data"))
        {
            Http = transport,
        };
        return h;
    }

    [Fact]
    public void ModelsLiveInTheDataFolderAndCountAsInstalledWhenTheirPackagesAreThere()
    {
        using var h = WithTransport(new FakeTransport(HttpStatusCode.NotFound), out var environment);
        var library = new ModelLibrary(environment);
        Assert.Equal(Path.Combine(h.PathIn("data"), "Models"), library.Folder);
        var model = OnDeviceModel.Depth;
        Assert.False(library.IsInstalled(model));
        Assert.True(library.NeedsDownload(model));
        Assert.Equal(ModelVerification.NotInstalled, library.Verification(model));
        // Installed without a receipt (copied by hand, or Glimmer 0.3.0's): usable, offered again to check.
        foreach (var package in model.Packages())
            Directory.CreateDirectory(Path.Combine(library.Folder, model.Requirement().Id, package));
        Assert.True(library.IsInstalled(model));
        Assert.Equal(ModelVerification.Unverified, library.Verification(model));
        Assert.True(library.NeedsDownload(model));
        // The features load from the same store.
        Assert.True(ModelStore.Shared.IsInstalled(model));
        var changes = 0;
        library.PropertyChanged += (_, _) => changes++;
        library.Remove(model);
        Assert.False(library.IsInstalled(model));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task AFailedDownloadSaysWhyAndInstallsNothing()
    {
        var transport = new FakeTransport(HttpStatusCode.NotFound);
        using var h = WithTransport(transport, out var environment);
        var library = new ModelLibrary(environment);
        var reports = new List<double>();
        var error = await Assert.ThrowsAsync<ModelDownloadError>(() =>
            library.DownloadAsync(OnDeviceModel.ImageLabels, new Progress<double>(reports.Add), CancellationToken.None));
        Assert.Contains(OnDeviceModel.ImageLabels.Title(), error.Message);
        Assert.False(library.IsInstalled(OnDeviceModel.ImageLabels));
        // It asked Hugging Face (the default source), through the given transport only.
        Assert.NotEmpty(transport.Requests);
        Assert.All(transport.Requests, uri => Assert.Equal("huggingface.co", uri.Host));
    }

    [Fact]
    public async Task TheSourceSettingPointsDownloadsAtAMirror()
    {
        var transport = new FakeTransport(HttpStatusCode.NotFound);
        using var h = WithTransport(transport, out var environment);
        var library = new ModelLibrary(environment);
        Assert.Equal("", library.Source);
        Assert.Equal(new Uri(ModelStore.DefaultSource), library.SourceAddress);
        library.Source = "  https://mirror.example/models  ";
        Assert.Equal("https://mirror.example/models", h.Settings.Get(ModelStore.SourceKey, ""));
        await Assert.ThrowsAsync<ModelDownloadError>(() =>
            library.DownloadAsync(OnDeviceModel.ImageLabels, new Progress<double>(), CancellationToken.None));
        Assert.All(transport.Requests, uri => Assert.Equal("mirror.example", uri.Host));
        // Not an address: Hugging Face.
        library.Source = "not a url";
        Assert.Equal(new Uri(ModelStore.DefaultSource), library.SourceAddress);
    }
}
