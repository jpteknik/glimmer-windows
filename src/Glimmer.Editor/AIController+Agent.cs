// Ported from App/AIController+Agent.swift
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>The assistant: plans with the text model, shows the plan, runs it step by step.</summary>
public sealed partial class AIController
{
    public const string AutoRunKey = "ai.autoRunPlans";
    public const string OnDeviceRoutingKey = "ai.routeOnDevice";
    public const string ShowImageKey = "ai.assistantSeesImage";
    public const string ReviewKey = "ai.reviewResults";

    private IAppSettings Settings => session.Environment.Settings;
    private readonly ISavedActionStore savedActionStore;
    private List<SavedAction> savedActions;
    private bool autoRun;
    private bool isPlanning;
    private bool isSuggesting;
    private IReadOnlyList<AgentPlanner.Suggestion> suggestions = [];
    internal readonly List<(string User, string Reply)> agentHistory = [];
    private CancellationTokenSource? planCancellation;
    /// <summary>What the step being run reports in its API result (info); reset before each step.</summary>
    internal JsonObject? StepInfo { get; set; }
    /// <summary>The sibling apps' agent APIs (Jämmer, Flimmer). They are Mac apps: on Windows none answers.</summary>
    internal SiblingClient Siblings { get; set; } = SiblingClient.Shared;

    public IReadOnlyList<ChatMessage> Messages => [.. messages.Select(m => m.Snapshot)];
    public IReadOnlyList<SavedAction> SavedActions => savedActions;
    public IReadOnlyList<AgentPlanner.Suggestion> Suggestions { get => suggestions; set { suggestions = value; Raise(); } }
    public bool IsSuggesting { get => isSuggesting; private set => Set(ref isSuggesting, value); }
    public bool IsPlanning { get => isPlanning; private set => Set(ref isPlanning, value); }

    /// <summary>The planner gets a small picture of the document (default on).</summary>
    private bool AssistantSeesImage => Settings.Get(ShowImageKey, true);
    /// <summary>After a plan ran, the model compares before and after and may adjust once (default on).</summary>
    private bool ReviewsResults => Settings.Get(ReviewKey, true);

    /// <summary>The document as a JPEG of at most 1024 px, for the text model to look at.</summary>
    internal static byte[]? SnapshotJPEG(DocumentState state)
    {
        try
        {
            using var flat = ImageExporter.FlattenedSRGB(state);
            var small = ImageExporter.Resized(flat, 1024);
            try
            {
                return ImageExporter.Encode(small, new ExportOptions(ExportFormat.Jpeg, quality: 0.8));
            }
            finally
            {
                if (!ReferenceEquals(small, flat)) small.Dispose();
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Try the on-device model first: one-command requests and "make a picture of …" don't need the paid
    /// planner.</summary>
    private bool RoutesOnDevice => OnDeviceRouter.IsAvailable && Settings.Get(OnDeviceRoutingKey, true);

    /// <summary>Runs plans without asking first. Plans that only use the editor's own tools always run straight away; this
    /// setting covers plans with AI jobs.</summary>
    public bool AutoRunAIPlans
    {
        get => autoRun;
        set
        {
            if (Set(ref autoRun, value)) Settings.Set(AutoRunKey, value);
        }
    }

    internal AgentContext AgentContext
    {
        get
        {
            var state = session.State;
            var layer = state.ActiveLayer;
            string kind = layer?.Content switch
            {
                RasterContent when layer.Raw is not null => "raw",   // a RAW layer: develop_raw is offered for it
                RasterContent => "raster",
                AdjustmentContent => "adjustment",
                GroupContent => "group",
                FillContent => "fill",
                SmartContent => "smart object",
                TextContent => "text",
                _ => "none",
            };
            return new AgentContext(state.Width, state.Height, state.AllLayers.Length, state.Selection is not null, kind, layer?.Name ?? "—");
        }
    }

    // MARK: Messages

    internal Message AddMessage(bool fromUser, UiText text, AgentPlan? plan = null, string? exactWords = null, Guid? id = null)
    {
        var message = new Message { Id = id ?? Guid.NewGuid(), FromUser = fromUser, Text = text, Plan = plan, ExactWords = exactWords };
        messages.Add(message);
        RaiseMessages();
        return message;
    }

    internal Message? FindMessage(Guid id) => messages.FirstOrDefault(m => m.Id == id);

    internal void RaiseMessages() => Raise(nameof(Messages));

    private void SetStepStatus(Guid stepId, StepStatus status)
    {
        stepStatus[stepId] = status;
        RaiseMessages();
    }

    private void SetPlanStatus(Guid messageId, PlanStatus status)
    {
        planStatus[messageId] = status;
        RaiseMessages();
    }

    public StepStatus StatusOfStep(Guid stepId) => stepStatus.TryGetValue(stepId, out var s) ? s : new StepStatus(StepStatusKind.Pending);
    public PlanStatus? StatusOfPlan(Guid messageId) => planStatus.TryGetValue(messageId, out var s) ? s : null;

    /// <summary>The person's language for a title from the register (the app's table).</summary>
    private string Localized(string english) => session.Environment.Localize?.Invoke(english) ?? english;

    /// <summary>Palette items the on-device router must never pick for a request.</summary>
    internal static bool NotRoutable(PaletteItem item)
    {
        if (item.Action is not PaletteAction.Command(var id)) return false;
        if (id == CommandID.ToggleGammaBlending || id == CommandID.BatchProcess) return true;
        if (WindowsCommands.Registry.Descriptor(id)?.Category is not { } category) return false;
        return category is CommandCategory.View or CommandCategory.File;
    }

    private IReadOnlyList<PaletteItem> PaletteItems => Palette.AllItems(session.Environment.Localize);

    public void Ask(string text)
    {
        var goal = text.Trim();
        if (goal.Length == 0 || IsPlanning) return;
        AddMessage(fromUser: true, UiText.Verbatim(goal));
#if DEBUG
        // Development hook: a raw JSON plan runs without the network (exercises the executor).
        if (goal.StartsWith('{'))
        {
            AgentPlan? parsed = null;
            try { parsed = AgentPlanner.ParsePlan(goal); }
            catch (Exception) { }
            if (parsed is not null)
            {
                Receive(parsed, goal);
                return;
            }
        }
#endif
        if (RoutesOnDevice)
        {
            IsPlanning = true;
            var cancel = NewPlanCancellation();
            _ = RouteOnDevice(goal, cancel.Token);
            return;
        }
        PlanWithTextModel(goal);
    }

    private CancellationTokenSource NewPlanCancellation()
    {
        var cancel = new CancellationTokenSource();
        planCancellation = cancel;
        return cancel;
    }

    private async Task RouteOnDevice(string goal, CancellationToken token)
    {
        var items = PaletteItems;
        // Commands that only open a dialog ("Vignette…") don't carry out a request by themselves, and settings, views and
        // files never do ("fix the colors" is not "Blend Colors in Gamma Space").
        var candidates = CommandSearch.Candidates(goal, items)
            .Where(i => !i.Title.EndsWith('…') && !NotRoutable(i))   // the register's own key, never the shown title; its ellipsis is the register's mark for "asks first"
            .Select(i => new OnDeviceRouter.Candidate(i.Id, i.Title, i.Subtitle)).ToList();
        OnDeviceRouter.Decision decision;
        try { decision = await Detached(() => OnDeviceRouter.RouteAsync(goal, candidates, token)); }
        catch (Exception) { decision = new OnDeviceRouter.Decision.Assistant(); }
        if (token.IsCancellationRequested)
        {
            IsPlanning = false;
            return;
        }
        switch (decision)
        {
            case OnDeviceRouter.Decision.Run run:
                // A command the request doesn't name is shown and waits for Run (PlanCheck).
                var item = items.FirstOrDefault(i => i.Id == run.Id);
                if (item is not null && !RequestNames(goal, item))
                {
                    IsPlanning = false;
                    var message = AddMessage(false, new UiText("I think you mean {0}. You didn't name it, so it waits for you.", new UiText(item.Title)));
                    message.PendingCommand = item.Id;
                    RaiseMessages();
                    agentHistory.Add((goal, $"Suggested {item.Title}."));
                    return;
                }
                if (item is not null && session.RunPaletteItem(item))
                {
                    IsPlanning = false;
                    AddMessage(false, new UiText("Done: {0} (on this computer, no AI cost).", new UiText(item.Title)));
                    agentHistory.Add((goal, $"Ran {item.Title}."));
                    return;
                }
                break;
            case OnDeviceRouter.Decision.Generate generate:
                if (HasAPIKey)
                {
                    IsPlanning = false;
                    AddMessage(false, new UiText("Generating “{0}” as a new layer.", generate.Prompt));
                    agentHistory.Add((goal, $"Generated a new layer: {generate.Prompt}"));
                    Generate(generate.Prompt, "", session.State.Width, session.State.Height);
                    return;
                }
                break;
        }
        IsPlanning = false;
        PlanWithTextModel(goal);
    }

    /// <summary>Sends the goal to the text model for a plan.</summary>
    private void PlanWithTextModel(string goal) => GateTextRequest(needsPicture: false, () => SendPlanRequest(goal));

    private void SendPlanRequest(string goal)
    {
        ITextModel model;
        try
        {
            model = TextModel();
        }
        catch (Exception error)
        {
            UiText why = error is OpenRouterError ? "Add your OpenRouter API key in Settings › AI to use the assistant." : PersonMessage.Text(error);
            AddMessage(false, why);
            return;
        }
        // What the service's own list says the model can't do is said before anything is sent.
        var provider = setup.TextProvider;
        var abilities = setup.ChosenTextModel(provider);
        if (TextModelOverride is null && abilities.Tools == false)
        {
            AddMessage(false, new UiText("{0} lists {1} as a model without tool calls, and the Assistant works by calling Glimmer's tools. Choose another model in Settings › AI.",
                                         provider.Name, abilities.Id));
            return;
        }
        bool canSee = TextModelOverride is not null || abilities.Vision != false;
        if (!canSee && !saidModelCantSee)
        {
            saidModelCantSee = true;
            AddMessage(false, new UiText("{0} can't take pictures, so the Assistant plans without looking at the document.", abilities.Id));
        }
        var context = AgentContext;
        var history = agentHistory.ToList();
        var state = session.State;
        bool seesImage = AssistantSeesImage;
        IsPlanning = true;
        // The reply streams into this message; Receive then attaches the plan.
        var messageId = Guid.NewGuid();
        var token = NewPlanCancellation().Token;
        var siblings = Siblings;
        _ = Plan();

        async Task Plan()
        {
            try
            {
                var image = canSee && seesImage ? await Detached(() => SnapshotJPEG(state)) : null;
                // Running siblings (Jämmer, Flimmer) add their operations as tools; found off the UI thread.
                var toolbox = await Detached(() => siblings.ToolboxAsync(token));
                var (plan, _) = await Detached(() => AgentPlanner.PlanAsync(goal, context, history, model, image, toolbox.Tools, toolbox.Context,
                    onText: text => Scheduler.Post(() =>
                    {
                        if (FindMessage(messageId) is { } streaming)
                        {
                            streaming.Text = UiText.Verbatim(text);
                            RaiseMessages();
                        }
                        else if (IsPlanning)
                        {
                            AddMessage(false, UiText.Verbatim(text), id: messageId);
                        }
                    }), ct: token));
                RemoveMessage(messageId);
                Receive(plan, goal);
            }
            catch (OperationCanceledException)
            {
                RemoveMessage(messageId);
            }
            catch (Exception error)
            {
                RemoveMessage(messageId);
                AddMessage(false, new UiText("I couldn't plan that: {0}", PersonMessage.Text(error)));
            }
            IsPlanning = false;
        }
    }

    private void RemoveMessage(Guid id)
    {
        if (messages.RemoveAll(m => m.Id == id) > 0) RaiseMessages();
    }

    /// <summary>Whether the request names this palette entry (its title, a search word of its command).</summary>
    internal bool RequestNames(string goal, PaletteItem item)
    {
        var keywords = new List<string>();
        if (item.Action is PaletteAction.Command(var id))
        {
            keywords.AddRange(WindowsCommands.Registry.Descriptor(id)?.Keywords ?? []);
            if (id == CommandID.AutoEnhance && PlanCheck.Words.TryGetValue("auto_enhance", out var words)) keywords.AddRange(words);
        }
        return PlanCheck.NamesCommand(goal, item.Title, keywords) || PlanCheck.NamesCommand(goal, Localized(item.Title), []);
    }

    internal static string? CommandTitle(string id) => WindowsCommands.Registry.Descriptor(new CommandID(id))?.Title;

    /// <summary>Runs the command a message is holding for confirmation.</summary>
    public void RunPendingCommand(Guid messageId)
    {
        if (FindMessage(messageId) is not { PendingCommand: { } id } message) return;
        message.PendingCommand = null;
        if (PaletteItems.FirstOrDefault(i => i.Id == id) is { } item && session.RunPaletteItem(item))
            message.Text = new UiText("Done: {0} (on this computer, no AI cost).", new UiText(item.Title));
        else
            message.Text = "That isn't available right now.";
        RaiseMessages();
    }

    public void DismissPendingCommand(Guid messageId)
    {
        if (FindMessage(messageId) is not { } message) return;
        message.PendingCommand = null;
        RaiseMessages();
    }

    private void Receive(AgentPlan received, string goal)
    {
        // A plan runs at once only when the request names every step that changes the document.
        var plan = received;
        bool named = plan.RunsAtOnce(goal, CommandTitle);
        // An extra that stands by itself (Auto Enhance beside what was asked) is held back as an offer, and what was
        // asked runs.
        List<AgentStep> offered = [];
        if (!named && plan.WithoutExtras(goal, CommandTitle) is { } split)
        {
            plan = split.Runs;
            offered = split.Offered;
            named = true;
        }
        // A step with a parameter its operation doesn't have would be refused part-way: the plan is shown and none of it runs.
        var own = plan.Steps.Select(StepParameters.StrayIn).FirstOrDefault(s => s is not null);
        string? refused = plan.StrayParameters.FirstOrDefault() ?? own?.Message;
        // A sentence saying something was done must not stand when it was not (PlanClaims). An answer in words, with no
        // step planned, is the model's own and stands. Above steps, the model's sentence stands only if it names nothing
        // beyond them; when no step is left, or the plan is refused, the app speaks alone.
        bool madeUp = received.DroppedOps.Length > 0;
        UiText? summary;
        string summaryEnglish;
        bool summaryIsModels = false;
        if (received.Steps.IsEmpty && !madeUp)
        {
            summaryIsModels = received.Message is not null;
            summary = received.Message is { } said ? UiText.Verbatim(said) : "I'm not sure how to do that.";
            summaryEnglish = summary.ToString();
        }
        else if (refused is not null || plan.Steps.IsEmpty)
        {
            summary = null;
            summaryEnglish = "";
        }
        else
        {
            var shown = PlanClaims.Shown(received.Message, [.. plan.Steps.Cast<IPlanStep>()], madeUp);
            summaryIsModels = shown is not null && shown == received.Message;
            summary = shown is not null ? UiText.Verbatim(shown) : "Here's the plan.";
            summaryEnglish = summary.ToString();
        }
        var parts = new List<UiText>();
        if (summary is not null && summaryEnglish.Length > 0) parts.Add(summary);
        if (!plan.RemovedFigures.IsEmpty && summaryEnglish.Length > 0 && summaryIsModels) parts.Add("Glimmer left out a figure it could not verify.");
        if (madeUp)
        {
            var list = string.Join(", ", received.DroppedOps);
            parts.Add(plan.Steps.IsEmpty
                ? new UiText("The assistant planned steps Glimmer doesn't have ({0}), so nothing was done.", list)
                : new UiText("The assistant also planned steps Glimmer doesn't have ({0}); they are left out.", list));
        }
        if (refused is not null)
        {
            // Said for a person; the API's exact words are kept under the message for whoever wants them.
            var subject = plan.StraySubject ?? own?.Subject ?? "";
            var name = plan.StrayName ?? own?.Name ?? "";
            parts.Add(new UiText("The plan asks {0} for a setting it doesn't have ({1}), so it can't run. Nothing was changed. Ask again, perhaps in other words.",
                                 new UiText(subject), name));
        }
        else if (!named && !plan.UsesAI)
        {
            parts.Add("Some of these steps aren't named in what you asked, so the plan waits for Run.");
        }
        var message = AddMessage(false, parts.Count == 1 ? parts[0] : AISetup.Sentences(parts), plan.Steps.IsEmpty ? null : plan, refused);
        agentHistory.Add((goal, (received.Message ?? summaryEnglish) + (plan.Steps.IsEmpty ? "" : " Steps: " + string.Join(", ", plan.Steps.Select(s => s.Op)))));
        if (plan.Steps.IsEmpty) return;
        if (refused is not null)
        {
            foreach (var step in plan.Steps) stepStatus[step.Id] = new StepStatus(StepStatusKind.Skipped);
            SetPlanStatus(message.Id, PlanStatus.Failed);
            return;
        }
        foreach (var step in plan.Steps) stepStatus[step.Id] = new StepStatus(StepStatusKind.Pending);
        SetPlanStatus(message.Id, PlanStatus.Proposed);
        // "Run at once" still waits for the once-per-service tick that the price is not known.
        if (named && (!plan.UsesAI || (AutoRunAIPlans && UnknownPriceAgreed))) RunPlan(message.Id);
        // Each held extra is a plan of its own that waits: it is run only if the person says so, and then on the picture
        // as it is by then.
        foreach (var step in offered)
        {
            var title = AgentPlanner.Op(step.Op) is { } op ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(op.Name.Replace('_', ' ')) : step.Op;
            var offer = AddMessage(false, new UiText("The assistant also suggests {0}, which you didn't ask for. Run it now on the picture as it is?", new UiText(title)),
                                   new AgentPlan([step]));
            stepStatus[step.Id] = new StepStatus(StepStatusKind.Pending);
            SetPlanStatus(offer.Id, PlanStatus.Proposed);
        }
    }

    public void RunPlan(Guid messageId)
    {
        if (FindMessage(messageId) is not { Plan: { } plan } message || StatusOfPlan(messageId) != PlanStatus.Proposed) return;
        SetPlanStatus(messageId, PlanStatus.Running);
        // The whole plan is one undo step, named after the request.
        int index = messages.IndexOf(message);
        var goal = messages.Take(index).LastOrDefault(m => m.FromUser)?.Text.ToString() ?? "";
        session.Document.BeginTransaction(UndoName(goal));
        var before = session.State;
        var token = NewPlanCancellation().Token;
        _ = Run();

        async Task Run()
        {
            // Corrections from the quality check land in the same undo step.
            try
            {
                bool failed = await RunSteps(plan.Steps, token);
                SetPlanStatus(messageId, token.IsCancellationRequested ? PlanStatus.Cancelled : failed ? PlanStatus.Failed : PlanStatus.Finished);
                // A plan that stopped part-way: the app's own list of what was done and what was not takes the sentence's place.
                if (failed || token.IsCancellationRequested)
                {
                    message.Text = WhatWasDone(plan.Steps);
                    RaiseMessages();
                }
                if (!failed && !token.IsCancellationRequested) await Review(goal, plan.Steps, before, token);
            }
            finally
            {
                session.Document.EndTransaction();
            }
        }
    }

    /// <summary>The app's own account of a plan that stopped part-way, from each step's state.</summary>
    internal UiText WhatWasDone(IReadOnlyList<AgentStep> steps)
    {
        var done = steps.Where(s => StatusOfStep(s.Id).Kind == StepStatusKind.Done).Select(StepDescription.Describe).ToList();
        var not = steps.Where(s => StatusOfStep(s.Id).Kind != StepStatusKind.Done).Select(StepDescription.Describe).ToList();
        var notList = StepDescription.ListAnd(not);
        if (done.Count == 0) return new UiText("Nothing was done. Not done: {0}.", notList);
        return new UiText("Done: {0}. Not done: {1}.", StepDescription.ListAnd(done), notList);
    }

    /// <summary>Runs steps in order; returns true when one failed (the rest are skipped).</summary>
    private async Task<bool> RunSteps(IReadOnlyList<AgentStep> steps, CancellationToken token)
    {
        bool failed = false;
        foreach (var step in steps)
        {
            if (token.IsCancellationRequested || failed)
            {
                SetStepStatus(step.Id, new StepStatus(StepStatusKind.Skipped));
                continue;
            }
            SetStepStatus(step.Id, new StepStatus(StepStatusKind.Running));
            try
            {
                await Execute(step);
                SetStepStatus(step.Id, new StepStatus(StepStatusKind.Done));
            }
            catch (Exception error)
            {
                failed = true;
                // The plan is the window's: in the person's language.
                SetStepStatus(step.Id, new StepStatus(StepStatusKind.Failed, PersonMessage.Text(error)));
                NoteSiblingFailure(error);
            }
        }
        return failed;
    }

    // MARK: Agent API

    /// <summary>One step's outcome as the agent API reports it.</summary>
    internal sealed record APIStepResult(string Op, string Status, string? Error, string? Reason = null, bool Hard = false,
                                         IReadOnlyList<string>? Requirements = null, JsonObject? Info = null);

    /// <summary>Runs steps from the local agent API as one undo step. Stops at the first failure and reports the rest as
    /// skipped.</summary>
    internal async Task<List<APIStepResult>> RunAPISteps(IReadOnlyList<AgentStep> steps, bool allowUnknownPrice = false,
                                                         Action<int>? progress = null, CancellationToken cancellation = default)
    {
        AgentAllowsUnknownPrice = allowUnknownPrice;
        RunningAPISteps = true;
        session.CommitLiveEdit();
        session.Document.BeginTransaction("Agent Edit");   // a catalogue key, like every action name: translated where the window shows it
        var results = new List<APIStepResult>();
        try
        {
            bool stopped = false;
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (stopped || cancellation.IsCancellationRequested)
                {
                    stopped = true;
                    results.Add(new APIStepResult(step.Op, cancellation.IsCancellationRequested ? "canceled" : "skipped", null));
                    continue;
                }
                try
                {
                    StepInfo = null;
                    await Execute(step);
                    results.Add(new APIStepResult(step.Op, "done", null, Info: StepInfo));
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    results.Add(new APIStepResult(step.Op, "canceled", null));
                }
                catch (ModelUnavailableError unavailable)
                {
                    stopped = true;
                    results.Add(new APIStepResult(step.Op, "failed", AgentAPI.Words(unavailable), "model_unavailable",
                                                  Requirements: [unavailable.Model.Requirement().Id]));
                }
                catch (ModelMissingError missing)
                {
                    // Only reached if a model went away after the request was checked: never downloaded here.
                    stopped = true;
                    results.Add(new APIStepResult(step.Op, "failed", AgentAPI.Words(missing), "model_missing",
                                                  Requirements: [.. missing.Missing.Select(m => m.Requirement().Id)]));
                }
                catch (Exception error)
                {
                    stopped = true;
                    var stepError = error as StepError;
                    results.Add(new APIStepResult(step.Op, "failed", AgentAPI.Words(error), stepError?.Reason, stepError?.Hard ?? false));
                }
                progress?.Invoke(i + 1);
            }
        }
        finally
        {
            session.Document.EndTransaction();
            AgentAllowsUnknownPrice = false;
            RunningAPISteps = false;
        }
        return results;
    }

    /// <summary>Cancels the AI jobs this window is running (used when an API job is cancelled).</summary>
    internal void CancelRunningJobs()
    {
        foreach (var job in jobs.Where(j => j.Status == AIJobStatusKind.Running).ToList()) Cancel(job.Id);
    }

    /// <summary>The quality check: the model compares before and after and may adjust once.</summary>
    private async Task Review(string goal, IReadOnlyList<AgentStep> steps, DocumentState before, CancellationToken token)
    {
        if (!ReviewsResults || goal.Length == 0) return;
        ITextModel model;
        try { model = TextModel(); }
        catch (Exception) { return; }
        var after = session.State;
        IsPlanning = true;
        try
        {
            var (b, a) = await Detached(() => (SnapshotJPEG(before), SnapshotJPEG(after)));
            if (b is null || a is null) return;
            AgentPlan plan;
            try { plan = await Detached(() => AgentPlanner.ReviewAsync(goal, steps, b, a, model, token)); }
            catch (Exception) { return; }
            if (token.IsCancellationRequested) return;
            // Approved: nothing to add to the conversation.
            if (plan.Steps.IsEmpty) return;
            var message = AddMessage(false, plan.Message is { } said ? UiText.Verbatim(said) : "Adjusting the result.", plan);
            agentHistory.Add((goal, "Adjusted: " + string.Join(", ", plan.Steps.Select(s => s.Op))));
            foreach (var step in plan.Steps) stepStatus[step.Id] = new StepStatus(StepStatusKind.Pending);
            SetPlanStatus(message.Id, PlanStatus.Running);
            bool failed = await RunSteps(plan.Steps, token);
            SetPlanStatus(message.Id, token.IsCancellationRequested ? PlanStatus.Cancelled : failed ? PlanStatus.Failed : PlanStatus.Finished);
        }
        finally
        {
            IsPlanning = false;
        }
    }

    // MARK: Suggestions

    /// <summary>Asks the model to look at the image and propose three edits.</summary>
    public void SuggestEdits() => GateTextRequest(needsPicture: true, SendSuggestionRequest);

    private void SendSuggestionRequest()
    {
        if (IsSuggesting) return;
        ITextModel model;
        try { model = TextModel(); }
        catch (Exception) { return; }
        var state = session.State;
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        IsSuggesting = true;
        _ = Run();

        async Task Run()
        {
            IReadOnlyList<AgentPlanner.Suggestion> found = [];
            try
            {
                var image = await Detached(() => SnapshotJPEG(state));
                if (image is not null)
                {
                    try { found = await Detached(() => AgentPlanner.SuggestionsAsync(image, language, model)); }
                    catch (Exception) { found = []; }
                }
            }
            finally
            {
                Suggestions = found;
                IsSuggesting = false;
            }
        }
    }

    // MARK: Saved actions

    /// <summary>Whether this plan is the quality check's follow-up to the plan before it. It is saved together with that
    /// plan, not by itself.</summary>
    public bool IsCorrection(Guid messageId)
    {
        int index = messages.FindIndex(m => m.Id == messageId);
        if (index <= 0) return false;
        var previous = messages[index - 1];
        return !previous.FromUser && previous.Plan is not null;
    }

    /// <summary>Keeps a finished plan (and any corrections that followed it) as a reusable action.</summary>
    public void SaveAction(Guid messageId)
    {
        int index = messages.FindIndex(m => m.Id == messageId);
        if (index < 0 || messages[index].Plan is not { } plan) return;
        var steps = plan.Steps.ToList();
        // Corrections from the quality check directly after belong to the same result.
        foreach (var later in messages.Skip(index + 1))
        {
            if (later.FromUser || later.Plan is not { } extra) break;
            steps.AddRange(extra.Steps);
        }
        var goal = messages.Take(index).LastOrDefault(m => m.FromUser)?.Text.ToString() ?? plan.Message ?? "Action";
        var name = new StringInfo(goal).LengthInTextElements > 48 ? new StringInfo(goal).SubstringByTextElements(0, 47) + "…" : goal;
        savedActions.RemoveAll(a => a.Name == name);
        savedActions.Add(new SavedAction(name, steps));
        StoreSavedActions();
    }

    public void DeleteSavedAction(Guid id)
    {
        if (savedActions.RemoveAll(a => a.Id == id) > 0) StoreSavedActions();
    }

    private void StoreSavedActions()
    {
        Glimmer.AI.SavedActions.Store(savedActions, savedActionStore);
        Raise(nameof(SavedActions));
    }

    /// <summary>Runs a saved action again: no planning, one undo step.</summary>
    public void Run(SavedAction action)
    {
        if (IsPlanning) return;
        var steps = action.Steps.Select(s => new AgentStep(s.Op, s.Params, s.Rationale)).ToList();
        AddMessage(fromUser: true, UiText.Verbatim(action.Name));
        var plan = new AgentPlan(steps);
        var message = AddMessage(false, "Running the saved action.", plan);
        foreach (var step in steps) stepStatus[step.Id] = new StepStatus(StepStatusKind.Pending);
        SetPlanStatus(message.Id, PlanStatus.Running);
        session.Document.BeginTransaction(UiText.Verbatim(action.Name));
        var token = NewPlanCancellation().Token;
        _ = RunIt();

        async Task RunIt()
        {
            try
            {
                bool failed = await RunSteps(steps, token);
                SetPlanStatus(message.Id, token.IsCancellationRequested ? PlanStatus.Cancelled : failed ? PlanStatus.Failed : PlanStatus.Finished);
            }
            finally
            {
                session.Document.EndTransaction();
            }
        }
    }

    /// <summary>A saved action run without the window's conversation (Batch, the command line): its steps as one undo step;
    /// false when a step failed.</summary>
    internal async Task<bool> RunQuietly(SavedAction action, CancellationToken token = default)
    {
        var steps = action.Steps.Select(s => new AgentStep(s.Op, s.Params, s.Rationale)).ToList();
        session.Document.BeginTransaction(UiText.Verbatim(action.Name));
        try { return !await RunSteps(steps, token); }
        finally { session.Document.EndTransaction(); }
    }

    /// <summary>"Assistant: make it warmer" (shortened).</summary>
    internal static UiText UndoName(string goal)
    {
        var info = new StringInfo(goal);
        var shortGoal = info.LengthInTextElements > 40 ? info.SubstringByTextElements(0, 39) + "…" : goal;
        return shortGoal.Length == 0 ? "Assistant" : new UiText("Assistant: {0}", shortGoal);
    }

    public void CancelPlan(Guid messageId)
    {
        if (StatusOfPlan(messageId) == PlanStatus.Running)
        {
            planCancellation?.Cancel();
            CancelRunningJobs();
        }
        if (StatusOfPlan(messageId) == PlanStatus.Proposed) SetPlanStatus(messageId, PlanStatus.Cancelled);
    }

    // MARK: Executor

    /// <summary>Why a step was refused, in the words an agent is told.</summary>
    internal sealed class StepError(string message, string? reason = null) : Exception(message)
    {
        /// <summary>A machine-readable reason (the API adds it to its 422 step_failed answer).</summary>
        public string? Reason { get; } = reason;
        /// <summary>True when no parameter of the step lifts the refusal (the API passes it on as hard).</summary>
        public bool Hard { get; init; }
    }

    /// <summary>Runs one step; AI steps wait until their job has finished.</summary>
    private async Task Execute(AgentStep step)
    {
        // Refused before anything is applied, whoever sent the step (the API checks a whole request first; a plan says it
        // in the step).
        if (StepParameters.StrayIn(step) is { } stray) throw new StepError(stray.Message, "unknown_parameter");
        var s = session;
        var before = jobs.FirstOrDefault()?.Id;
        switch (step.Op)
        {
            case "add_adjustment":
            {
                if (AdjustmentKind.FromRawValue(step.String("type") ?? "") is not { } kind) throw new StepError("Unknown adjustment.");
                var adjustment = AdjustmentFrom(kind, step.Object("params"));
                s.AddLayer(new Layer(LayerName(kind.Title()), new AdjustmentContent(adjustment)), new UiText("New {0}", new UiText(kind.Title())));
                break;
            }
            case "apply_filter":
            {
                if (FilterKind.FromRawValue(step.String("type") ?? "") is not { } kind) throw new StepError("Unknown filter.");
                EnsurePixelLayer();
                var values = step.Object("params").Where(p => p.Value.AsNumber is not null)
                    .Select(p => KeyValuePair.Create(p.Key, p.Value.AsNumber!.Value));
                s.ApplyFilter(new FilterSettings(kind, values));
                break;
            }
            case "add_layer_effect":
                AddLayerEffect(step.String("type") ?? "", step.Object("params"));
                break;
            case "apply_preset":
            {
                var name = (step.String("name") ?? "").ToLowerInvariant();
                var preset = Preset.All.FirstOrDefault(p => p.Name.ToLowerInvariant() == name || p.Id == name)
                    ?? throw new StepError($"Unknown preset “{name}”.");
                ApplyPreset(preset);
                break;
            }
            case "add_text":
                AddText(step);
                break;
            case "image_edit_composite":
                EditImage(step.String("instruction") ?? "");
                break;
            case "generate_layer":
                Generate(step.String("prompt") ?? "", "", s.State.Width, s.State.Height);
                break;
            case "auto_enhance":
                await PerformAutoEnhanceAsync();
                break;
            case "auto_color":
                await PerformAutoColorAsync();
                break;
            case "select_people":
                RequireModels("select_people");
                await PerformSelectPeopleAsync();
                break;
            case "select_sky":
                RequireModels("select_sky");
                if (!await PerformSelectSkyAsync()) throw new StepError("No sky found along the top of the image.");
                break;
            case "remove_background":
                RequireModels("remove_background");
                // A smart object (an image an agent just added) is cut out as it is; only a layer without a picture of its
                // own sends the step to the nearest pixel layer.
                if (s.State.ActiveLayer?.Content is not SmartContent) EnsurePixelLayer();
                RemoveBackground();
                break;
            case "select_subject":
                RequireModels("select_subject");
                SelectSubject();
                break;
            case "inpaint_selection":
                if (s.State.Selection is null) throw new StepError("This step needs a selection.");
                Fill(step.String("mode") == "remove" ? AIPipelines.InpaintMode.Remove : AIPipelines.InpaintMode.Fill, step.String("prompt") ?? "", null);
                break;
            case "upscale":
            {
                int scale = (step.Number("scale") ?? 2) >= 3 ? 4 : 2;
                Upscale(scale, neural: Math.Max(s.State.Width, s.State.Height) * scale <= 16_384, enhance: false, creativity: 0);
                break;
            }
            case "fill":
            {
                if (HexColors.Parse(step.String("color")) is not { } color) throw new StepError("Invalid color.");
                EnsurePixelLayer();
                FillSelection(color);
                break;
            }
            case "set_foreground":
            {
                if (HexColors.Parse(step.String("color")) is not { } color) throw new StepError("Invalid color.");
                s.Foreground = color;
                break;
            }
            case "select_all": s.Perform(CommandID.SelectAll); break;
            case "deselect": s.Perform(CommandID.Deselect); break;
            case "invert_selection": s.Perform(CommandID.InvertSelection); break;
            case "feather_selection":
                if (s.State.Selection is null) throw new StepError("There's no selection to feather.");
                s.ModifySelection(CommandID.FeatherSelection, step.Number("radius") ?? 10);
                break;
            case "select_layer":
            {
                var key = (step.String("name") ?? "").Trim();
                var layer = FindLayer(key) ?? throw new StepError($"No layer named “{key}”.");
                s.SetActiveLayer(layer.Id);
                break;
            }
            case "new_layer":
                s.Perform(CommandID.NewLayer);
                if (step.String("name") is { } layerName && s.State.ActiveLayerID is { } activeId) s.Rename(activeId, layerName);
                break;
            case "add_waveform":
                await PerformAddWaveform(step);
                break;
            case "transform_layer":
                PerformTransformLayer(step);
                break;
            case "style_text":
                PerformStyleText(step);
                break;
            case "crop":
                PerformCrop(step);
                break;
            case "resize_image":
                PerformResizeImage(step);
                break;
            case "resize_canvas":
                PerformResizeCanvas(step);
                break;
            case "select_rect":
                PerformSelectRect(step);
                break;
            case "set_layer":
                PerformSetLayer(step);
                break;
            case "develop_raw":
                await PerformDevelopRaw(step);
                break;
            case "heal_selection":
                await PerformHealSelection(step);
                break;
            case var op when SiblingApps.Split(op) is not null:
                await PerformSiblingTool(step);
                break;
            case "run_command":
            {
                var id = new CommandID(step.String("id") ?? "");
                if (!AgentPlanner.RunnableCommands.Any(d => d.Id == id)) throw new StepError("Unknown command.");
                if (!s.CanPerform(id)) throw new StepError($"“{id.RawValue}” isn't available right now.");
                s.Perform(id);
                break;
            }
            default:
                throw new StepError("Unknown step.");
        }
        // Wait for any AI job the step started.
        if (jobs.FirstOrDefault() is { } job && job.Id != before)
        {
            await job.Finished.Task;
            if (job.Status == AIJobStatusKind.Failed) throw new StepError(job.FailureForAgent ?? job.Failure?.ToString() ?? "The job failed.");
            if (job.Status == AIJobStatusKind.Cancelled) throw new OperationCanceledException();
        }
    }

    /// <summary>The models an operation needs, from the register: never downloaded here. In the window the Assistant's step
    /// asks the person; from the API it is only reported.</summary>
    private void RequireModels(string op)
    {
        var missing = ModelStore.Shared.Missing(ModelsFor(op));
        if (missing.Count == 0) return;
        if (!RunningAPISteps) ModelRequest = new ModelDownloadRequest(missing, null);
        throw new ModelMissingError(missing);
    }

    /// <summary>A layer by its id or its name, the topmost first.</summary>
    internal Layer? FindLayer(string key) =>
        session.State.AllLayers.Reverse().FirstOrDefault(l =>
            string.Equals(l.Id.ToString(), key, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Filters and fills need pixels: fall back to the topmost visible pixel layer.</summary>
    private void EnsurePixelLayer()
    {
        if (session.State.ActiveLayer?.IsRaster == true) return;
        var layer = session.State.AllLayers.Reverse().FirstOrDefault(l => l.IsRaster && l.IsVisible && !l.Tiles.IsEmpty)
            ?? throw new StepError("There's no pixel layer to work on.");
        session.SetActiveLayer(layer.Id);
    }

    private void FillSelection(SRGBColor color)
    {
        if (session.State.ActiveLayer is not { } layer) return;
        var bounds = session.State.Selection?.Bounds(session.State.Bounds) ?? session.State.Bounds;
        var tiles = new PixelOps().Run(PixelOps.Mode.Paint, layer.Tiles, selection: session.State.Selection, coords: bounds.TileCoords,
                                       color: new System.Numerics.Vector4(color.Linear, 1));
        session.Edit("Fill", s => s.UpdateLayer(layer.Id, l => l with { Tiles = tiles }));
    }

    private void AddText(AgentStep step)
    {
        var state = session.State;
        double size = step.Number("size") ?? Math.Max(state.Height * 0.08, 12);
        var text = new TextLayer(step.String("text") ?? "Text", default, size: size,
                                 color: HexColors.Parse(step.String("color")) ?? session.Foreground, alignment: TextAlignment.Center);
        if (step.String("fontName")?.Trim() is { Length: > 0 } font)
        {
            if (!TextLayer.FontExists(font)) throw new StepError($"No font named “{font}” is installed (use its PostScript name).", "unknown_font");
            text = text with { FontName = font };
        }
        if (step.Get("bold")?.AsBool == true) text = text.SetBold(true, new TextRange(0, text.String.Length)).Text;
        var layout = text.LayoutSize;
        double y = step.String("position") switch
        {
            "top" => state.Height * 0.08,
            "bottom" => state.Height * 0.92 - layout.Height,
            _ => (state.Height - layout.Height) / 2,
        };
        text = text with { Origin = new Vec2(step.Number("x") ?? (state.Width - layout.Width) / 2, step.Number("y") ?? y) };
        var info = new StringInfo(text.String);
        var name = info.LengthInTextElements > 32 ? info.SubstringByTextElements(0, 32) : text.String;
        var layer = new Layer(name, new TextContent(text));
        session.AddLayer(layer, "Add Text");
        var result = new JsonObject { ["layer"] = layer.Id.ToString().ToUpperInvariant() };
        if (AgentRouter.LayerBounds(layer) is { } b) result["bounds"] = RectJson(b);
        StepInfo = result;
    }

    internal static JsonObject RectJson(PixelRect r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };

    internal static Adjustment AdjustmentFrom(AdjustmentKind kind, ImmutableDictionary<string, AgentValue> parameters)
    {
        var adjustment = new Adjustment(kind);
        foreach (var (k, v) in parameters)
        {
            if (v.AsBool is { } b) adjustment = adjustment with { Flags = adjustment.Flags.SetItem(k, b) };
            else if (v.AsNumber is { } n) adjustment = adjustment with { Numbers = adjustment.Numbers.SetItem(k, n) };
            if (HexColors.Parse(v.AsString) is { } c) adjustment = adjustment with { Colors = adjustment.Colors.SetItem(k, c) };
        }
        return adjustment;
    }

    /// <summary>Applies a web-style effect patch to a layer's styles: enabled → IsEnabled, hex colours → SRGBColor. A value
    /// of the wrong kind leaves the effect as it was (the original decodes the patched effect or keeps it).</summary>
    internal static LayerEffects Patched(LayerEffects effects, string type, ImmutableDictionary<string, AgentValue> parameters) => type switch
    {
        "dropShadow" => effects with { DropShadow = Patch(effects.DropShadow ?? new ShadowEffect(), parameters) },
        "innerShadow" => effects with { InnerShadow = Patch(effects.InnerShadow ?? new ShadowEffect(), parameters) },
        "outerGlow" => effects with { OuterGlow = Patch(effects.OuterGlow ?? new GlowEffect(), parameters) },
        "stroke" => effects with { Stroke = Patch(effects.Stroke ?? new StrokeEffect(), parameters) },
        "colorOverlay" => effects with { ColorOverlay = Patch(effects.ColorOverlay ?? new ColorOverlayEffect(), parameters) },
        _ => throw new StepError("Unknown layer effect."),
    };

    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Patch<T>(T value, ImmutableDictionary<string, AgentValue> parameters) where T : class
    {
        var copy = (T)CloneMethod.Invoke(value, null)!;
        foreach (var (key, v) in parameters)
        {
            var name = key == "enabled" ? "isEnabled" : key;
            var property = typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            // A key the effect doesn't have is passed over, as the decoder does.
            if (property is null || !property.CanWrite) continue;
            object? converted = null;
            var t = property.PropertyType;
            if (t == typeof(SRGBColor)) converted = HexColors.Parse(v.AsString);
            else if (t == typeof(double)) converted = v.AsNumber;
            else if (t == typeof(bool)) converted = v.AsBool;
            else if (t.IsEnum && v.AsString is { } raw && Enum.TryParse(t, raw, ignoreCase: true, out var parsed)) converted = parsed;
            if (converted is null) return value;
            property.SetValue(copy, converted);
        }
        return copy;
    }

    private void AddLayerEffect(string type, ImmutableDictionary<string, AgentValue> parameters)
    {
        if (session.State.ActiveLayer is not { } layer) throw new StepError("Select a layer first.");
        var effects = Patched(layer.Effects, type, parameters);
        session.Edit("Layer Style", s => s.UpdateLayer(layer.Id, l => l with { Effects = effects }));
    }
}

/// <summary>A step's name in the plan, as the menus call it where the menus have it (PlanView.describe).</summary>
internal static class StepDescription
{
    private static readonly Dictionary<string, string> Effects = new()
    {
        ["dropShadow"] = "Drop Shadow", ["innerShadow"] = "Inner Shadow", ["outerGlow"] = "Outer Glow", ["stroke"] = "Stroke", ["colorOverlay"] = "Color Overlay",
    };

    private static readonly Dictionary<string, string> MenuTitles = new()
    {
        ["crop"] = "Crop", ["resize_image"] = "Image Size…", ["resize_canvas"] = "Canvas Size…", ["auto_enhance"] = "Auto Enhance", ["auto_color"] = "Auto Color",
        ["select_people"] = "Select People", ["select_sky"] = "Select Sky", ["select_subject"] = "Select Subject", ["remove_background"] = "Remove Background",
        ["transform_layer"] = "Free Transform", ["add_waveform"] = "Add Waveform", ["deselect"] = "Deselect",
        ["heal_selection"] = "Heal Selection", ["develop_raw"] = "Develop…",
    };

    private static UiText Menu(string title) => new(title.Replace("…", ""));

    private static UiText Plain(string? s) => Menu(CultureInfo.InvariantCulture.TextInfo.ToTitleCase((s ?? "").Replace('_', ' ')));

    public static UiText Describe(AgentStep step)
    {
        string S(string key) => step.String(key) ?? "";
        return step.Op switch
        {
            "add_adjustment" => AdjustmentKind.FromRawValue(S("type")) is { } a ? Menu(a.Title()) : "Adjustment",
            "apply_filter" => FilterKind.FromRawValue(S("type")) is { } f ? Menu(f.Title()) : "Filter",
            "add_layer_effect" => new UiText("Layer style: {0}", Effects.TryGetValue(S("type"), out var e) ? Menu(e) : "Effect"),
            "apply_preset" => new UiText("Preset: {0}", S("name")),
            "add_text" => new UiText("Text “{0}”", S("text")),
            "style_text" => step.String("match") is { } m ? new UiText("Style “{0}”", m) : "Style text",
            "image_edit_composite" => new UiText("AI edit: {0}", S("instruction")),
            "generate_layer" => new UiText("Generate: {0}", S("prompt")),
            "select_layer" => new UiText("Select layer “{0}”", S("name")),
            "inpaint_selection" => S("mode") == "remove" ? "Remove selected object" : new UiText("Fill selection: {0}", S("prompt")),
            "upscale" => new UiText("Upscale {0}×", (int)(step.Number("scale") ?? 2)),
            "fill" => new UiText("Fill with {0}", S("color")),
            "set_foreground" => new UiText("Foreground {0}", S("color")),
            "run_command" => WindowsCommands.Registry.Descriptor(new CommandID(S("id"))) is { } d ? Menu(d.Title) : Plain(step.String("id")),
            _ => MenuTitles.TryGetValue(step.Op, out var t) ? Menu(t) : Plain(step.Op),
        };
    }

    /// <summary>"A, B and C" (Swift's list(type: .and)), each part translated on its own.</summary>
    public static UiText ListAnd(IReadOnlyList<UiText> items) => items.Count switch
    {
        0 => UiText.Verbatim(""),
        1 => items[0],
        2 => new UiText("{0} and {1}", items[0], items[1]),
        _ => new UiText(string.Join(", ", Enumerable.Range(0, items.Count - 1).Select(i => $"{{{i}}}")) + $", and {{{items.Count - 1}}}",
                        items.Cast<object?>().ToArray()),
    };
}
