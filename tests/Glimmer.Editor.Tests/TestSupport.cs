// Windows only: the app's services as test doubles, a UI thread driven by hand, and the canvas's pointer dispatch
// (App/CanvasView.swift's mouseDown/mouseDragged/mouseUp) for driving tools as a person would.
using System.Numerics;
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor.Tests;

/// <summary>The UI thread with a clock the test moves: posts queue until <see cref="RunPosted"/>, timers fire when
/// <see cref="Advance"/> reaches them, and background work runs at once (its result is posted like any other).</summary>
public sealed class ManualScheduler : IUiScheduler
{
    private sealed class Timer(TimeSpan due, long order, Action action) : IDisposable
    {
        public TimeSpan Due { get; } = due;
        public long Order { get; } = order;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }

    private readonly Queue<Action> posted = new();
    private readonly List<Timer> timers = [];
    private long order;

    public TimeSpan Now { get; private set; }
    public int BackgroundRuns { get; private set; }

    private readonly SemaphoreSlim arrived = new(0);
    private SynchronizationContext? context;

    public void Post(Action action)
    {
        lock (posted) posted.Enqueue(action);
        arrived.Release();
    }

    /// <summary>The UI thread's SynchronizationContext: an await on the UI thread comes back through Post, as it does
    /// through WPF's dispatcher.</summary>
    public SynchronizationContext Context => context ??= new UiContext(this);

    private sealed class UiContext(ManualScheduler scheduler) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => scheduler.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>Makes the test thread the UI thread (its awaits come back through Post) until disposed.</summary>
    public IDisposable AsUIThread()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(Context);
        return new Restore(previous);
    }

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SynchronizationContext.SetSynchronizationContext(previous);
    }

    /// <summary>Runs what is posted until <paramref name="done"/> holds, waiting for work that finishes on other threads
    /// (a socket, the thread pool); false when it doesn't within <paramref name="timeout"/>.</summary>
    public bool RunUntil(Func<bool> done, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            RunPosted();
            if (done()) return true;
            var left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return false;
            arrived.Wait(left < TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>Waits for <paramref name="task"/> the way the UI thread does: running what is posted meanwhile.</summary>
    public T Await<T>(Task<T> task)
    {
        if (!RunUntil(() => task.IsCompleted)) throw new TimeoutException("The task didn't finish.");
        return task.GetAwaiter().GetResult();
    }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var timer = new Timer(Now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), order++, action);
        timers.Add(timer);
        return timer;
    }

    public void RunInBackground(Action work, Action? then = null)
    {
        BackgroundRuns++;
        try { work(); }
        finally { if (then is not null) Post(then); }
    }

    /// <summary>Runs what was posted, and what that posts, as the UI thread does between events.</summary>
    public void RunPosted()
    {
        // What runs here runs on the UI thread: its awaits come back here.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(Context);
        try
        {
            for (int guard = 0; guard < 100_000; guard++)
            {
                Action next;
                lock (posted)
                {
                    if (posted.Count == 0) return;
                    next = posted.Dequeue();
                }
                next();
            }
            throw new InvalidOperationException("posts keep posting");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Moves the clock forward, firing timers in order as they come due (and running posts between them).</summary>
    public void Advance(TimeSpan span)
    {
        var end = Now + span;
        RunPosted();
        for (int guard = 0; guard < 100_000; guard++)
        {
            var due = timers.Where(t => !t.Cancelled && t.Due <= end).OrderBy(t => t.Due).ThenBy(t => t.Order).FirstOrDefault();
            if (due is null) break;
            timers.Remove(due);
            if (due.Due > Now) Now = due.Due;
            due.Action();
            RunPosted();
        }
        timers.RemoveAll(t => t.Cancelled);
        Now = end;
        RunPosted();
    }

    /// <summary>Lets everything that is about to happen happen (thumbnails, the end of a slider drag): ten seconds.</summary>
    public void Settle() => Advance(TimeSpan.FromSeconds(10));

    public int PendingTimers => timers.Count(t => !t.Cancelled);
}

/// <summary>UserDefaults as a dictionary.</summary>
public sealed class FakeSettings : IAppSettings
{
    private readonly Dictionary<string, object?> values = [];

    public T Get<T>(string key, T fallback)
    {
        if (!values.TryGetValue(key, out var value) || value is null) return fallback;
        if (value is T t) return t;
        try { return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        values[key] = value;
        Changed?.Invoke(this, key);
    }

    public bool Contains(string key) => values.ContainsKey(key);
    public event EventHandler<string>? Changed;
}

/// <summary>The clipboard: what was put there, and a change count.</summary>
public sealed class FakeClipboard : IClipboardService
{
    public uint SequenceNumber { get; private set; }
    public byte[]? Png { get; set; }
    public List<string> Files { get; } = [];
    public RgbaImage? LastImage { get; private set; }
    public string? Text { get; private set; }

    public bool ContainsImage => Png is not null || LastImage is not null;
    public byte[]? GetImagePng() => Png;
    public IReadOnlyList<string> GetImageFiles() => Files;

    public void SetImage(RgbaImage image)
    {
        LastImage = image;
        SequenceNumber++;
    }

    public void SetText(string text)
    {
        Text = text;
        SequenceNumber++;
    }

    /// <summary>Another app put something on the clipboard.</summary>
    public void ChangedElsewhere(byte[]? png = null)
    {
        Png = png;
        LastImage = null;
        SequenceNumber++;
    }
}

/// <summary>Open and save dialogs with answers given in advance (cancelled when there is none).</summary>
public sealed class FakeDialogs : IFileDialogs
{
    public Queue<string?> SaveAnswers { get; } = new();
    public Queue<IReadOnlyList<string>> ImageAnswers { get; } = new();
    public List<(string Suggested, IReadOnlyList<string> Extensions, UiText? Message)> SaveQuestions { get; } = [];

    public Queue<IReadOnlyList<string>> DocumentAnswers { get; } = new();
    public Queue<string?> FolderAnswers { get; } = new();
    public List<string> FolderQuestions { get; } = [];

    public IReadOnlyList<string> ChooseImages(string title, string prompt, bool multiple) =>
        ImageAnswers.Count > 0 ? ImageAnswers.Dequeue() : [];

    public IReadOnlyList<string> ChooseDocuments(bool multiple = true) =>
        DocumentAnswers.Count > 0 ? DocumentAnswers.Dequeue() : [];

    public string? ChooseSaveLocation(string suggestedName, IReadOnlyList<string> extensions, UiText? message = null)
    {
        SaveQuestions.Add((suggestedName, extensions, message));
        return SaveAnswers.Count > 0 ? SaveAnswers.Dequeue() : null;
    }

    public string? ChooseFolder(string title, string? initial = null)
    {
        FolderQuestions.Add(title);
        return FolderAnswers.Count > 0 ? FolderAnswers.Dequeue() : null;
    }
}

/// <summary>The app's windows: records what the editor asked of them.</summary>
public sealed class FakeHost : IDocumentHost
{
    public List<string> Calls { get; } = [];
    public List<IEditorSession> Shown { get; } = [];
    public List<IEditorSession> Closed { get; } = [];
    public List<UiText> Errors { get; } = [];
    public List<string> Recent { get; } = [];
    public SaveChangesChoice SaveAnswer { get; set; } = SaveChangesChoice.DontSave;
    public LinkedCloseChoice LinkedAnswer { get; set; } = LinkedCloseChoice.KeepForLater;
    public int SaveQuestions { get; private set; }

    public void ShowNewDocumentWindow() => Calls.Add(nameof(ShowNewDocumentWindow));
    public void ShowWelcomeWindow() => Calls.Add(nameof(ShowWelcomeWindow));
    public void ShowAgentSplash() => Calls.Add(nameof(ShowAgentSplash));
    public void ShowLinkedWaiting() => Calls.Add(nameof(ShowLinkedWaiting));
    public void OpenSettings(string pane) => Calls.Add($"{nameof(OpenSettings)}:{pane}");
    public void ShowDocument(IEditorSession session) => Shown.Add(session);
    public void BringForward(IEditorSession session) => Calls.Add(nameof(BringForward));
    public void CloseDocument(IEditorSession session) => Closed.Add(session);

    public SaveChangesChoice AskToSaveChanges(IEditorSession session)
    {
        SaveQuestions++;
        return SaveAnswer;
    }

    public LinkedCloseChoice AskToCloseLinked(IEditorSession session, UiText message, UiText detail, bool canSaveBack) => LinkedAnswer;
    public void PresentError(UiText message) => Errors.Add(message);
    public void NoteRecentDocument(string path) => Recent.Add(path);

    public IDisposable ShowProgress(UiText title, UiText message)
    {
        Calls.Add(nameof(ShowProgress));
        return new Gone();
    }

    private sealed class Gone : IDisposable
    {
        public void Dispose() { }
    }
}

public sealed class FakeSound : IAppSound
{
    public int Beeps { get; private set; }
    public void Beep() => Beeps++;
}

/// <summary>One app run: the services, its folders (removed afterwards), and documents to edit.</summary>
public sealed class Harness : IDisposable
{
    public Harness(Action<FakeSettings>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "glimmer-editor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        configure?.Invoke(Settings);
        Environment = MakeEnvironment();
        Directory.CreateDirectory(Environment.TemporaryFolder);
    }

    public string Root { get; }
    public ManualScheduler Scheduler { get; } = new();
    public FakeSettings Settings { get; } = new();
    public FakeHost Host { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeSound Sound { get; } = new();
    public List<string> Log { get; } = [];
    public List<string> Recycled { get; } = [];
    public EditorEnvironment Environment { get; private set; }

    /// <summary>The same folders and settings with a new clock: what the next start of the app sees.</summary>
    public EditorEnvironment MakeEnvironment(ManualScheduler? scheduler = null) =>
        new(Settings, scheduler ?? Scheduler, Host, Dialogs, Clipboard, Sound, Path.Combine(Root, "temp"), Path.Combine(Root, "data"))
        {
            Log = Log.Add,
            MoveToRecycleBin = path =>
            {
                Recycled.Add(path);
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
                return true;
            },
        };

    public string PathIn(string name) => Path.Combine(Root, name);

    /// <summary>A document and its session, as a new window gets them (800 × 600 white unless said otherwise).</summary>
    public EditorSession NewSession(DocumentState? state = null)
    {
        var session = new EditorSession(new GlimmerDocument(Environment, state ?? Docs.White()), Environment);
        Scheduler.RunPosted();
        return session;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Documents to start from.</summary>
public static class Docs
{
    public static readonly Vector4 WhitePixel = new(1, 1, 1, 1);
    public static readonly Vector4 BlackPixel = new(0, 0, 0, 1);

    /// <summary>The UI tests' document (-uiTestingNewDocument): 800 × 600, white background.</summary>
    public static DocumentState White(int width = 800, int height = 600) => DocumentState.New(width, height, DocumentState.Background.White);

    /// <summary>A white background with a black square at (200, 150)-(400, 350) on a layer of its own above it.</summary>
    public static DocumentState WithSquare(int width = 800, int height = 600)
    {
        var square = new PixelRect(200, 150, 200, 200);
        var pixels = new float[square.Width * square.Height * 4];
        for (int i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 1;
        var layer = new Layer("Square", new RasterContent(TileGrid.Empty.WriteRegion(square, pixels)));
        return DocumentState.New(width, height, DocumentState.Background.White).InsertLayerAboveActive(layer);
    }

    /// <summary>One raster layer, left half black and right half white (for tools that move what is there).</summary>
    public static DocumentState TwoTone(int width = 400, int height = 300)
    {
        var pixels = new float[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                float v = x < width / 2 ? 0 : 1;
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 1;
            }
        var layer = new Layer("Background", new RasterContent(TileGrid.Empty.WriteRegion(new PixelRect(0, 0, width, height), pixels)));
        return new DocumentState(width, height, layers: [layer]);
    }

    /// <summary>One raster layer of a single grey (linear value).</summary>
    public static DocumentState Gray(float value, int width = 400, int height = 300)
    {
        var bounds = new PixelRect(0, 0, width, height);
        var layer = new Layer("Background", new RasterContent(TileGrid.Solid(new Vector4(value, value, value, 1), bounds)));
        return new DocumentState(width, height, layers: [layer]);
    }

    /// <summary>The pixel (premultiplied linear RGBA) at (x, y) of a layer.</summary>
    public static Vector4 Pixel(Layer layer, int x, int y) => Pixel(layer.Tiles, x, y);

    public static Vector4 Pixel(TileGrid tiles, int x, int y)
    {
        var v = tiles.ReadRegion(new PixelRect(x, y, 1, 1));
        return new Vector4(v[0], v[1], v[2], v[3]);
    }

    public static Layer Named(this DocumentState state, string name) => state.AllLayers.First(l => l.Name == name);
}

/// <summary>Pointer input as the canvas sends it to the session for each tool (CanvasNSView), in document points.</summary>
public sealed class CanvasInput(EditorSession session, ManualScheduler scheduler)
{
    private enum DragKind { None, Move, Marquee, Stroke, Lasso, Gradient, Eyedropper, Transform, Crop, Pen, Shape, Liquify, Pan }

    private DragKind drag;
    private Vec2 last;

    public void Down(Vec2 p, InputModifiers modifiers = InputModifiers.None, int clickCount = 1)
    {
        last = p;
        drag = DragKind.None;
        if (session.Liquify is not null)
        {
            session.LiquifyDrag(p, start: true, pressure: 1);
            drag = DragKind.Liquify;
            return;
        }
        switch (session.EffectiveTool)
        {
            case ToolID.Move:
                if (clickCount == 2 && session.TextLayerAt(p) is not null)
                {
                    session.Tool = ToolID.Type;
                    session.TypeClick(p);
                    break;
                }
                session.BeginMove(p);
                drag = DragKind.Move;
                break;
            case ToolID.RectangleMarquee or ToolID.EllipseMarquee:
                session.BeginMarquee(p, modifiers);
                drag = DragKind.Marquee;
                break;
            case ToolID.MagicSelect:
                session.AI.MagicSelect(p, !modifiers.HasFlag(InputModifiers.Option));
                break;
            case ToolID.CloneStamp or ToolID.HealingBrush when modifiers.HasFlag(InputModifiers.Option):
                session.SetCloneSource(p);
                break;
            case ToolID.Brush or ToolID.Eraser or ToolID.CloneStamp or ToolID.HealingBrush or ToolID.Dodge or ToolID.Burn
                or ToolID.Blur or ToolID.Sharpen or ToolID.Smudge or ToolID.PatternStamp:
                session.BeginStroke(new PointerSample(p), erase: session.EffectiveTool == ToolID.Eraser,
                                    straightLine: modifiers.HasFlag(InputModifiers.Shift));
                drag = DragKind.Stroke;
                break;
            case ToolID.Lasso:
                session.BeginLasso(p, modifiers);
                drag = DragKind.Lasso;
                break;
            case ToolID.PolygonLasso:
                session.PolygonClick(p, modifiers, doubleClick: clickCount == 2);
                break;
            case ToolID.MagicWand:
                session.MagicWand(p, modifiers);
                break;
            case ToolID.PaintBucket:
                session.PaintBucket(p);
                break;
            case ToolID.Gradient:
                session.GradientDrag = new DragSegment(p, p);
                drag = DragKind.Gradient;
                break;
            case ToolID.Eyedropper:
                session.SampleColor(p, intoBackground: modifiers.HasFlag(InputModifiers.Option));
                drag = DragKind.Eyedropper;
                break;
            case ToolID.FreeTransform:
                session.TransformMouseDown(p, viewTolerance: 8, command: modifiers.HasFlag(InputModifiers.Command));
                drag = DragKind.Transform;
                break;
            case ToolID.Crop:
                session.CropMouseDown(session.Snapped(p), viewTolerance: 8);
                drag = DragKind.Crop;
                break;
            case ToolID.Type:
                session.TypeClick(p);
                break;
            case ToolID.Pen:
                session.PenMouseDown(session.Snapped(p), viewTolerance: 7);
                drag = DragKind.Pen;
                break;
            case ToolID.Shape:
                var d = session.Snapped(p);
                session.ShapeDrag = new DragSegment(d, d);
                drag = DragKind.Shape;
                break;
            default:
                drag = DragKind.Pan;
                break;
        }
    }

    public void Dragged(Vec2 p, InputModifiers modifiers = InputModifiers.None)
    {
        switch (drag)
        {
            case DragKind.Pan:
                session.Canvas.Pan(p - last);
                break;
            case DragKind.Move:
                session.UpdateMove(p);
                break;
            case DragKind.Transform:
                session.TransformDrag(p, shift: modifiers.HasFlag(InputModifiers.Shift));
                break;
            case DragKind.Crop:
                session.CropDrag(session.Snapped(p), fromCentre: modifiers.HasFlag(InputModifiers.Option), constrain: modifiers.HasFlag(InputModifiers.Shift));
                break;
            case DragKind.Pen:
                session.PenDragged(p, breakSymmetry: modifiers.HasFlag(InputModifiers.Option));
                break;
            case DragKind.Shape:
                if (session.ShapeDrag is { } s) session.ShapeDrag = new DragSegment(s.Start, session.Snapped(p));
                break;
            case DragKind.Liquify:
                session.LiquifyDrag(p, start: false, pressure: 1);
                break;
            case DragKind.Stroke:
                session.ContinueStroke(new PointerSample(p));
                break;
            case DragKind.Lasso:
                session.ExtendLasso(p);
                break;
            case DragKind.Gradient:
                if (session.GradientDrag is { } g) session.GradientDrag = new DragSegment(g.Start, p);
                break;
            case DragKind.Eyedropper:
                session.SampleColor(p, intoBackground: modifiers.HasFlag(InputModifiers.Option));
                break;
            case DragKind.Marquee:
                session.UpdateMarquee(session.Snapped(p));
                break;
        }
        last = p;
    }

    public void Up(Vec2 p)
    {
        switch (drag)
        {
            case DragKind.Move: session.EndMove(); break;
            case DragKind.Marquee: session.EndMarquee(); break;
            case DragKind.Stroke: session.EndStroke(p); break;
            case DragKind.Lasso: session.FinishLasso(); break;
            case DragKind.Gradient: session.FinishGradient(); break;
            case DragKind.Transform: session.TransformMouseUp(); break;
            case DragKind.Liquify: session.LiquifyMouseUp(); break;
            case DragKind.Crop: session.CropMouseUp(); break;
            case DragKind.Pen: session.PenMouseUp(); break;
            case DragKind.Shape: session.FinishShape(); break;
        }
        drag = DragKind.None;
    }

    /// <summary>A press, a drag through <paramref name="steps"/> points and a release; each is an event, after which the
    /// UI thread runs what was posted (that is when an event's undo group closes).</summary>
    public void Drag(Vec2 from, Vec2 to, InputModifiers modifiers = InputModifiers.None, int steps = 8)
    {
        Down(from, modifiers);
        scheduler.RunPosted();
        for (int i = 1; i <= steps; i++)
        {
            Dragged(from + (to - from) * (i / (double)steps), modifiers);
            scheduler.RunPosted();
        }
        Up(to);
        scheduler.RunPosted();
    }

    public void Click(Vec2 p, InputModifiers modifiers = InputModifiers.None, int clickCount = 1)
    {
        Down(p, modifiers, clickCount);
        scheduler.RunPosted();
        Up(p);
        scheduler.RunPosted();
    }
}
