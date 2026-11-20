// Tests for the port of App/AIController.swift and its extensions: jobs and their costs, the paid picture tools against a
// picture service that answers from memory, the on-device features' model questions, the assistant's plans and saved
// actions, the agent steps, the text gate, and Heal Selection. No test reaches a network or a real model.
using System.Net.Http;
using Glimmer.AI;
using Glimmer.Core;

namespace Glimmer.Editor.Tests;

[Collection(SharedModelStoreCollection.Name)]
public class AIControllerTests
{
    private static (Harness H, EditorSession S, SharedModelStore Models) Start(DocumentState? state = null)
    {
        var h = new Harness();
        var s = h.NewSession(state ?? Docs.TwoTone());
        return (h, s, new SharedModelStore(h));
    }

    [Fact]
    public void GenerateAddsThePictureAsALayerAndCountsItsCost()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        var service = new FakeImageModel { Width = 800, Height = 600, Cost = 0.04 };
        s.AI.ImageModelOverride = () => service;
        int layersBefore = s.State.Layers.Length;
        s.AI.Generate("  a red barn  ", "", 800, 600);
        h.Scheduler.RunPosted();
        var job = Assert.Single(s.AI.Jobs);
        Assert.Equal(AIJobStatusKind.Done, job.Status);
        Assert.Equal("Generate “{0}”", job.Title.Template);
        Assert.Equal("a red barn", job.Title.Args[0]);
        Assert.True(job.Paid);
        Assert.Equal(0.04m, s.AI.TotalCost);
        Assert.Equal(0, s.AI.JobsWithoutPrice);
        Assert.Equal(layersBefore + 1, s.State.Layers.Length);
        // Fitted inside the 400 × 300 document and centred: one undo step of its own.
        var layer = s.State.ActiveLayer!;
        Assert.Equal("Generated", layer.Name);
        Assert.Equal(new PixelRect(0, 0, 400, 300), layer.Tiles.ContentBounds(new TileBoundsCache()));
        Assert.Equal("Generate Image", s.Document.UndoManager.UndoActionName);
        Assert.Equal(["a red barn"], service.Prompts.Select(p => p.Contains("a red barn") ? "a red barn" : p));
    }

    [Fact]
    public void APaidJobThatFailsAfterTheServiceWasReachedMayHaveBeenCharged()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        s.AI.ImageModelOverride = () => new FakeImageModel { Failure = new InvalidOperationException("The service answered with no picture.") };
        s.AI.EditImage("make it night");
        h.Scheduler.RunPosted();
        var job = Assert.Single(s.AI.Jobs);
        Assert.Equal(AIJobStatusKind.Failed, job.Status);
        Assert.True(job.MayHaveBeenCharged);
        Assert.Equal(1, s.AI.JobsWithoutPrice);
        Assert.Equal("The service answered with no picture.", s.ErrorMessage?.ToString());
        // One that never got a connection can't have been billed.
        s.AI.ClearFinished();
        s.AI.ImageModelOverride = () => new FakeImageModel { Failure = new HttpRequestException(HttpRequestError.ConnectionError, "No connection.") };
        s.AI.EditImage("make it night");
        h.Scheduler.RunPosted();
        Assert.False(Assert.Single(s.AI.Jobs).MayHaveBeenCharged);
        Assert.Equal(0, s.AI.JobsWithoutPrice);
    }

    [Fact]
    public void FillNeedsASelectionAndWorksInsideIt()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        s.AI.ImageModelOverride = () => new FakeImageModel();
        s.AI.Fill(AIPipelines.InpaintMode.Remove, "", null);
        Assert.Equal("Make a selection first (M), then try again.", s.ErrorMessage?.ToString());
        Assert.Empty(s.AI.Jobs);
        s.Edit("Select", st => st with { Selection = Selection.Rectangle(new CGRectLike(100, 100, 180, 160)) });
        s.AI.Fill(AIPipelines.InpaintMode.Remove, "", null);
        h.Scheduler.RunPosted();
        Assert.Equal(AIJobStatusKind.Done, s.AI.Jobs[0].Status);
        Assert.Equal("Remove object", s.AI.Jobs[0].Title.ToString());
        Assert.Equal("Removed", s.State.ActiveLayer!.Name);
    }

    [Fact]
    public void OnDeviceFeaturesAskForTheModelsTheRegisterLists()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        s.AI.RemoveBackground();
        Assert.Equal([OnDeviceModel.Subject], s.AI.ModelRequest?.Models);
        s.AI.ModelRequest = null;
        s.AI.SelectSubject();
        Assert.Equal([OnDeviceModel.Subject], s.AI.ModelRequest?.Models);
        s.AI.ModelRequest = null;
        s.AI.SelectPeople();
        Assert.Equal([OnDeviceModel.PersonDetector, OnDeviceModel.PersonMatting], s.AI.ModelRequest?.Models);
        s.AI.ModelRequest = null;
        s.AI.SelectSky();
        Assert.Equal([OnDeviceModel.Depth, OnDeviceModel.SegmentAnything], s.AI.ModelRequest?.Models);
        s.AI.ModelRequest = null;
        s.AI.BeginLensBlur(new Glimmer.Render.DocumentEffects.LensBlurParameters());
        Assert.Equal([OnDeviceModel.Depth], s.AI.ModelRequest?.Models);
        // Nothing ran and nothing changed: the person is asked first.
        Assert.Empty(s.AI.Jobs);
        Assert.Empty(s.Document.History);
        Assert.NotNull(s.AI.ModelRequest?.Retry);
    }

    [Fact]
    public void TextInPicturesWithoutARecognizerSaysWhatToInstall()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        var before = OnDeviceServices.TextRecognizer;
        OnDeviceServices.TextRecognizer = null;
        try
        {
            s.AI.CopyImageText();
            h.Scheduler.RunUntil(() => s.ErrorMessage is not null);
            Assert.Equal(new TextRecognitionUnavailableError().Message, s.ErrorMessage?.ToString());
            Assert.Null(h.Clipboard.Text);
        }
        finally
        {
            OnDeviceServices.TextRecognizer = before;
        }
    }

    [Fact]
    public void AutoEnhanceAddsAnEditableGroupOrSaysThereIsNothingToDo()
    {
        var (h, s, models) = Start(Docs.Gray(0.35f));
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        s.AI.AutoEnhance();
        h.Scheduler.RunUntil(() => s.Document.History.Count > 0 || s.Toast is not null);
        if (s.Toast is { } toast) Assert.Equal("The image already looks balanced. Auto Enhance found nothing to change.", toast.ToString());
        else Assert.Contains(s.State.Layers, l => l.Name == "Auto Enhance" && l.IsGroup && l.Children.All(c => c.IsAdjustment));
    }

    private const string VibrancePlan = """{"message": "Adding vibrance.", "steps": [{"op": "add_adjustment", "params": {"type": "vibrance"}}]}""";

    [Fact]
    public void APlanRunsAsOneUndoStepAndCanBeSavedAndRunAgain()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        s.AI.Ask(VibrancePlan);
        var reply = s.AI.Messages[^1];
        Assert.False(reply.FromUser);
        Assert.NotNull(reply.Plan);
        if (s.AI.StatusOfPlan(reply.Id) == PlanStatus.Proposed) s.AI.RunPlan(reply.Id);
        h.Scheduler.RunUntil(() => s.AI.StatusOfPlan(reply.Id) is PlanStatus.Finished or PlanStatus.Failed);
        Assert.Equal(PlanStatus.Finished, s.AI.StatusOfPlan(reply.Id));
        Assert.Equal(StepStatusKind.Done, s.AI.StatusOfStep(reply.Plan!.Steps[0].Id).Kind);
        Assert.Contains(s.State.Layers, l => l.Content is AdjustmentContent { Adjustment.Kind: AdjustmentKind.Vibrance });
        Assert.Single(s.Document.History);
        Assert.StartsWith("Assistant: ", s.Document.UndoManager.UndoActionName);
        // Saved, it is kept in the settings under the Mac's own key and runs again without planning.
        s.AI.SaveAction(reply.Id);
        var saved = Assert.Single(s.AI.SavedActions);
        Assert.Contains("add_adjustment", h.Settings.Get(SettingsSavedActionStore.Key, ""));
        s.AI.Run(saved);
        h.Scheduler.RunUntil(() => s.Document.History.Count == 2);
        Assert.Equal(2, s.State.Layers.Count(l => l.IsAdjustment));
        Assert.Equal(saved.Name, s.Document.UndoManager.UndoActionName);
        s.AI.DeleteSavedAction(saved.Id);
        Assert.Empty(s.AI.SavedActions);
    }

    [Fact]
    public void APlanWithAnUnknownSettingIsShownAndNotRun()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        s.AI.Ask("""{"message": "Cropping.", "steps": [{"op": "crop", "params": {"widht": 100, "height": 100}}]}""");
        var reply = s.AI.Messages[^1];
        Assert.Equal(PlanStatus.Failed, s.AI.StatusOfPlan(reply.Id));
        Assert.Equal(StepStatusKind.Skipped, s.AI.StatusOfStep(reply.Plan!.Steps[0].Id).Kind);
        Assert.NotNull(reply.ExactWords);
        Assert.Contains("widht", reply.ExactWords);
        Assert.Empty(s.Document.History);
    }

    [Fact]
    public void APlanFromTheTextModelStreamsIntoTheConversation()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        h.Settings.Set(AIController.OnDeviceRoutingKey, false);
        h.Settings.Set(AIController.ReviewKey, false);
        // Told what goes to the service, and agreed that it states no price (else the notices wait for the person).
        h.Settings.Set(AISetup.ToldKey("openrouter"), true);
        h.Settings.Set(AISetup.ToldPicturesKey("openrouter"), true);
        h.Settings.Set(AISetup.TextUnknownPriceKey("openrouter"), true);
        var model = new FakeTextModel();
        model.ToolReplies.Enqueue(new TextToolReply("Adding vibrance.", [new TextToolCall("add_adjustment", """{"type": "vibrance"}""")], 0.002));
        s.AI.TextModelOverride = () => model;
        s.AI.Ask("add vibrance");
        h.Scheduler.RunUntil(() => !s.AI.IsPlanning && s.AI.Messages.Any(m => m.Plan is not null));
        var reply = s.AI.Messages.Last(m => m.Plan is not null);
        Assert.Equal("add_adjustment", reply.Plan!.Steps[0].Op);
        h.Scheduler.RunUntil(() => s.AI.StatusOfPlan(reply.Id) is PlanStatus.Finished or PlanStatus.Failed or PlanStatus.Proposed);
        if (s.AI.StatusOfPlan(reply.Id) == PlanStatus.Proposed) s.AI.RunPlan(reply.Id);
        h.Scheduler.RunUntil(() => s.AI.StatusOfPlan(reply.Id) == PlanStatus.Finished);
        Assert.Contains(s.State.Layers, l => l.IsAdjustment);
        Assert.True(model.Calls >= 1);
    }

    [Fact]
    public void AgentStepsReportEachStepAndStopAtTheFirstFailure()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        AgentStep[] steps =
        [
            new("add_adjustment", ("type", "vibrance")),
            new("select_sky"),
            new("deselect"),
        ];
        var results = h.Scheduler.Await(s.AI.RunAPISteps(steps));
        Assert.Equal(["done", "failed", "skipped"], results.Select(r => r.Status));
        Assert.Equal("model_missing", results[1].Reason);
        Assert.Equal(Requirement.ModelsFor("select_sky", null), results[1].Requirements);
        // From the API nothing is asked in the window.
        Assert.Null(s.AI.ModelRequest);
        // The done step stays, as one undo step named for the agent.
        Assert.Equal("Agent Edit", s.Document.UndoManager.UndoActionName);
    }

    [Fact]
    public void AgentStepsRefuseUnknownStepsAndSettings()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        using var ui = h.Scheduler.AsUIThread();
        var unknown = h.Scheduler.Await(s.AI.RunAPISteps([new AgentStep("juggle")]));
        Assert.Equal("Unknown step.", unknown[0].Error);
        var stray = h.Scheduler.Await(s.AI.RunAPISteps([new AgentStep("feather_selection", ("radius", 4), ("feathr", 2))]));
        Assert.Equal("unknown_parameter", stray[0].Reason);
        var text = h.Scheduler.Await(s.AI.RunAPISteps([new AgentStep("add_text", ("text", "Hello"), ("position", "top"))]));
        Assert.Equal("done", text[0].Status);
        Assert.Equal("Hello", s.State.ActiveLayer!.Name);
        Assert.NotNull(text[0].Info?["bounds"]);
    }

    [Fact]
    public void TheTextGateRefusesAPictureToAModelThatTakesNone()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        h.Environment.Keys.StoreFor("openrouter").Write("sk-or-test");
        h.Settings.Set(AIDefaults.TextModelKey, "some/text-only-model");
        h.Settings.Set(AIDefaults.TextModelKey + ".vision", false);
        s.AI.FindDistractions();
        Assert.Equal(TextNoticeKind.CantSee, s.TextNotice?.Kind);
        Assert.Null(s.TextNotice?.Proceed);
        Assert.Empty(s.AI.Jobs);
    }

    [Fact]
    public void TheFirstRequestToACloudServiceAsksThenThePriceIsAskedOnce()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        h.Environment.Keys.StoreFor("openrouter").Write("sk-or-test");
        s.AI.SuggestEdits();
        var first = s.TextNotice;
        Assert.Equal(TextNoticeKind.FirstUse, first?.Kind);
        Assert.Equal("Send this to {0}?", first!.Title.Template);
        first.Proceed!();
        Assert.True(h.Settings.Get(AISetup.ToldKey("openrouter"), false));
        Assert.True(h.Settings.Get(AISetup.ToldPicturesKey("openrouter"), false));
        // No price is listed: asked once the first notice has gone.
        h.Scheduler.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TextNoticeKind.UnknownPrice, s.TextNotice?.Kind);
        Assert.False(s.AI.IsSuggesting);
    }

    [Fact]
    public void HealSelectionFillsTheSelectedAreaInANewLayer()
    {
        var (h, s, models) = Start(Docs.TwoTone());
        using var _ = h;
        using var __ = models;
        s.AI.HealSelection();
        Assert.Equal("There's no selection to heal. Select the area to fill first.", s.ErrorMessage?.ToString());
        s.Edit("Select", st => st with { Selection = Selection.Rectangle(new CGRectLike(40, 40, 60, 60)) });
        s.AI.HealSelection();
        h.Scheduler.RunPosted();
        Assert.Equal(AIJobStatusKind.Done, s.AI.Jobs[0].Status);
        var heal = s.State.ActiveLayer!;
        Assert.Equal("Heal", heal.Name);
        Assert.False(heal.Tiles.IsEmpty);
    }

    [Fact]
    public void ADistractionCanBeSelectedAndDismissed()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        var item = new AIPipelines.Distraction("d1", "Sign", "Draws the eye", "medium", new AIPipelines.DistractionBox(0.1, 0.1, 0.2, 0.2));
        s.AI.SelectDistractions([item]);
        Assert.Equal(new PixelRect(40, 30, 80, 60), s.State.Selection!.Bounds(s.State.Bounds));
        Assert.Equal("Select Distractions", s.Document.UndoManager.UndoActionName);
    }

    [Fact]
    public void WhatWasDoneListsTheStepsByTheirMenuNames()
    {
        var (h, s, models) = Start();
        using var _ = h;
        using var __ = models;
        AgentStep[] steps = [new("auto_enhance"), new("crop"), new("select_people")];
        var text = s.AI.WhatWasDone(steps);
        Assert.Equal("Nothing was done. Not done: Auto Enhance, Crop, and Select People.", text.ToString());
    }
}
