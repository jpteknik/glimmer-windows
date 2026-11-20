// Ported from App/EditorSession.swift
using System.Collections.Immutable;
using System.ComponentModel;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;
using Glimmer.Tools;
using SkiaSharp;

namespace Glimmer.Editor;

/// <summary>Per-window editing state: the active tool, transient previews, panels and every user command. Owns the
/// document's undo registration.</summary>
public sealed partial class EditorSession : ObservableObject, IEditorSession
{
    public GlimmerDocument Document { get; }
    IEditorDocument IEditorSession.Document => Document;
    public CanvasController Canvas { get; }
    ICanvasController IEditorSession.Canvas => Canvas;
    public AIController AI { get; }
    IAIController IEditorSession.AI => AI;
    public EditorEnvironment Environment { get; }

    private IAppSettings Settings => Environment.Settings;
    private IUiScheduler Scheduler => Environment.Scheduler;

    /// <summary>How the local agent API addresses this window's document.</summary>
    public string AgentID { get; } = Guid.NewGuid().ToString().ToUpperInvariant();

    public EditorSession(GlimmerDocument document, EditorEnvironment? environment = null)
    {
        Document = document;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Canvas = new CanvasController(this);
        Canvas.DocumentSize = document.State.Size;
        cropDeletesPixels = Settings.Get(Preferences.CropDeletesKey, true);
        classicLayout = Settings.Get(Preferences.ClassicLayoutKey, false);
        startCardDismissed = Settings.Get(Preferences.SkipStartCardKey, false);
        AI = new AIController(this);
        AI.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AIController.Distractions) or nameof(AIController.MagicPoints)) Canvas.RequestRender();
        };
        Document.StateChanged += (_, _) => DocumentDidChange();
        Document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlimmerDocument.History)) Raise(nameof(IsUntouchedBlank));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name && RenderInputs.Contains(name)) Canvas.RequestRender();
        };
        RefreshThumbnails();
        // A document read from disk (a restored window) may carry its link.
        Document.LinkRead += () => RestoreLink();
        // After every step: a linked document writes its journal entry.
        Document.Step += AfterStep;
        Document.Saved += path => DocumentWasSaved(path);
        StartRecovery();
    }

    /// <summary>What the canvas draws: a change to any of these asks for a new frame (CanvasNSView.observeSession).</summary>
    private static readonly HashSet<string> RenderInputs =
    [
        nameof(State), nameof(Overrides), nameof(EffectiveTool), nameof(Marquee), nameof(Lasso), nameof(GradientDrag),
        nameof(HoverPoint), nameof(Brush), nameof(TransformSession), nameof(CropSession), nameof(ActivePathID), nameof(ShapeDrag),
        nameof(GuideDrag), nameof(EditingText), nameof(Tool), nameof(CloneSource), nameof(Liquify), nameof(LiquifySize),
        nameof(ChannelMask), nameof(IsPaletteOpen), nameof(SelectionOutline),
    ];

    public DocumentState State => Document.State;
    public Layer? ActiveLayer => State.ActiveLayer;

    // MARK: Tools

    private ToolID tool = ToolID.Move;
    public ToolID Tool
    {
        get => tool;
        set
        {
            if (tool == value) return;
            var oldValue = tool;
            tool = value;
            Raise(nameof(Tool), nameof(EffectiveTool));
            if (oldValue == ToolID.MagicSelect) AI.ResetMagicSelect();
            if (oldValue == ToolID.FreeTransform) CommitTransform();
            if (oldValue == ToolID.Crop) CropSessionValue = null;
            if (oldValue == ToolID.Pen) FinishPathSegment();
            if (tool == ToolID.FreeTransform) BeginTransform();
            if (tool == ToolID.Crop) BeginCrop();
        }
    }

    private bool temporaryHand;
    /// <summary>Space held: the Hand tool takes over temporarily.</summary>
    public bool TemporaryHand
    {
        get => temporaryHand;
        set { if (Set(ref temporaryHand, value)) Raise(nameof(EffectiveTool)); }
    }

    public ToolID EffectiveTool => temporaryHand ? ToolID.Hand : tool;

    private LayerOverrides overrides = LayerOverrides.None;
    /// <summary>Non-undoable preview of an in-progress tool action.</summary>
    public LayerOverrides Overrides
    {
        get => overrides;
        set { overrides = value; Raise(); }
    }

    private InspectorTab inspectorTab = InspectorTab.Layers;
    public InspectorTab InspectorTab { get => inspectorTab; set => Set(ref inspectorTab, value); }

    private AISection aiSection = AISection.Assistant;
    /// <summary>The AI panel section to reveal (set by menu commands).</summary>
    public AISection AISection { get => aiSection; set => Set(ref aiSection, value); }

    private Marquee? marquee;
    /// <summary>The marquee being dragged (document coordinates).</summary>
    public Marquee? Marquee { get => marquee; set => Set(ref marquee, value); }

    // MARK: Painting and selection-tool state

    private BrushSettings brush = new();
    public BrushSettings Brush { get => brush; set => Set(ref brush, value); }
    private SRGBColor foreground = new(0, 0, 0);
    public SRGBColor Foreground { get => foreground; set => Set(ref foreground, value); }
    private SRGBColor background = new(1, 1, 1);
    public SRGBColor Background { get => background; set => Set(ref background, value); }
    private double foregroundOpacity = 1.0, backgroundOpacity = 1.0;
    /// <summary>Alpha of the foreground and background colours (the colour wells' opacity).</summary>
    public double ForegroundOpacity { get => foregroundOpacity; set => Set(ref foregroundOpacity, value); }
    public double BackgroundOpacity { get => backgroundOpacity; set => Set(ref backgroundOpacity, value); }
    private bool editingMask;
    /// <summary>Paint into the active layer's mask instead of its pixels.</summary>
    public bool EditingMask { get => editingMask; set { if (Set(ref editingMask, value)) Raise(nameof(PaintsMask)); } }
    private double wandTolerance = 32.0;
    public double WandTolerance { get => wandTolerance; set => Set(ref wandTolerance, value); }
    private bool wandContiguous = true;
    public bool WandContiguous { get => wandContiguous; set => Set(ref wandContiguous, value); }
    private bool sampleAllLayers = true;
    public bool SampleAllLayers { get => sampleAllLayers; set => Set(ref sampleAllLayers, value); }
    private GradientStyle gradientStyle = GradientStyle.Linear;
    public GradientStyle GradientStyle { get => gradientStyle; set => Set(ref gradientStyle, value); }
    private bool gradientToTransparent, gradientReverse;
    public bool GradientToTransparent { get => gradientToTransparent; set => Set(ref gradientToTransparent, value); }
    public bool GradientReverse { get => gradientReverse; set => Set(ref gradientReverse, value); }
    private ImmutableArray<GradientStop>? gradientStops;
    /// <summary>Custom stops for the Gradient tool; null = foreground to background.</summary>
    public ImmutableArray<GradientStop>? GradientStops { get => gradientStops; set { gradientStops = value; Raise(); } }
    private double selectionFeather;
    /// <summary>Feather applied to selections as the marquee, lasso and wand make them (pixels).</summary>
    public double SelectionFeather { get => selectionFeather; set => Set(ref selectionFeather, value); }
    private bool interfaceHidden;
    /// <summary>Tab hides every panel for a clean view of the image.</summary>
    public bool InterfaceHidden { get => interfaceHidden; set => Set(ref interfaceHidden, value); }
    private List<Vec2>? lasso;
    /// <summary>Lasso being drawn (document points).</summary>
    public IReadOnlyList<Vec2>? Lasso => lasso;
    private Selection.Operation lassoOperation = Selection.Operation.Replace;
    private DragSegment? gradientDrag;
    /// <summary>Gradient drag in progress (document points).</summary>
    public DragSegment? GradientDrag { get => gradientDrag; set => Set(ref gradientDrag, value); }
    private Vec2? hoverPoint;
    /// <summary>Pointer position over the canvas, for the brush cursor (view points).</summary>
    public Vec2? HoverPoint { get => hoverPoint; set => Set(ref hoverPoint, value); }
    private CommandID? selectionDialog;
    public CommandID? SelectionDialog { get => selectionDialog; set => Set(ref selectionDialog, value); }

    // Phase 6 tools
    private Guid? editingText;
    public Guid? EditingText
    {
        get => editingText;
        set
        {
            var was = editingText;
            if (!Set(ref editingText, value)) return;
            // The open editor shows the text, so the layer's own pixels are left out meanwhile (CanvasNSView set these
            // overrides when it opened its editor, and cleared them when it closed it).
            if (value is { } id) Overrides = new LayerOverrides(pixels: [new(id, TileGrid.Empty)]);
            else if (was is { } old && overrides.Pixels.TryGetValue(old, out var hidden) && hidden.IsEmpty) Overrides = LayerOverrides.None;
        }
    }
    private TextStyle? textSelectionStyle;
    /// <summary>While text is being edited: the style at the caret or the start of the selection.</summary>
    public TextStyle? TextSelectionStyle { get => textSelectionStyle; set => Set(ref textSelectionStyle, value); }
    private bool textSelectionIsRange;
    /// <summary>Whether letters are selected (then the options bar styles only them).</summary>
    public bool TextSelectionIsRange { get => textSelectionIsRange; set => Set(ref textSelectionIsRange, value); }
    /// <summary>The open text editor, which styles the selected words.</summary>
    public ITextStyler? TextStyler { get; set; }
    private TextDefaults textDefaults = new("HelveticaNeue-Bold", 96, TextAlignment.Left);
    public TextDefaults TextDefaults { get => textDefaults; set => Set(ref textDefaults, value); }
    private Guid? activePathID;
    public Guid? ActivePathID { get => activePathID; set { if (Set(ref activePathID, value)) Raise(nameof(ActivePath)); } }
    private (int Subpath, int Anchor, int Part)? penDrag;
    private bool penOpen;
    private ShapeKind shapeKind = ShapeKind.Rectangle;
    public ShapeKind ShapeKind { get => shapeKind; set => Set(ref shapeKind, value); }
    private double shapeLineWidth = 8.0;
    public double ShapeLineWidth { get => shapeLineWidth; set => Set(ref shapeLineWidth, value); }

    private bool cropDeletesPixels;
    /// <summary>Crop throws away pixels outside the new canvas (as in the web app and Photoshop).</summary>
    public bool CropDeletesPixels
    {
        get => cropDeletesPixels;
        set { if (Set(ref cropDeletesPixels, value)) Settings.Set(Preferences.CropDeletesKey, value); }
    }

    private double shapeCornerRadius = 32.0, shapeStrokeWidth;
    public double ShapeCornerRadius { get => shapeCornerRadius; set => Set(ref shapeCornerRadius, value); }
    public double ShapeStrokeWidth { get => shapeStrokeWidth; set => Set(ref shapeStrokeWidth, value); }
    private SRGBColor? shapeFillColor;
    /// <summary>Fill of new shapes; null follows the foreground colour.</summary>
    public SRGBColor? ShapeFillColor { get => shapeFillColor; set => Set(ref shapeFillColor, value); }
    private SRGBColor shapeStrokeColor = new(0, 0, 0);
    public SRGBColor ShapeStrokeColor { get => shapeStrokeColor; set => Set(ref shapeStrokeColor, value); }
    private DocumentState? originalShapeState;
    private DragSegment? shapeDrag;
    public DragSegment? ShapeDrag { get => shapeDrag; set => Set(ref shapeDrag, value); }
    private GuideDrag? guideDrag;
    public GuideDrag? GuideDrag { get => guideDrag; set => Set(ref guideDrag, value); }

    // Phase 7: filters, retouching, liquify, channels
    private FilterSettings? filterDialog;
    public FilterSettings? FilterDialog { get => filterDialog; private set => Set(ref filterDialog, value); }
    private FilterSettings? lastFilter;
    public FilterSettings? LastFilter { get => lastFilter; private set => Set(ref lastFilter, value); }
    private bool isPreviewingFilter;
    public bool IsPreviewingFilter { get => isPreviewingFilter; private set => Set(ref isPreviewingFilter, value); }
    private FilterEngine? filterEngineCache;
    private IDisposable? filterPreviewTask;
    private int filterPreviewGeneration;
    /// <summary>While previewing a smart filter: the state to restore on Cancel, and the filter being edited.</summary>
    private DocumentState? smartFilterPreviewBase;
    private int? smartFilterEditIndex;
    private Vec2? cloneSource;
    public Vec2? CloneSource { get => cloneSource; private set => Set(ref cloneSource, value); }
    private bool cloneAligned = true;
    public bool CloneAligned { get => cloneAligned; set => Set(ref cloneAligned, value); }
    private Vec2? cloneOffset;
    private double retouchStrength = 0.5;
    public double RetouchStrength { get => retouchStrength; set => Set(ref retouchStrength, value); }
    private PatternKind patternKind = PatternKind.Checkerboard;
    public PatternKind PatternKind { get => patternKind; set => Set(ref patternKind, value); }
    private double patternScale = 32.0;
    public double PatternScale { get => patternScale; set => Set(ref patternScale, value); }
    private double dodgeExposure = 0.5;
    public double DodgeExposure { get => dodgeExposure; set => Set(ref dodgeExposure, value); }
    private int dodgeRange = 1;
    public int DodgeRange { get => dodgeRange; set => Set(ref dodgeRange, value); }
    private (SmudgeEngine Engine, StrokeSampler Sampler, Guid Layer, Vec2 Last)? smudge;
    private LiquifySession? liquify;
    /// <summary>Liquify in progress (non-null while the Liquify mode is open).</summary>
    public LiquifySession? Liquify { get => liquify; private set { liquify = value; Raise(); } }
    object? IEditorSession.Liquify => liquify;
    private LiquifyEngine.Mode liquifyMode = LiquifyEngine.Mode.Forward;
    public LiquifyEngine.Mode LiquifyMode { get => liquifyMode; set => Set(ref liquifyMode, value); }
    private double liquifySize = 150.0, liquifyPressure = 0.5;
    public double LiquifySize { get => liquifySize; set => Set(ref liquifySize, value); }
    public double LiquifyPressure { get => liquifyPressure; set => Set(ref liquifyPressure, value); }
    private int channelMask = 15;
    /// <summary>Which channels the canvas shows: bits 1 red, 2 green, 4 blue, 8 alpha (15 = the normal view).</summary>
    public int ChannelMask { get => channelMask; set => Set(ref channelMask, value); }
    private bool isPaletteOpen;
    public bool IsPaletteOpen { get => isPaletteOpen; set => Set(ref isPaletteOpen, value); }
    private bool showNavigator;
    public bool ShowNavigator
    {
        get => showNavigator;
        set { if (Set(ref showNavigator, value) && value) ScheduleNavigatorRefresh(0); }
    }
    private RgbaImage? navigatorImage;
    public RgbaImage? NavigatorImage { get => navigatorImage; private set => Set(ref navigatorImage, value); }
    private IDisposable? navigatorTask;
    private int navigatorGeneration;
    private LevelsHistogramFor? levelsHistogram;
    /// <summary>Histogram of what the Levels layer being edited receives (the layers below it).</summary>
    public LevelsHistogramFor? LevelsHistogram { get => levelsHistogram; private set => Set(ref levelsHistogram, value); }

    private (StrokeEngine Engine, StrokeSampler Sampler, Guid Layer, bool Mask)? stroke;
    private Vec2? lastStrokePoint;
    private (TileGrid Grid, uint ChangeCount)? clipboard;

    /// <summary>The floating side panel (layers and friends, or the assistant) and the tool rail.</summary>
    private bool showLayersPanel, showTools = true;
    public bool ShowLayersPanel { get => showLayersPanel; set => Set(ref showLayersPanel, value); }
    public bool ShowTools { get => showTools; set => Set(ref showTools, value); }
    private bool classicLayout;
    /// <summary>Classic layout docks the panel at the right edge and keeps it open (the web app's "Classic").</summary>
    public bool ClassicLayout
    {
        get => classicLayout;
        set { if (Set(ref classicLayout, value)) Settings.Set(Preferences.ClassicLayoutKey, value); }
    }
    private bool startCardDismissed;
    /// <summary>The start card on an untouched blank document can be put away.</summary>
    public bool StartCardDismissed { get => startCardDismissed; set => Set(ref startCardDismissed, value); }
    private string documentName = "Untitled";
    /// <summary>The window title (document name), shown in the chip at the top left.</summary>
    public string DocumentName { get => documentName; set => Set(ref documentName, value); }
    private bool isExportSheetPresented, isLayerStylePresented, isImageSizePresented, isCanvasSizePresented;
    public bool IsExportSheetPresented { get => isExportSheetPresented; set => Set(ref isExportSheetPresented, value); }
    public bool IsLayerStylePresented { get => isLayerStylePresented; set => Set(ref isLayerStylePresented, value); }
    public bool IsImageSizePresented { get => isImageSizePresented; set => Set(ref isImageSizePresented, value); }
    public bool IsCanvasSizePresented { get => isCanvasSizePresented; set => Set(ref isCanvasSizePresented, value); }
    private string? healWarning;
    /// <summary>Heal Selection asks before filling a large area: the reason, while the question is up.</summary>
    public string? HealWarning { get => healWarning; set => Set(ref healWarning, value); }
    private TextNotice? textNotice;
    /// <summary>A question or refusal about the text service, while it is up (AIController+TextGate).</summary>
    public TextNotice? TextNotice { get => textNotice; set => Set(ref textNotice, value); }
    /// <summary>What the import of a Photoshop file kept and lost, for this window. Shown once when the file opens and
    /// again from File › Import Report…. Not saved with the document.</summary>
    public PSDImportReport? ImportReport => State.ImportReport;
    private bool isImportReportPresented, isPSDExportPresented, isRawDevelopPresented, isBatchSheetPresented;
    public bool IsImportReportPresented { get => isImportReportPresented; set => Set(ref isImportReportPresented, value); }
    /// <summary>File › Export… › Photoshop File…: the sheet with what the file will hold.</summary>
    public bool IsPSDExportPresented { get => isPSDExportPresented; set => Set(ref isPSDExportPresented, value); }
    /// <summary>Layer › Develop…: the RAW layer being developed again, while the sheet is up.</summary>
    public bool IsRawDevelopPresented { get => isRawDevelopPresented; set => Set(ref isRawDevelopPresented, value); }
    public bool IsBatchSheetPresented { get => isBatchSheetPresented; set => Set(ref isBatchSheetPresented, value); }
    private ExportOptions exportOptions = new();
    public ExportOptions ExportOptions { get => exportOptions; set => Set(ref exportOptions, value); }
    private bool isExporting;
    public bool IsExporting { get => isExporting; private set => Set(ref isExporting, value); }
    private UiText? errorMessage;
    public UiText? ErrorMessage { get => errorMessage; set => Set(ref errorMessage, value); }
    private SiblingApp? offlineSibling;
    /// <summary>A sibling app the assistant needed but that isn't running (offers to open it).</summary>
    public SiblingApp? OfflineSibling { get => offlineSibling; set => Set(ref offlineSibling, value); }
    private string? explicitName;
    /// <summary>The name an agent or sibling gave the document (POST /documents with name).</summary>
    public string? ExplicitName { get => explicitName; private set => Set(ref explicitName, value); }
    /// <summary>Made through the agent API: never reused as the "untouched start document".</summary>
    public bool MadeByAPI { get; set; }

    /// <summary>Names the untitled document: the window title (and the name Save suggests) and the chip.</summary>
    public void SetDocumentName(string name)
    {
        ExplicitName = name;
        DocumentName = name;
    }

    /// <summary>The untouched start document takes another document's content (one undo step).</summary>
    public void ReplaceUntouched(DocumentState newState)
    {
        Document.BeginTransaction("New Document");   // an agent request: its own undo step
        Edit("New Document", _ => newState);
        Document.EndTransaction();
        Scheduler.Schedule(TimeSpan.FromMilliseconds(100), () => Canvas.Perform(CommandID.FitToWindow));
    }

    private int? rasterizeTextConfirmation;
    /// <summary>Set while asking whether a Free Transform may turn this many text layers into pixels.</summary>
    public int? RasterizeTextConfirmation { get => rasterizeTextConfirmation; set => Set(ref rasterizeTextConfirmation, value); }

    /// <summary>Raised to move the keyboard focus to the omnibar (/ on the canvas).</summary>
    public event EventHandler? OmnibarFocusRequested;
    private Guid? renameRequest;
    /// <summary>A layer whose name field should open (Return in the Layers panel).</summary>
    public Guid? RenameRequest { get => renameRequest; set => Set(ref renameRequest, value); }

    public void RequestOmnibarFocus()
    {
        if (Settings.Get(Preferences.ShowOmnibarKey, true) && !interfaceHidden) OmnibarFocusRequested?.Invoke(this, EventArgs.Empty);
        else IsPaletteOpen = true;
    }

    private UiText? toast;
    private IDisposable? toastTask;
    /// <summary>A short message shown over the canvas for a few seconds.</summary>
    public UiText? Toast { get => toast; private set => Set(ref toast, value); }

    /// <summary>The app announces it for screen readers when it changes (NSAccessibility announcement on the Mac).</summary>
    public void ShowToast(UiText message)
    {
        Toast = message;
        toastTask?.Dispose();
        toastTask = Scheduler.Schedule(TimeSpan.FromSeconds(3.5), () => Toast = null);
    }

    private Dictionary<Guid, RgbaImage> thumbnails = [], maskThumbnails = [];
    public IReadOnlyDictionary<Guid, RgbaImage> Thumbnails => thumbnails;
    public IReadOnlyDictionary<Guid, RgbaImage> MaskThumbnails => maskThumbnails;
    private ThumbnailRenderer? thumbnailRenderer;
    private Dictionary<string, long> thumbnailKeys = [];
    private IDisposable? thumbnailTask;
    private (Guid Layer, Vec2 Start, PixelRect? Bounds)? move;

    // MARK: Observation

    /// <summary>Counts changes of the document (the linked file's "saved back" mark compares with it).</summary>
    internal int StateGeneration { get; private set; }

    private void DocumentDidChange()
    {
        StateGeneration++;
        Canvas.DocumentSize = State.Size;
        Canvas.RequestRender();
        Raise(nameof(State), nameof(ActiveLayer), nameof(IsUntouchedBlank), nameof(PaintsMask), nameof(ActivePath), nameof(ImportReport));
        NoteSelectionChanged();
        thumbnailTask?.Dispose();
        thumbnailTask = Scheduler.Schedule(TimeSpan.FromMilliseconds(150), RefreshThumbnails);
        if (showNavigator) ScheduleNavigatorRefresh(400);
    }

    /// <summary>The navigator shows a small flattened preview; it's rebuilt off the main thread once edits pause (longer
    /// pauses for very large documents).</summary>
    public void ScheduleNavigatorRefresh(int delay)
    {
        navigatorTask?.Dispose();
        var snapshot = State;
        long pixels = (long)snapshot.Width * snapshot.Height;
        int generation = ++navigatorGeneration;
        navigatorTask = Scheduler.Schedule(TimeSpan.FromMilliseconds(delay + (pixels > 36_000_000 ? 1200 : 0)), () =>
            Scheduler.Background(() =>
            {
                using var full = ImageExporter.FlattenedSRGB(snapshot);
                using var small = GlimmerDocument.Downscaled(full, 480);
                return ToRgbaImage(small);
            }, image => NavigatorImage = image, () => generation != navigatorGeneration, _ => { }));
    }

    private IDisposable? levelsTask;
    private int levelsGeneration;

    /// <summary>Recomputes LevelsHistogram for <paramref name="layerId"/> off the main thread.</summary>
    public Task RefreshLevelsHistogram(Guid layerId)
    {
        var done = new TaskCompletionSource();
        levelsTask?.Dispose();
        var below = State.Below(layerId);
        int generation = ++levelsGeneration;
        levelsTask = Scheduler.Schedule(TimeSpan.FromMilliseconds(150), () =>
            Scheduler.Background(() =>
            {
                using var full = ImageExporter.FlattenedSRGB(below);
                return Histogram.FromImage(full.GetPixelSpan(), full.Width, full.Height);
            }, histogram =>
            {
                if (histogram is not null) LevelsHistogram = new LevelsHistogramFor(layerId, histogram);
                done.TrySetResult();
            }, () =>
            {
                bool stale = generation != levelsGeneration;
                if (stale) done.TrySetResult();
                return stale;
            }, _ => done.TrySetResult()));
        return done.Task;
    }

    /// <summary>An 8-bit sRGB bitmap as the views take pictures: BGRA, premultiplied.</summary>
    internal static RgbaImage ToRgbaImage(SKBitmap image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var bytes = new byte[info.BytesSize];
        using var source = SKImage.FromBitmap(image);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                // ReadPixels converts colour type, alpha type and colour space to what `info` says.
                source.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0);
            }
        }
        return new RgbaImage(info.Width, info.Height, info.RowBytes, bytes);
    }

    private void RefreshThumbnails()
    {
        thumbnailRenderer ??= new ThumbnailRenderer();
        var renderer = thumbnailRenderer;
        var next = new Dictionary<Guid, RgbaImage>();
        var nextMasks = new Dictionary<Guid, RgbaImage>();
        var keys = new Dictionary<string, long>();
        var bounds = State.Bounds;

        RgbaImage? Cached(string key, long signature, RgbaImage? old, Func<SKBitmap?> make)
        {
            long k = HashCode.Combine(signature, bounds);
            keys[key] = k;
            if (thumbnailKeys.TryGetValue(key, out var was) && was == k && old is not null) return old;
            using var image = make();
            return image is null ? null : ToRgbaImage(image);
        }

        foreach (var layer in State.AllLayers)
        {
            var id = layer.Id;
            switch (layer.Content)
            {
                case RasterContent r:
                    if (Cached(id.ToString(), r.Tiles.ContentSignature, thumbnails.GetValueOrDefault(id),
                               () => renderer.Thumbnail(r.Tiles, bounds, 96)) is { } t) next[id] = t;
                    break;
                case SmartContent s:
                    {
                        var src = new PixelRect(0, 0, s.Smart.Width, s.Smart.Height);
                        if (Cached(id.ToString(), s.Smart.Source.ContentSignature, thumbnails.GetValueOrDefault(id),
                                   () => renderer.Thumbnail(s.Smart.Source, src, 96)) is { } st) next[id] = st;
                        break;
                    }
            }
            if (layer.Mask is { } mask)
            {
                long signature = HashCode.Combine(mask.Tiles.ContentSignature, mask.DefaultValue);
                if (Cached($"{id}.mask", signature, maskThumbnails.GetValueOrDefault(id),
                           () => renderer.Thumbnail(mask.Tiles, bounds, 96, mask.DefaultValue)) is { } m) nextMasks[id] = m;
            }
        }
        thumbnailKeys = keys;
        thumbnails = next;
        maskThumbnails = nextMasks;
        Raise(nameof(Thumbnails), nameof(MaskThumbnails));
    }

    // MARK: Editing helpers

    /// <summary>One undoable step (Swift's edit(_:_:)): commits a pending live edit first, reports a failure.</summary>
    public void Edit(UiText name, Func<DocumentState, DocumentState> change)
    {
        CommitLiveEdit();
        try
        {
            Document.Edit(name, change);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    public void Report(Exception error)
    {
        // A model that isn't downloaded: ask the person (never downloaded without their click).
        if (error is ModelMissingError missing && !AI.RunningAPISteps)
        {
            AI.ModelRequest = new ModelDownloadRequest(missing.Missing, null);
            return;
        }
        ErrorMessage = PersonMessage.Text(error);
    }

    // MARK: Live edits (sliders, colour wells)

    private (UiText Name, DocumentState Before)? liveEdit;
    private IDisposable? liveEditCommit;

    /// <summary>Applies a change immediately and records one undo step once the control goes quiet (or CommitLiveEdit
    /// is called, e.g. when a drag ends).</summary>
    public void Live(UiText name, Func<DocumentState, DocumentState> change)
    {
        if (liveEdit is null || !liveEdit.Value.Name.Equals(name))
        {
            CommitLiveEdit();
            liveEdit = (name, State);
        }
        Document.State = change(Document.State);
        liveEditCommit?.Dispose();
        liveEditCommit = Scheduler.Schedule(TimeSpan.FromMilliseconds(600), CommitLiveEdit);
    }

    public void CommitLiveEdit()
    {
        liveEditCommit?.Dispose();
        liveEditCommit = null;
        if (liveEdit is not { } edit) return;
        liveEdit = null;
        Document.CommitInteractiveEdit(edit.Name, edit.Before);
    }

    /// <summary>Discards an in-progress live edit (e.g. Cancel in a dialog).</summary>
    public void RevertLiveEdit(DocumentState snapshot)
    {
        liveEditCommit?.Dispose();
        liveEdit = null;
        Document.State = snapshot;
    }

    /// <summary>Live change to the active layer.</summary>
    public void LiveLayer(string actionName, Func<Layer, Layer> change) => LiveLayer(new UiText(actionName), change);

    public void LiveLayer(UiText actionName, Func<Layer, Layer> change)
    {
        if (State.ActiveLayerID is not { } id) return;
        Live(actionName, s => s.UpdateLayer(id, change));
    }

    // MARK: Marquee tools

    public void BeginMarquee(Vec2 point, InputModifiers modifiers)
    {
        CommitLiveEdit();
        var op = OperationFor(modifiers);
        Marquee = new Marquee(point, point, tool == ToolID.EllipseMarquee, ToContract(op), false);
    }

    public void UpdateMarquee(Vec2 point)
    {
        if (marquee is { } m) Marquee = m with { Current = point };
    }

    public void EndMarquee()
    {
        if (marquee is not { } m) return;
        Marquee = null;
        var r = m.CGRect;
        var op = FromContract(m.Operation);
        if (r.Width < 1 || r.Height < 1)
        {
            // A click without a drag deselects (replace mode only).
            if (op == Selection.Operation.Replace && State.Selection is not null) Edit("Deselect", s => s with { Selection = null });
            return;
        }
        var shape = m.Ellipse ? Selection.Ellipse(r) : Selection.Rectangle(r);
        ApplySelection(shape, op, m.Ellipse ? "Elliptical Marquee" : "Rectangular Marquee");
    }

    internal static SelectionOperation ToContract(Selection.Operation op) => op switch
    {
        Selection.Operation.Add => SelectionOperation.Add,
        Selection.Operation.Subtract => SelectionOperation.Subtract,
        Selection.Operation.Intersect => SelectionOperation.Intersect,
        _ => SelectionOperation.Replace,
    };

    internal static Selection.Operation FromContract(SelectionOperation op) => op switch
    {
        SelectionOperation.Add => Selection.Operation.Add,
        SelectionOperation.Subtract => Selection.Operation.Subtract,
        SelectionOperation.Intersect => Selection.Operation.Intersect,
        _ => Selection.Operation.Replace,
    };

    public void AddLayer(Layer layer, UiText actionName) => Edit(actionName, s => s.InsertLayerAboveActive(layer));

    private void EditActive(UiText name, Func<Layer, Layer> change)
    {
        if (State.ActiveLayerID is not { } id) return;
        Edit(name, s => s.UpdateLayer(id, change));
    }

    // MARK: Smart objects

    private void ConvertToSmartObject()
    {
        if (ActiveLayer is not { } layer) return;
        // Text becomes a smart object of its rendered pixels (no longer editable as text).
        TileGrid grid;
        switch (layer.Content)
        {
            case RasterContent r: grid = r.Tiles; break;
            case TextContent t: grid = DocumentCompositor.Rasterize(t.Text); break;
            default: return;
        }
        if (grid.TileBounds is not { } bounds) return;
        try
        {
            var source = TileOperations.Translate(grid, (-bounds.X, -bounds.Y));
            var smart = new SmartObject(source, bounds.Width, bounds.Height, new AffineTransform(tx: bounds.X, ty: bounds.Y));
            EditActive("Convert to Smart Object", l => l with { Content = new SmartContent(smart) });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private void RasterizeActiveLayer()
    {
        if (ActiveLayer is not { } layer) return;
        try
        {
            var bounds = RasterizeBounds(layer) ?? State.Bounds;
            var tiles = DocumentCompositor.Rasterize(layer, bounds, State.BlendSpace);
            EditActive(layer.IsGroup ? "Merge Group" : "Rasterize Layer", l => l with
            {
                Content = new RasterContent(tiles),
                BlendMode = l.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : l.BlendMode,
            });
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>The area worth rasterizing: the document, plus content hanging outside it.</summary>
    private PixelRect? RasterizeBounds(Layer layer) => layer.Content switch
    {
        SmartContent s => s.Smart.Bounds.Intersection(new PixelRect(-8192, -8192, 16384 + State.Width, 16384 + State.Height)),
        _ => State.Bounds,
    };

    // MARK: Layer panel actions

    public void SetActiveLayer(Guid? id)
    {
        if (id is not { } target || target == State.ActiveLayerID) return;
        CommitLiveEdit();
        // Selection is not an undoable edit.
        Document.State = State with { ActiveLayerID = target };
    }

    public void SetVisibility(Guid id, bool visible) =>
        Edit(visible ? "Show Layer" : "Hide Layer", s => s.UpdateLayer(id, l => l with { IsVisible = visible }));

    public void SetExpanded(Guid id, bool expanded)
    {
        // Disclosure state is saved with the document but isn't an undoable edit.
        Document.State = State.UpdateLayer(id, layer => layer.Content is GroupContent g
            ? layer with { Content = new GroupContent(g.Group with { IsExpanded = expanded }) }
            : layer);
    }

    public void Rename(Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || State.LayerById(id)?.Name == trimmed) return;
        Edit("Rename Layer", s => s.UpdateLayer(id, l => l with { Name = trimmed }));
    }

    public void SetBlendMode(BlendMode mode)
    {
        if (State.ActiveLayerID is not { } id || ActiveLayer?.BlendMode == mode) return;
        Edit("Change Blend Mode", s => s.UpdateLayer(id, l => l with { BlendMode = mode }));
    }

    /// <summary>Live opacity change during a slider drag; one undo step on release.</summary>
    public void SetOpacity(double value) => LiveLayer("Change Opacity", l => l with { Opacity = Math.Min(Math.Max(value, 0), 1) });

    /// <summary>Moves <paramref name="id"/> so it appears directly above the row <paramref name="above"/> (null = bottom of the stack).</summary>
    public void MoveLayer(Guid id, Guid? above) => Edit("Reorder Layers", s => s.MoveLayerAbove(id, above));

    public void MoveLayerIntoGroup(Guid id, Guid group) => Edit("Move Into Group", s => s.MoveLayerIntoTopOf(id, group));

    // MARK: Move tool

    public void BeginMove(Vec2 point)
    {
        if (ActiveLayer is not { } layer || layer.IsAdjustment) return;
        CommitLiveEdit();
        move = (layer.Id, point, State.Aids.Snap ? SnapBounds(layer) : null);
    }

    /// <summary>What the Move tool snaps: the layer's visible extent.</summary>
    private PixelRect? SnapBounds(Layer layer)
    {
        switch (layer.Content)
        {
            case RasterContent r: return r.Tiles.TileBounds is { } tb ? ContentBounds(r.Tiles, tb) : null;
            case TextContent t: return t.Text.Bounds;
            case SmartContent s: return s.Smart.Bounds;
            default:
                if (layer.VectorMask?.ControlBounds is not { } rect) return null;
                return new PixelRect((int)rect.MinX.Rounded(), (int)rect.MinY.Rounded(), (int)rect.Width.Rounded(), (int)rect.Height.Rounded());
        }
    }

    public void UpdateMove(Vec2 point)
    {
        if (move is not { } m) return;
        var d = point - m.Start;
        if (m.Bounds is { } b) d += SnapOffset(new Vec2(b.X, b.Y) + d, new Vec2(b.Width, b.Height));
        Overrides = new LayerOverrides(offsets: [new(m.Layer, ((int)d.X.Rounded(), (int)d.Y.Rounded()))]);
    }

    public void EndMove()
    {
        if (move is not { } m) return;
        var offset = overrides.Offsets.TryGetValue(m.Layer, out var o) ? o : (0, 0);
        move = null;
        CommitTranslation(m.Layer, offset, "Move");
        Overrides = LayerOverrides.None;
    }

    public void NudgeActiveLayer(int dx, int dy)
    {
        if (State.ActiveLayerID is not { } id) return;
        CommitTranslation(id, (dx, dy), "Nudge");
    }

    private void CommitTranslation(Guid id, (int X, int Y) offset, UiText actionName)
    {
        if (offset == (0, 0) || State.LayerById(id) is not { } layer) return;
        try
        {
            var moved = TileOperations.Translate(layer, offset);
            Edit(actionName, s => s.UpdateLayer(id, _ => moved));
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>A new document nobody has drawn in or changed.</summary>
    public bool IsUntouchedBlank => Document.History.Count == 0 && State.TextureTileIDs.Count == 0 && State.AllLayers.Length <= 1;

    // MARK: Placing images

    public void ChoosePlaceImage()
    {
        var paths = Environment.Dialogs.ChooseImages("Choose images to place as new layers", "Place", multiple: true);
        if (paths.Count > 0) Place(paths);
    }

    public void Place(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var image = ImageImporter.Image(path);
                Place(image, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception error)
            {
                Report(error);
            }
        }
    }

    /// <summary>Adds <paramref name="image"/> as a new layer centred on the document.</summary>
    public void Place(DecodedImage image, string name)
    {
        if (IsUntouchedBlank)
        {
            // Nothing here yet: the image becomes the document, at its own size.
            var tiles = ImageImporter.Tiles(image, (0, 0));
            var layer = new Layer(name, new RasterContent(tiles));
            int w = image.Width, h = image.Height;
            Edit("Open Image", s => s with { Width = w, Height = h, Layers = [layer], ActiveLayerID = layer.Id, Selection = null });
            Scheduler.Schedule(TimeSpan.FromMilliseconds(100), () => Canvas.Perform(CommandID.FitToWindow));
            return;
        }
        var origin = ((State.Width - image.Width) / 2, (State.Height - image.Height) / 2);
        var placed = new Layer(name, new RasterContent(ImageImporter.Tiles(image, origin)));
        Edit("Place Image", s => s.InsertLayerAboveActive(placed));
        NameByContent(placed.Id, image, name);
    }

    /// <summary>Gives a layer with a camera or clipboard name ("IMG_1234", "Pasted Layer") a name that says what it shows,
    /// found on this computer. Not an undo step: it only replaces a name nobody chose.</summary>
    public void NameByContent(Guid id, DecodedImage image, string currentName)
    {
        // A name is a nicety: without the classifier there is nothing to do (it is never downloaded for this).
        if (!ImageLabel.IsGeneric(currentName) || !ModelStore.Shared.IsInstalled(OnDeviceModel.ImageLabels)) return;
        var picture = new DocumentState(image.Width, image.Height, layers: [new Layer("", new RasterContent(ImageImporter.Tiles(image)))]);
        Scheduler.Background(() =>
        {
            using var flat = ImageExporter.FlattenedSRGB(picture);
            return ImageLabel.NameAsync(RGBAImage.FromSKBitmap(flat).Limited(512)).GetAwaiter().GetResult();
        }, label =>
        {
            if (label is null || State.AllLayers.FirstOrDefault(l => l.Id == id)?.Name != currentName) return;
            Document.State = State.UpdateLayer(id, l => l with { Name = label });
        }, failed: _ => { });
    }

    /// <summary>Places encoded image data (PNG, JPEG, BMP, TIFF) from the clipboard or a drop.</summary>
    public void PlaceImageData(byte[] encoded, string name)
    {
        try
        {
            var image = ImageImporter.Image(encoded);
            Place(image, name);
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Places images from the clipboard (files or image data). Returns false if there were none.</summary>
    public bool PlaceFromClipboard()
    {
        var files = Environment.Clipboard.GetImageFiles();
        if (files.Count > 0)
        {
            Place(files);
            return true;
        }
        if (Environment.Clipboard.GetImagePng() is { } png)
        {
            PlaceImageData(png, Environment.LayerName("Pasted Layer"));
            return true;
        }
        return false;
    }

    // MARK: Export

    /// <summary>Saves the document as a project for Glimmer for the web.</summary>
    public void ExportWebProject()
    {
        CommitLiveEdit();
        var path = Environment.Dialogs.ChooseSaveLocation($"{DocumentName}.aips", ["aips"],
            "Fill layers, shapes and smart filters are saved as pixels; the web app has no equivalent.");
        if (path is null) return;
        var snapshot = State;
        IsExporting = true;
        Scheduler.Background(() =>
        {
            File.WriteAllBytes(path, AipsExporter.ExportProject(snapshot));
            return true;
        }, _ => IsExporting = false, failed: error =>
        {
            IsExporting = false;
            Report(error);
        });
    }

    public void Export(ExportOptions options)
    {
        string ext = options.Format == ExportFormat.Jpeg ? "jpg" : options.Format.FileExtension();
        var path = Environment.Dialogs.ChooseSaveLocation($"Untitled.{ext}", [ext]);
        if (path is null) return;
        Export(options, path);
    }

    /// <summary>Exports to <paramref name="path"/> without asking (after the save dialog, and for tests and agents).</summary>
    public void Export(ExportOptions options, string path, Action<Exception?>? finished = null)
    {
        var snapshot = State;
        IsExporting = true;
        Scheduler.Background(() =>
        {
            ImageExporter.Export(snapshot, options, path);
            return new FileInfo(path).Length;
        }, bytes =>
        {
            IsExporting = false;
            ShowToast(new UiText("Exported {0} ({1}).", Path.GetFileName(path), EditorHelpers.FileSize(bytes)));
            finished?.Invoke(null);
        }, failed: error =>
        {
            IsExporting = false;
            Report(error);
            finished?.Invoke(error);
        });
    }
}
