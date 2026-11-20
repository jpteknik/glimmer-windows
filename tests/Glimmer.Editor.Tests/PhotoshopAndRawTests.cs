// Tests for the document parts of File › Export › Photoshop File… (PhotoshopDocument) and Layer › Develop…
// (RawDevelopModel, from App/RawDevelopSheet.swift).
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor.Tests;

public class PhotoshopAndRawTests
{
    [Fact]
    public void APhotoshopExportIsAPhotoshopFileWithItsReport()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare(200, 100));
        Assert.Equal(s.State.BlendSpace == BlendSpace.Linear, s.Photoshop.BlendsInLinearLight);
        var (data, report) = h.Scheduler.Await(s.Photoshop.PreparePSDExportAsync(8, flattened: false, CancellationToken.None));
        Assert.Equal("8BPS"u8.ToArray(), data.Take(4));
        Assert.True(report.LayersWritten >= 1);
        // It reads back as the same document size.
        var back = DocumentOpener.ImportPhotoshopFile(data);
        Assert.Equal((200, 100), (back.Width, back.Height));
    }

    [Fact]
    public void OnlyALayerWithARawFileCanBeDevelopedAgain()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        Assert.Null(s.MakeRawDevelopModel());
        // A layer that says it has a RAW file the decoder can't read gives no model either (the sheet says why).
        s.Edit("Raw", st => st.UpdateLayer(st.ActiveLayerID!.Value, l => l with { Raw = new RawSource([1, 2, 3], "x.dng", new RawSettings()) }));
        Assert.Null(s.MakeRawDevelopModel());
    }
}
