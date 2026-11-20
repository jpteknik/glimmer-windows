// Tests for the port of the Layers panel actions of App/EditorSession.swift, App/EditorSession+Filters.swift (filters
// with live preview, smart filters) and the thumbnails and navigator the panels show. The original has no tests at this
// level.
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

public class LayerAndFilterTests
{
    private static string[] Steps(EditorSession s) => [.. s.Document.History.Select(t => t.ToString())];

    [Fact]
    public void LayersPanelActions()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        var background = s.State.Named("Background").Id;
        var square = s.State.Named("Square").Id;
        // Choosing a layer isn't a step.
        s.SetActiveLayer(background);
        Assert.Equal(background, s.State.ActiveLayerID);
        Assert.Empty(s.Document.History);
        s.SetVisibility(square, false);
        Assert.False(s.State.LayerById(square)!.IsVisible);
        s.Rename(square, "  Box  ");
        Assert.Equal("Box", s.State.LayerById(square)!.Name);
        s.Rename(square, "   ");
        s.Rename(square, "Box");
        s.SetBlendMode(BlendMode.Multiply);
        s.SetBlendMode(BlendMode.Multiply);
        Assert.Equal(BlendMode.Multiply, s.State.LayerById(background)!.BlendMode);
        s.MoveLayer(background, square);
        Assert.Equal(background, s.State.Layers[^1].Id);
        Assert.Equal(["Hide Layer", "Rename Layer", "Change Blend Mode", "Reorder Layers"], Steps(s));
        s.Perform(CommandID.NewGroup);
        var group = s.State.ActiveLayerID!.Value;
        s.MoveLayerIntoGroup(square, group);
        Assert.Contains(s.State.LayerById(group)!.Subtree, l => l.Id == square);
        // Disclosure is saved but isn't a step.
        int steps = s.Document.History.Count;
        s.SetExpanded(group, false);
        Assert.False(((GroupContent)s.State.LayerById(group)!.Content).Group.IsExpanded);
        Assert.Equal(steps, s.Document.History.Count);
    }

    [Fact]
    public void ThumbnailsFollowTheLayersOnceEditsPause()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        h.Scheduler.Settle();
        Assert.Equal(2, s.Thumbnails.Count);
        var square = s.State.Named("Square").Id;
        var before = s.Thumbnails[square];
        s.Perform(CommandID.AddMaskRevealAll);
        h.Scheduler.Settle();
        Assert.Contains(square, s.MaskThumbnails.Keys);
        s.NudgeActiveLayer(30, 0);
        h.Scheduler.Settle();
        Assert.NotEqual(before.Id, s.Thumbnails[square].Id);
        Assert.True(s.Thumbnails[square].Width <= 128 && s.Thumbnails[square].Height <= 128);
    }

    [Fact]
    public void TheNavigatorShowsASmallPicture()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.White(1600, 1200));
        Assert.Null(s.NavigatorImage);
        s.Perform(CommandID.ToggleNavigator);
        s.Perform(CommandID.NewLayer);
        h.Scheduler.Settle();
        var image = Assert.IsType<RgbaImage>(s.NavigatorImage);
        Assert.Equal((480, 360), (image.Width, image.Height));
        // BGRA, white.
        Assert.Equal(255, image.Bgra.Span[0]);
    }

    [Fact]
    public void LevelsHistogramIsOfWhatIsBelow()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewAdjustment(AdjustmentKind.Levels));
        var levels = s.State.ActiveLayerID!.Value;
        var done = s.RefreshLevelsHistogram(levels);
        h.Scheduler.Settle();
        Assert.True(done.IsCompleted);
        Assert.Equal(levels, s.LevelsHistogram!.LayerId);
    }

    [Fact]
    public void AFilterPreviewsLiveAndAppliesAsOneStep()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var blur = CommandID.Filter(FilterKind.GaussianBlur);
        Assert.True(s.CanPerform(blur));
        Assert.False(s.CanPerform(CommandID.RepeatFilter));
        s.Perform(blur);
        Assert.Equal(FilterKind.GaussianBlur, s.FilterDialog!.Kind);
        Assert.True(s.IsPreviewingFilter);
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(s.IsPreviewingFilter);
        Assert.False(s.Overrides.Pixels.IsEmpty);
        Assert.Empty(s.Document.History);
        s.ApplyFilter(s.FilterDialog!);
        Assert.Null(s.FilterDialog);
        Assert.True(s.Overrides.Pixels.IsEmpty);
        Assert.Equal([FilterKind.GaussianBlur.Title()], Steps(s));
        Assert.True(Docs.Pixel(s.State.Layers[0], 199, 150).X > 0.01);
        // Repeat Filter runs it again without the dialog.
        Assert.True(s.CanPerform(CommandID.RepeatFilter));
        s.Perform(CommandID.RepeatFilter);
        Assert.Equal(2, s.Document.History.Count);
    }

    [Fact]
    public void ACancelledFilterLeavesNothing()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        var before = s.State;
        s.Perform(CommandID.Filter(FilterKind.GaussianBlur));
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(100));
        s.CancelFilterDialog();
        h.Scheduler.Settle();
        Assert.Same(before, s.State);
        Assert.True(s.Overrides.Pixels.IsEmpty);
        Assert.Null(s.FilterDialog);
        Assert.Empty(s.Document.History);
        // Preview switched off in the dialog: nothing shows, the dialog stays.
        s.Perform(CommandID.Filter(FilterKind.GaussianBlur));
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(100));
        s.StopFilterPreview();
        Assert.True(s.Overrides.Pixels.IsEmpty);
        Assert.NotNull(s.FilterDialog);
    }

    [Fact]
    public void FiltersOnSmartObjectsAreSmartFilters()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.WithSquare());
        s.Perform(CommandID.ConvertToSmartObject);
        var id = s.State.ActiveLayerID!.Value;
        Assert.IsType<SmartContent>(s.ActiveLayer!.Content);
        s.Perform(CommandID.Filter(FilterKind.GaussianBlur));
        // The preview is the filter list itself, for now.
        Assert.Single(((SmartContent)s.ActiveLayer!.Content).Smart.Filters);
        s.ApplyFilter(s.FilterDialog!);
        Assert.Equal(["Convert to Smart Object", "Smart Filter"], Steps(s));
        Assert.Single(((SmartContent)s.State.LayerById(id)!.Content).Smart.Filters);
        s.SetSmartFilterEnabled(id, 0, false);
        Assert.False(((SmartContent)s.State.LayerById(id)!.Content).Smart.Filters[0].IsEnabled);
        // Editing it again replaces its settings.
        s.EditSmartFilter(id, 0);
        Assert.NotNull(s.FilterDialog);
        s.ApplyFilter(s.FilterDialog! with { });
        Assert.Single(((SmartContent)s.State.LayerById(id)!.Content).Smart.Filters);
        s.DeleteSmartFilter(id, 0);
        Assert.Empty(((SmartContent)s.State.LayerById(id)!.Content).Smart.Filters);
        // Cancelling an edit puts the list back as it was.
        s.Perform(CommandID.Filter(FilterKind.GaussianBlur));
        s.CancelFilterDialog();
        Assert.Empty(((SmartContent)s.State.LayerById(id)!.Content).Smart.Filters);
    }

    [Fact]
    public void FiltersRefuseLayersWithoutPixels()
    {
        using var h = new Harness();
        var s = h.NewSession(Docs.TwoTone());
        s.Perform(CommandID.NewSolidFill);
        Assert.False(s.CanPerform(CommandID.Filter(FilterKind.GaussianBlur)));
        s.ApplyFilter(new FilterSettings(FilterKind.GaussianBlur));
        Assert.Equal("Filters work on pixel layers and smart objects. Rasterize the layer first.", s.ErrorMessage?.ToString());
    }
}
