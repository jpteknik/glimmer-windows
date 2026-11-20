// New in the port (the Mac has no segmentation tests of its own: Vision needs no download there). What can be checked
// without the models: each feature asks for exactly the models it needs, and nothing is fetched. The real models are
// tested in OnDeviceModelTests (GLIMMER_MODELS_DIR); DownloadsEveryModelFromItsPinnedCommit downloads them, only when
// GLIMMER_DOWNLOAD_MODELS names a folder.
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.AI.Tests;

public class SegmentationTests
{
    /// <summary>A store with nothing installed, which ignores GLIMMER_MODELS_DIR and can reach no server.</summary>
    private static ModelStore EmptyStore(TempFolder root) => new(root.Sub("models"), handler: new ModelDownloadTests.SampleServer(new())) { TestModels = null };

    [Fact]
    public async Task RemoveBackgroundNeedsTheSubjectModel()
    {
        using var root = new TempFolder();
        var store = EmptyStore(root);
        var error = await Assert.ThrowsAsync<ModelMissingError>(() => SubjectSegmentation.SubjectMaskAsync(TestPictures.Mug(), store));
        Assert.Equal(new[] { OnDeviceModel.Subject }, error.Missing);
        Assert.Equal("This needs the model “Subject (IS-Net)”, which isn't downloaded yet. Download it in Glimmer › Settings › Models, then try again.", error.Message);
    }

    [Fact]
    public async Task SelectPeopleNamesBothItsModels()
    {
        using var root = new TempFolder();
        var store = EmptyStore(root);
        var error = await Assert.ThrowsAsync<ModelMissingError>(() => SubjectSegmentation.PeopleMaskAsync(TestPictures.Mug(), store));
        Assert.Equal(new[] { OnDeviceModel.PersonDetector, OnDeviceModel.PersonMatting }, error.Missing);
    }

    [Fact]
    public async Task DepthAndMagicSelectNeedTheirModels()
    {
        using var root = new TempFolder();
        var store = EmptyStore(root);
        await Assert.ThrowsAsync<ModelMissingError>(() => DepthEstimator.DepthAsync(TestPictures.Mug(), store));
        var selector = new MagicSelector(store);
        await Assert.ThrowsAsync<ModelMissingError>(() => selector.PrepareAsync(TestPictures.Mug(), key: 1));
        // Nothing prepared: segmenting is refused before any model is looked for.
        await Assert.ThrowsAsync<InvalidOperationException>(() => selector.SegmentAsync([new MagicSelector.Point(new Vec2(1, 1), positive: true)]));
    }

    [Fact]
    public void TheNoSubjectErrorSaysSo()
    {
        Assert.Equal("No subject was found in the image.", SegmentationError.NoSubject().Message);
        Assert.Equal(SegmentationError.NoSubject(), SegmentationError.NoSubject());
    }

    /// <summary>The depth model's input keeps the picture's shape: longer side 518, both sides multiples of 14.</summary>
    [Fact]
    public void DepthInputKeepsTheShape()
    {
        Assert.Equal((518, 392), DepthEstimator.InputSize(4000, 3000));
        Assert.Equal((294, 518), DepthEstimator.InputSize(3000, 5300));
        Assert.Equal((518, 518), DepthEstimator.InputSize(100, 100));
        var (w, h) = DepthEstimator.InputSize(7000, 100);
        Assert.True(w == 518 && h == 14);
    }

    /// <summary>
    /// Downloads every on-device model from Hugging Face at its pinned commit through the store's own download path,
    /// checking every file: only when GLIMMER_DOWNLOAD_MODELS names a folder (it reaches the network). The folder is
    /// then the one GLIMMER_MODELS_DIR names for OnDeviceModelTests.
    /// </summary>
    [DownloadFact]
    public async Task DownloadsEveryModelFromItsPinnedCommit()
    {
        var store = new ModelStore(Environment.GetEnvironmentVariable("GLIMMER_DOWNLOAD_MODELS"));
        foreach (var model in OnDeviceModels.AllCases)
        {
            if (store.Verification(model) != ModelVerification.Verified) await store.DownloadAsync(model);
            Assert.Equal(ModelVerification.Verified, store.Verification(model));
        }
    }
}
