// Ported from App/AIController.swift
//
// Swift's @MainActor class with Task.detached work becomes a class used on the UI thread whose work runs through the
// session's IUiScheduler (RunInBackground: the thread pool in the app, the test's own hand in the tests), with the result
// applied back on the UI thread. Where the original awaits (the assistant's plans, the agent API), the awaits resume
// through the UI thread's SynchronizationContext, as they do on the main actor.
using System.Net.Http;
using System.Net.Sockets;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;

namespace Glimmer.Editor;

/// <summary>Runs AI jobs for one document window. Every result lands as a new layer (or a mask/selection), so the
/// original pixels are never touched.</summary>
public sealed partial class AIController : ObservableObject, IAIController
{
    internal sealed class Job
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required UiText Title { get; init; }
        public AIJobStatusKind Status { get; set; } = AIJobStatusKind.Running;
        /// <summary>Set when the job failed: why, in the person's language.</summary>
        public UiText? Failure { get; set; }
        public double? Cost { get; set; }
        /// <summary>A service bills for this job (a picture service, or a text service that isn't on this computer).</summary>
        public bool Paid { get; init; }
        /// <summary>A paid job that failed after the request had gone to the service: the service may have billed it, and
        /// Glimmer can't know.</summary>
        public bool MayHaveBeenCharged { get; set; }
        /// <summary>Why it failed, in the words an agent is told (English); Failure carries them in the person's language.</summary>
        public string? FailureForAgent { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        /// <summary>Completes (on the UI thread) when the job has finished, whichever way (Swift: await job.task?.value).</summary>
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Called on the UI thread when the job has finished, with how.</summary>
        public Action<AIJobStatusKind>? Ended { get; set; }

        public AIJob Snapshot => new(Id, Title, Status, Failure, Cost is { } c ? (decimal)c : null, Paid, MayHaveBeenCharged);
    }

    internal sealed class Message
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required bool FromUser { get; init; }
        public required UiText Text { get; set; }
        /// <summary>The assistant's plan, shown as a checklist under the message.</summary>
        public AgentPlan? Plan { get; set; }
        /// <summary>A single command the on-device model chose for a request that doesn't name it: it waits for Run. The
        /// palette entry's id; null once it has been run or dismissed.</summary>
        public string? PendingCommand { get; set; }
        /// <summary>The exact words of a refusal, as the agent API gives them: shown on request under a message that says
        /// the same thing plainly.</summary>
        public string? ExactWords { get; set; }

        public ChatMessage Snapshot => new(Id, FromUser, Text, Plan, ExactWords, PendingCommand);
    }

    private readonly List<Job> jobs = [];
    internal readonly List<Message> messages = [];
    internal readonly Dictionary<Guid, StepStatus> stepStatus = [];
    internal readonly Dictionary<Guid, PlanStatus> planStatus = [];
    private readonly EditorSession session;
    private readonly AISetup setup;

    public AIController(EditorSession session)
    {
        this.session = session;
        setup = new AISetup(session.Environment);
        savedActionStore = new SettingsSavedActionStore(session.Environment.Settings);
        savedActions = [.. Glimmer.AI.SavedActions.Load(savedActionStore)];
        autoRun = session.Environment.Settings.Get(AutoRunKey, false);
    }

    public EditorSession Session => session;
    internal AISetup Setup => setup;
    private IUiScheduler Scheduler => session.Environment.Scheduler;

    /// <summary>The model every picture job goes to; tests give their own instead of a service's.</summary>
    internal Func<IImageModel>? ImageModelOverride { get; set; }
    /// <summary>The model every text request goes to; tests give their own instead of a service's.</summary>
    internal Func<ITextModel>? TextModelOverride { get; set; }

    // MARK: Setup

    public bool HasAPIKey => setup.HasAPIKey;
    /// <summary>Whether the picture service chosen in Settings has a key saved (OpenRouter unless another is chosen).</summary>
    public bool HasImageKey => ImageModelOverride is not null || setup.HasImageKey;
    /// <summary>Whether a paid picture job may start from the window: the price is known, or the person has made the tick
    /// for this service.</summary>
    public bool UnknownPriceAgreed => ImageModelOverride is not null || setup.UnknownPriceAgreed;
    /// <summary>Whether a text request can be made at all (a key where one is needed, and a model).</summary>
    public bool HasTextModel
    {
        get
        {
            if (TextModelOverride is not null) return true;
            try
            {
                TextModel();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Said once per window: the chosen text model can't take pictures.</summary>
    internal bool saidModelCantSee;

    /// <summary>The model every text request goes to (the Assistant, suggestions, Find Distractions). The one place a
    /// text service is chosen; nothing else constructs one.</summary>
    internal ITextModel TextModel() => TextModelOverride?.Invoke() ?? setup.TextModel();

    /// <summary>The last stop before a paid picture job; the buttons that lead here are grey until the tick is made.</summary>
    private IImageModel ImageModel() => ImageModelOverride?.Invoke() ?? setup.ImageModel(AgentAllowsUnknownPrice);

    /// <summary>True while steps from the agent API run with "allowUnknownPrice": true: that call's own word, never
    /// remembered.</summary>
    internal bool AgentAllowsUnknownPrice { get; set; }

    /// <summary>True while steps from the agent API run: a missing model is then only reported, never asked about in the
    /// window.</summary>
    public bool RunningAPISteps { get; set; }

    private ModelDownloadRequest? modelRequest;
    /// <summary>Models a feature needs, waiting for the person's answer in the window (ModelDownloadSheet).</summary>
    public ModelDownloadRequest? ModelRequest { get => modelRequest; set => Set(ref modelRequest, value); }

    /// <summary>Runs <paramref name="action"/> now when every model in <paramref name="models"/> is installed; otherwise
    /// asks the person whether to download them, and runs it after (ModelDownloadSheet.swift's withModels). From the agent
    /// API nothing is asked: the request was refused before it started.</summary>
    internal void WithModels(IReadOnlyList<OnDeviceModel> models, Action action)
    {
        var missing = ModelStore.Shared.Missing(models);
        if (missing.Count == 0)
        {
            action();
            return;
        }
        if (RunningAPISteps) return;
        ModelRequest = new ModelDownloadRequest(missing, action);
    }

    /// <summary>What a feature needs downloaded: the register's entries for the agent op (or run_command id).</summary>
    internal static IReadOnlyList<OnDeviceModel> ModelsFor(string op, string? command = null) =>
        [.. (Requirement.ModelsFor(op, command) ?? []).Select(OnDeviceModels.With).OfType<OnDeviceModel>()];

    /// <summary>Asks for the models <paramref name="op"/> needs when one is missing (and runs <paramref name="retry"/>
    /// once they are there); true when they are all installed and the caller can go on.</summary>
    private bool HasModels(string op, Action retry)
    {
        var models = ModelsFor(op);
        if (ModelStore.Shared.Missing(models).Count == 0) return true;
        WithModels(models, retry);
        return false;
    }

    // MARK: Jobs

    public IReadOnlyList<AIJob> Jobs => [.. jobs.Select(j => j.Snapshot)];
    public decimal TotalCost => (decimal)jobs.Sum(j => j.Cost ?? 0);
    /// <summary>Finished paid jobs whose service's answer carried no cost: never summed as if they had one, and counted
    /// beside the sum. So are paid jobs that failed after the service was reached: they may have been charged.</summary>
    public int JobsWithoutPrice =>
        jobs.Count(j => j.Paid && (j.Cost ?? 0) <= 0 && (j.Status == AIJobStatusKind.Done || j.MayHaveBeenCharged));
    public bool IsBusy => jobs.Any(j => j.Status == AIJobStatusKind.Running);

    internal IReadOnlyList<Job> JobList => jobs;

    private void RaiseJobs() => Raise(nameof(Jobs), nameof(TotalCost), nameof(JobsWithoutPrice), nameof(IsBusy));

    /// <summary>Runs <paramref name="work"/> away from the UI thread; <paramref name="apply"/> lands the result on the UI
    /// thread. A failure in either is the job's.</summary>
    internal Job RunJob<T>(UiText title, Func<CancellationToken, Task<(T Value, double? Cost)>> work, Action<T> apply, bool paid = false)
    {
        var job = new Job { Title = title, Paid = paid };
        var token = job.Cancellation.Token;
        jobs.Insert(0, job);
        if (jobs.Count > 30) jobs.RemoveAt(jobs.Count - 1);
        RaiseJobs();
        (T Value, double? Cost) result = default;
        Exception? error = null;
        Scheduler.RunInBackground(() =>
        {
            // On a thread of its own: the work's awaits never wait for the UI thread.
            try { result = Task.Run(() => work(token), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception e) { error = e; }
        }, () =>
        {
            try
            {
                if (error is not null) throw error;
                token.ThrowIfCancellationRequested();
                apply(result.Value);
                Finish(job, AIJobStatusKind.Done, cost: result.Cost);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                Finish(job, AIJobStatusKind.Cancelled);
            }
            catch (Exception e)
            {
                // The job list is the window's: in the person's language. A paid job's work is the request itself (what
                // can be refused is refused before the job starts), so a failure here came after it was sent, unless the
                // computer never got a connection.
                Finish(job, AIJobStatusKind.Failed, failure: PersonMessage.Text(e), mayHaveBeenCharged: paid && !NeverSent(e),
                       forAgent: AgentAPI.Words(e));
                session.Report(e);
            }
        });
        return job;
    }

    /// <summary>The same for work that is done in one go.</summary>
    internal Job RunJob<T>(UiText title, Func<CancellationToken, (T Value, double? Cost)> work, Action<T> apply, bool paid = false) =>
        RunJob(title, token => Task.FromResult(work(token)), apply, paid);

    /// <summary>No connection was made: the request can't have reached the service (URLError's notConnectedToInternet,
    /// cannotFindHost, cannotConnectToHost, dnsLookupFailed).</summary>
    private static bool NeverSent(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }) return true;
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.ConnectionRefused })
                return true;
        }
        return false;
    }

    private void Finish(Job job, AIJobStatusKind status, double? cost = null, UiText? failure = null, bool mayHaveBeenCharged = false,
                        string? forAgent = null)
    {
        if (status != AIJobStatusKind.Done) IsSegmenting = false;
        job.Status = status;
        job.Failure = failure;
        job.MayHaveBeenCharged = mayHaveBeenCharged;
        job.FailureForAgent = forAgent;
        job.Cost = cost;
        RaiseJobs();
        job.Ended?.Invoke(status);
        job.Finished.TrySetResult();
    }

    public void Cancel(Guid jobId) => jobs.FirstOrDefault(j => j.Id == jobId)?.Cancellation.Cancel();

    public void ClearFinished()
    {
        jobs.RemoveAll(j => j.Status != AIJobStatusKind.Running);
        RaiseJobs();
    }

    /// <summary>Runs <paramref name="work"/> away from the UI thread; the task completes on the UI thread (Swift: try await
    /// Task.detached { … }.value).</summary>
    internal Task<T> Detached<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        T result = default!;
        Exception? error = null;
        Scheduler.RunInBackground(() =>
        {
            try { result = Task.Run(work).GetAwaiter().GetResult(); }
            catch (Exception e) { error = e; }
        }, () =>
        {
            if (error is OperationCanceledException) done.SetCanceled();
            else if (error is not null) done.SetException(error);
            else done.SetResult(result);
        });
        return done.Task;
    }

    internal Task<T> Detached<T>(Func<T> work) => Detached(() => Task.FromResult(work()));

    // MARK: Inputs

    /// <summary>The document flattened to straight sRGB (ImageExporter.flattenedSRGB as an RGBAImage).</summary>
    internal static RGBAImage Composite(DocumentState state)
    {
        var (buffer, _, rect) = new DocumentCompositor().RenderPixels(state);
        var floats = new float[buffer.Length];
        for (int i = 0; i < floats.Length; i++) floats[i] = (float)buffer[i];
        return RGBAImage.FromLinearPremultiplied(floats, rect.Width, rect.Height);
    }

    /// <summary>The document flattened in its own format: premultiplied linear (ImageExporter.flatten as the pixels the
    /// document keeps).</summary>
    internal static LinearImage LinearComposite(DocumentState state)
    {
        var (buffer, _, rect) = new DocumentCompositor().RenderPixels(state);
        var floats = new float[buffer.Length];
        for (int i = 0; i < floats.Length; i++) floats[i] = (float)buffer[i];
        return new LinearImage(rect.Width, rect.Height, floats);
    }

    /// <summary>One layer on its own over the document's area (ImageExporter.layerImageSRGB as an RGBAImage).</summary>
    internal static RGBAImage LayerImage(Layer layer, DocumentState state) =>
        Composite(state with { Layers = [layer with { IsVisible = true }], ActiveLayerID = layer.Id, Selection = null });

    /// <summary>A picture file as straight sRGB (RGBAImage(ImageImporter.image(contentsOf:))).</summary>
    internal static RGBAImage ImageFile(string path)
    {
        var decoded = ImageImporter.Image(path);
        return Composite(new DocumentState(decoded.Width, decoded.Height, layers: [new Layer("", new RasterContent(ImageImporter.Tiles(decoded)))]));
    }

    /// <summary>A picture as tiles with its top-left corner at <paramref name="origin"/> (ImageImporter.tiles(from:origin:)).</summary>
    internal static TileGrid Tiles(RGBAImage image, (int X, int Y) origin = default) =>
        TileGrid.Empty.WriteRegion(new PixelRect(origin.X, origin.Y, image.Width, image.Height), image.ToLinearPremultiplied());

    private void AddLayer(RGBAImage image, string name, (int X, int Y) origin, UiText action)
    {
        var tiles = Tiles(image, origin);
        // The job finishes outside any user event: an undo step of its own.
        session.Document.Edit(action, s => s.InsertLayerAboveActive(new Layer(name, new RasterContent(tiles))), separateStep: true);
    }

    /// <summary>A name Glimmer gives a new layer, in the person's language (String(localized:)).</summary>
    private string LayerName(string english) => session.Environment.LayerName(english);

    private static string Prefix(string text, int count) => text.Length > count ? text[..count] : text;

    /// <summary>Opens a new document window (DocumentOpener.shared.createDocument).</summary>
    private void CreateDocument(DocumentState state)
    {
        if (session.Environment.Opener is { } opener) opener.CreateDocument(state);
        else throw new InvalidOperationException("Glimmer couldn't open a new window for the result.");
    }

    // MARK: Capabilities

    public void Generate(string prompt, string negative, int width, int height)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0) return;
        try
        {
            var model = ImageModel();
            var docSize = (Width: session.State.Width, Height: session.State.Height);
            // A fresh seed each time: the result cache keys on it, so the same prompt gives a new picture.
            int seed = Random.Shared.Next(1, int.MaxValue);
            RunJob(new UiText("Generate “{0}”", Prefix(prompt, 40)), async token =>
            {
                var r = await AIPipelines.GenerateAsync(prompt, negative, width, height, model, seed, token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image =>
            {
                // Fit inside the document, centred.
                double k = Math.Min(1, Math.Min(docSize.Width / (double)image.Width, docSize.Height / (double)image.Height));
                var fitted = k < 1 ? image.Resized((int)(image.Width * k), (int)(image.Height * k)) : image;
                AddLayer(fitted, LayerName("Generated"), ((docSize.Width - fitted.Width) / 2, (docSize.Height - fitted.Height) / 2), "Generate Image");
            }, paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    public void EditImage(string instruction)
    {
        instruction = instruction.Trim();
        if (instruction.Length == 0) return;
        try
        {
            var model = ImageModel();
            var source = Composite(session.State);
            RunJob(new UiText("Edit “{0}”", Prefix(instruction, 40)), async token =>
            {
                var r = await AIPipelines.EditAsync(source, instruction, model, cancellationToken: token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image => AddLayer(image, LayerName("Edited"), (0, 0), "Edit Image"), paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>Generative fill / remove over the current selection.</summary>
    public void Fill(AIPipelines.InpaintMode mode, string prompt, string? referencePath)
    {
        var state = session.State;
        if (state.Selection is not { } selection || selection.Bounds(state.Bounds) is not { } bounds)
        {
            session.ErrorMessage = "Make a selection first (M), then try again.";
            return;
        }
        try
        {
            var model = ImageModel();
            var source = Composite(state);
            var values = selection.Values(state.Bounds);
            for (int i = 0; i < values.Length; i++) values[i] *= 255;
            var mask = new GrayImage(state.Width, state.Height, values);
            var reference = referencePath is null ? null : ImageFile(referencePath);
            UiText title = mode == AIPipelines.InpaintMode.Remove ? "Remove object"
                : prompt.Length == 0 ? "Generative fill" : new UiText("Fill “{0}”", Prefix(prompt, 40));
            RunJob(title, async token =>
            {
                var r = await AIPipelines.InpaintAsync(source, mask, bounds, mode, prompt, model, reference, cancellationToken: token).ConfigureAwait(false);
                return ((r.Image, r.Origin), r.Cost);
            }, result => AddLayer(result.Image, mode == AIPipelines.InpaintMode.Remove ? "Removed" : "Generative Fill", result.Origin, title),
            paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    public void Expand(AIPipelines.Margins margins, string prompt)
    {
        if (margins.IsEmpty) return;
        try
        {
            var model = ImageModel();
            var source = Composite(session.State);
            RunJob("Expand", async token =>
            {
                var r = await AIPipelines.ExpandAsync(source, margins, prompt, model, cancellationToken: token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image => ApplyExpansion(image, margins), paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>Grows the canvas, shifts every layer and adds the expanded image on top.</summary>
    private void ApplyExpansion(RGBAImage image, AIPipelines.Margins m)
    {
        var offset = (m.Left, m.Top);
        var shifted = session.State.Layers.Select(l => TileOperations.Translate(l, offset)).ToList();
        var tiles = Tiles(image);
        session.Document.Edit("Expand", state =>
        {
            var layer = new Layer(LayerName("Expanded"), new RasterContent(tiles));
            return state with
            {
                Width = image.Width,
                Height = image.Height,
                Layers = [.. shifted, layer],
                Selection = null,
                ActiveLayerID = layer.Id,
            };
        });
    }

    /// <summary>Lab colour transfer of the active layer toward a reference image (new layer).</summary>
    public void MatchColor(string referencePath, double strength)
    {
        if (session.ActiveLayer is not { } layer || !(layer.IsRaster || layer.Content is SmartContent))
        {
            session.ErrorMessage = "Select a pixel layer to match.";
            return;
        }
        try
        {
            var reference = ImageFile(referencePath);
            var source = LayerImage(layer, session.State);
            var name = layer.Name;
            RunJob("Match color", _ => (Imaging.LabColorTransfer(source, reference.Limited(1024), strength), (double?)null),
                   image => AddLayer(image, $"{name} (matched)", (0, 0), "Match Color"));
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>On this computer: masks the active layer to its subject (non-destructive layer mask).</summary>
    public void RemoveBackground()
    {
        if (session.ActiveLayer is not { } layer || layer.IsAdjustment || layer.IsGroup)
        {
            session.ErrorMessage = "Select a pixel layer first.";
            return;
        }
        // The Mac does this with Vision; here it needs a model, asked about before anything runs.
        if (!HasModels("remove_background", RemoveBackground)) return;
        try
        {
            var image = LayerImage(layer, session.State);
            var id = layer.Id;
            var bounds = session.State.Bounds;
            RunJob("Remove background", async token =>
            {
                var mask = await SubjectSegmentation.SubjectMaskAsync(image, ct: token).ConfigureAwait(false);
                return (Selection.From(Unit(mask), bounds), (double?)null);
            }, selection => session.Document.Edit("Remove Background", s => s.UpdateLayer(id, l => l with { Mask = selection.AsMask })));
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>On this computer: selects the main subject of the whole image.</summary>
    public void SelectSubject()
    {
        if (!HasModels("select_subject", SelectSubject)) return;
        try
        {
            var image = Composite(session.State);
            var bounds = session.State.Bounds;
            RunJob("Select subject", async token =>
            {
                var mask = await SubjectSegmentation.SubjectMaskAsync(image, ct: token).ConfigureAwait(false);
                return (Selection.From(Unit(mask), bounds), (double?)null);
            }, selection => session.Document.Edit("Select Subject", s => s with { Selection = selection }));
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>A 0…255 mask as 0…1.</summary>
    internal static float[] Unit(GrayImage mask) => [.. mask.Values.Select(v => v / 255)];

    // MARK: Phase 4 capabilities

    public void Relight(AIPipelines.LightDirection direction, SRGBColor color, double intensity, string environment)
    {
        try
        {
            var model = ImageModel();
            var source = Composite(session.State);
            var title = direction.Title().ToLowerInvariant();
            RunJob(new UiText("Relight ({0})", title), async token =>
            {
                var r = await AIPipelines.RelightAsync(source, direction, color, intensity, model, environment, token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image => AddLayer(image, $"Relit ({title})", (0, 0), "Relight"), paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    public void RemoveReflections(double strength)
    {
        try
        {
            var model = ImageModel();
            var state = session.State;
            var source = Composite(state);
            var roi = state.Selection?.Bounds(state.Bounds);
            RunJob("Remove reflections", async token =>
            {
                var r = await AIPipelines.RemoveReflectionsAsync(source, roi, strength, model, token).ConfigureAwait(false);
                return ((r.Image, r.Origin), r.Cost);
            }, result => AddLayer(result.Image, LayerName("Reflections Removed"), result.Origin, "Remove Reflections"), paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>Re-grades the active layer to sit naturally in what's below it.</summary>
    public void Harmonize(double strength)
    {
        var state = session.State;
        if (state.ActiveLayer is not { } layer || state.PathOf(layer.Id) is not { } path || layer.IsAdjustment)
        {
            session.ErrorMessage = "Select the pasted-in layer to harmonize.";
            return;
        }
        try
        {
            var model = ImageModel();
            var fgFull = LayerImage(layer, state);
            // Everything under the layer's root ancestor.
            var below = state with { Layers = [.. state.Layers.Take(path[0])] };
            var bgFull = Composite(below);
            var bounds = (layer.Tiles.TileBounds ?? state.Bounds).Intersection(state.Bounds);
            if (bounds.IsEmpty) return;
            var roi = Imaging.ClampROI(bounds, state.Width, state.Height, padding: 0.1);
            var fg = fgFull.Cropped(roi);
            var bg = bgFull.Cropped(roi);
            var id = layer.Id;
            var name = layer.Name;
            RunJob(new UiText("Harmonize “{0}”", name), async token =>
            {
                var r = await AIPipelines.HarmonizeAsync(fg, bg, strength, model, token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image =>
            {
                var tiles = Tiles(image, (roi.X, roi.Y));
                session.Document.Edit("Harmonize", s => s.UpdateLayer(id, l => l with { IsVisible = false })
                    .InsertLayerAboveActive(new Layer(string.Format(LayerName("{0} (harmonized)"), name), new RasterContent(tiles))));
            }, paid: true);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    /// <summary>Opens an upscaled copy as a new document: Lanczos, or Real-ESRGAN on this computer, optionally followed by
    /// an AI detail pass.</summary>
    public void Upscale(int scale, bool neural, bool enhance, double creativity)
    {
        var state = session.State;
        try
        {
            UiText title = neural ? new UiText("Upscale {0}× (Real-ESRGAN)", scale) : new UiText("Upscale {0}×", scale);
            var layerName = string.Format(LayerName("Upscaled {0}×"), scale);
            if (!enhance && !neural)
            {
                RunJob(title, _ => (DocumentEffects.Upscale(state, scale), (double?)null), grid =>
                    CreateDocument(new DocumentState(state.Width * scale, state.Height * scale, layers: [new Layer(layerName, new RasterContent(grid))])));
                return;
            }
            var model = enhance ? ImageModel() : null;
            var source = Composite(state);
            RunJob(enhance ? new UiText("{0} with AI detail", title) : title, async token =>
            {
                var upscaled = neural
                    ? await RealESRGAN.UpscaleAsync(source, scale, ct: token).ConfigureAwait(false)
                    : source.Resized(source.Width * scale, source.Height * scale);
                if (model is null) return (upscaled, (double?)null);
                var r = await AIPipelines.EnhanceAsync(upscaled, creativity, model, cancellationToken: token).ConfigureAwait(false);
                return (r.Image, r.Cost);
            }, image => CreateDocument(new DocumentState(image.Width, image.Height, layers: [new Layer(layerName, new RasterContent(Tiles(image)))])),
            paid: enhance);
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    // MARK: Clean Up

    private IReadOnlyList<AIPipelines.Distraction> distractions = [];
    public IReadOnlyList<AIPipelines.Distraction> Distractions { get => distractions; private set { distractions = value; Raise(); } }
    private UiText? distractionMessage;
    public UiText? DistractionMessage { get => distractionMessage; private set => Set(ref distractionMessage, value); }

    public void FindDistractions() => GateTextRequest(needsPicture: true, SendDistractionRequest);

    private void SendDistractionRequest()
    {
        try
        {
            var model = TextModel();
            var source = Composite(session.State);
            Distractions = [];
            DistractionMessage = null;
            RunJob("Find distractions", async token =>
            {
                var r = await AIPipelines.FindDistractionsAsync(source, model, token).ConfigureAwait(false);
                return ((r.Items, r.Message), r.Cost);
            }, result =>
            {
                Distractions = result.Items;
                DistractionMessage = result.Message is { } m ? UiText.Verbatim(m)
                    : result.Items.Count == 0 ? (UiText?)"No obvious distractions found." : null;
            }, paid: setup.TextProvider.Slug != "ollama");
        }
        catch (Exception error)
        {
            session.Report(error);
        }
    }

    public void DismissDistraction(string id) => Distractions = [.. distractions.Where(d => d.Id != id)];

    /// <summary>Selects the given distractions (boxes) so they can be refined or removed.</summary>
    public void SelectDistractions(IReadOnlyList<AIPipelines.Distraction> items)
    {
        var state = session.State;
        var shapes = items.Select(d =>
        {
            var r = d.PixelRect(state.Width, state.Height);
            return Selection.Rectangle(new CGRectLike(r.X, r.Y, r.MaxX, r.MaxY));
        }).ToList();
        if (shapes.Count == 0) return;
        var combined = shapes[0];
        foreach (var s in shapes.Skip(1)) combined = combined.Combined(s, Selection.Operation.Add);
        session.Document.Edit("Select Distractions", s => s with { Selection = combined });
    }

    public void RemoveDistractions(IReadOnlyList<AIPipelines.Distraction> items)
    {
        SelectDistractions(items);
        Fill(AIPipelines.InpaintMode.Remove, "", null);
        var ids = items.Select(i => i.Id).ToHashSet();
        Distractions = [.. distractions.Where(d => !ids.Contains(d.Id))];
    }

    public void ApplyPreset(Preset preset)
    {
        // Like the web app, filter steps (grain, clarity, vignette …) bake into the active pixel layer; the adjustments go
        // in a group above it.
        if (preset.FilterSteps.Count > 0 && !session.ApplyFilterSteps(preset.FilterSteps))
        {
            AddMessage(fromUser: false, new UiText("“{0}” also uses filters ({1}). Select a pixel layer and apply it again to include them.",
                                                   preset.Name, string.Join(", ", preset.FilterSteps.Select(f => f.Name))));
        }
        session.Document.Edit(new UiText("Apply {0}", preset.Name), s => s.InsertLayerAboveActive(preset.MakeGroup()));
    }

    // MARK: Depth lens blur

    private (int Key, float[] Depth)? depthCache;

    internal static int DocumentKey(DocumentState state)
    {
        var h = new HashCode();
        h.Add(state.Width);
        h.Add(state.Height);
        foreach (var layer in state.AllLayers)
        {
            h.Add(layer.Id);
            h.Add(layer.IsVisible);
            h.Add(layer.Opacity);
            h.Add(layer.Tiles.ContentSignature);
        }
        return h.ToHashCode();
    }

    private static readonly IReadOnlyList<OnDeviceModel> DepthModels = [OnDeviceModel.Depth];

    /// <summary>Lens Blur in one go (the agent's lens_blur): depth once per image, then the blur as a new layer.</summary>
    internal Job? LensBlur(DocumentEffects.LensBlurParameters parameters)
    {
        var state = session.State;
        if (depthCache?.Key != DocumentKey(state) && ModelStore.Shared.Missing(DepthModels).Count > 0)
        {
            WithModels(DepthModels, () => LensBlur(parameters));
            return null;
        }
        try
        {
            var image = Composite(state);
            int key = DocumentKey(state);
            var cached = depthCache?.Key == key ? depthCache?.Depth : null;
            return RunJob("Lens blur", async token =>
            {
                var depth = cached ?? Unit(await DepthEstimator.DepthAsync(image, ct: token).ConfigureAwait(false));
                return ((DocumentEffects.LensBlur(state, depth, parameters), depth), (double?)null);
            }, result =>
            {
                depthCache = (key, result.Item2);
                session.Document.Edit("Lens Blur", s => s.InsertLayerAboveActive(new Layer(LayerName("Lens Blur"), new RasterContent(result.Item1))));
            });
        }
        catch (Exception error)
        {
            session.Report(error);
            return null;
        }
    }

    // MARK: Live lens blur

    /// <summary>A lens blur being tuned: the sliders re-render a preview layer until Apply or Cancel.</summary>
    private (Guid PreviewID, DocumentState Base, float[] Depth)? lensBlurLive;
    private bool isPreparingLensBlur;
    private IDisposable? lensBlurTask;
    private int lensBlurGeneration;

    public bool IsLensBlurLive => lensBlurLive is not null;
    public bool IsPreparingLensBlur { get => isPreparingLensBlur; private set => Set(ref isPreparingLensBlur, value); }

    /// <summary>Estimates depth (once per image), then shows the blur on the canvas.</summary>
    public void BeginLensBlur(DocumentEffects.LensBlurParameters parameters)
    {
        if (lensBlurLive is not null || isPreparingLensBlur) return;
        if (depthCache?.Key != DocumentKey(session.State) && ModelStore.Shared.Missing(DepthModels).Count > 0)
        {
            WithModels(DepthModels, () => BeginLensBlur(parameters));
            return;
        }
        session.CommitLiveEdit();
        var state = session.State;
        try
        {
            var image = Composite(state);
            int key = DocumentKey(state);
            var cached = depthCache?.Key == key ? depthCache?.Depth : null;
            IsPreparingLensBlur = true;
            var job = RunJob("Estimating depth", async token =>
            {
                if (cached is not null) return (cached, (double?)null);
                return (Unit(await DepthEstimator.DepthAsync(image, ct: token).ConfigureAwait(false)), (double?)null);
            }, depth =>
            {
                IsPreparingLensBlur = false;
                depthCache = (key, depth);
                // The image changed while the depth was being estimated: start over rather than blur stale pixels.
                if (DocumentKey(session.State) != key) return;
                var id = Guid.NewGuid();
                lensBlurLive = (id, session.State, depth);
                Raise(nameof(IsLensBlurLive));
                // A preview layer outside the undo history; Apply turns it into a real step.
                session.Document.State = session.State with
                {
                    Layers = session.State.Layers.Add(new Layer("Lens Blur", new RasterContent(TileGrid.Empty), id: id)),
                };
                UpdateLensBlur(parameters, showDepth: false);
            });
            // A failed or cancelled estimate leaves nothing being prepared (the original stayed "preparing" for good,
            // and Lens Blur couldn't be started again in that window).
            job.Ended = status => { if (status != AIJobStatusKind.Done) IsPreparingLensBlur = false; };
        }
        catch (Exception error)
        {
            IsPreparingLensBlur = false;
            session.Report(error);
        }
    }

    /// <summary>Re-renders the preview for new settings, or shows the depth map instead.</summary>
    public void UpdateLensBlur(DocumentEffects.LensBlurParameters parameters, bool showDepth)
    {
        if (lensBlurLive is not { } live) return;
        lensBlurTask?.Dispose();
        int generation = ++lensBlurGeneration;
        lensBlurTask = Scheduler.Schedule(TimeSpan.FromMilliseconds(60), () =>
        {
            Scheduler.Background(() =>
            {
                try
                {
                    return showDepth ? DepthTiles(live.Depth, live.Base.Width, live.Base.Height) : DocumentEffects.LensBlur(live.Base, live.Depth, parameters);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return null;
                }
            }, grid =>
            {
                if (grid is null || lensBlurLive?.PreviewID != live.PreviewID) return;
                session.Document.State = session.State.UpdateLayer(live.PreviewID, l => l with { Content = new RasterContent(grid) });
            }, isCancelled: () => generation != lensBlurGeneration);
        });
    }

    /// <summary>The depth map as a grey image: white is near, black is far.</summary>
    private static TileGrid? DepthTiles(float[] depth, int width, int height)
    {
        if (depth.Length != width * height) return null;
        var rgba = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            // The 8-bit sRGB grey of the original, as the linear value the tiles keep.
            float v = ColorMath.SrgbToLinear(MathF.Floor(Math.Clamp(depth[i], 0, 1) * 255) / 255);
            rgba[i * 4] = v;
            rgba[i * 4 + 1] = v;
            rgba[i * 4 + 2] = v;
            rgba[i * 4 + 3] = 1;
        }
        return TileGrid.Empty.WriteRegion(new PixelRect(0, 0, width, height), rgba);
    }

    /// <summary>Keeps the blur as a new layer (one undo step) or throws the preview away.</summary>
    public void EndLensBlur(bool apply, DocumentEffects.LensBlurParameters parameters)
    {
        if (lensBlurLive is not { } live) return;
        lensBlurTask?.Dispose();
        lensBlurGeneration++;
        lensBlurLive = null;
        Raise(nameof(IsLensBlurLive));
        session.Document.State = session.State.RemoveLayer(live.PreviewID).State;
        if (!apply) return;
        // Render with the final settings: the preview may have been a step behind, or showing depth.
        RunJob("Lens blur", _ => (DocumentEffects.LensBlur(live.Base, live.Depth, parameters), (double?)null), grid =>
            session.Document.Edit("Lens Blur", s => s.InsertLayerAboveActive(new Layer(LayerName("Lens Blur"), new RasterContent(grid)))));
    }

    // MARK: Magic Select

    private readonly MagicSelector selector = new();
    private IReadOnlyList<MagicSelector.Point> magicPoints = [];
    public IReadOnlyList<MagicSelector.Point> MagicPoints { get => magicPoints; private set { magicPoints = value; Raise(); } }
    private bool isSegmenting;
    public bool IsSegmenting { get => isSegmenting; set => Set(ref isSegmenting, value); }
    private Selection? selectionBeforeMagic;
    private static readonly IReadOnlyList<OnDeviceModel> MagicModels = [OnDeviceModel.SegmentAnything];

    public void MagicSelect(Vec2 point, bool positive)
    {
        var state = session.State;
        if (state.Bounds.Intersection(new PixelRect((int)point.X, (int)point.Y, 1, 1)).Width <= 0) return;
        if (ModelStore.Shared.Missing(MagicModels).Count > 0)
        {
            WithModels(MagicModels, () => MagicSelect(point, positive));
            return;
        }
        if (magicPoints.Count == 0) selectionBeforeMagic = state.Selection;
        MagicPoints = [.. magicPoints, new MagicSelector.Point(point, positive)];
        var points = magicPoints;
        try
        {
            var image = Composite(state);
            int key = DocumentKey(state);
            var bounds = state.Bounds;
            IsSegmenting = true;
            RunJob(points.Count == 1 ? "Magic Select" : "Magic Select (refine)", async token =>
            {
                await selector.PrepareAsync(image, key, token).ConfigureAwait(false);
                var mask = await selector.SegmentAsync(points, token).ConfigureAwait(false);
                return (Selection.From(Unit(mask), bounds), (double?)null);
            }, selection =>
            {
                IsSegmenting = false;
                session.Document.Edit("Magic Select", s => s with { Selection = selection });
            });
        }
        catch (Exception error)
        {
            IsSegmenting = false;
            session.Report(error);
        }
    }

    public void ResetMagicSelect()
    {
        MagicPoints = [];
        selectionBeforeMagic = null;
    }

    /// <summary>Esc: forget the points and put back the selection that was there before Magic Select.</summary>
    public void CancelMagicSelect()
    {
        var before = selectionBeforeMagic;
        ResetMagicSelect();
        session.Document.Edit("Cancel Magic Select", s => s with { Selection = before });
    }

    // MARK: Assistant (see AIController+Agent.cs)
}

/// <summary>The saved actions under the Mac's own key, "assistant.savedActions", as the JSON text of the list.</summary>
internal sealed class SettingsSavedActionStore(IAppSettings settings) : ISavedActionStore
{
    public const string Key = "assistant.savedActions";

    public byte[]? Load() => settings.Get<string?>(Key, null) is { Length: > 0 } text ? System.Text.Encoding.UTF8.GetBytes(text) : null;

    public void Store(byte[] data) => settings.Set(Key, System.Text.Encoding.UTF8.GetString(data));
}
