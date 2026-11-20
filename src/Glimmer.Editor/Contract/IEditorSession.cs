// View contract (see ViewContractTypes.cs for the rules every member follows).
// Mirrors the members of App/EditorSession*.swift that the views of App/ read or call. Grouped as the views use them.
using System.Collections.Immutable;
using System.ComponentModel;
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Render;
using Glimmer.Tools;

namespace Glimmer.Editor;

/// <summary>Per-window editing state (EditorSession). One per document tab.</summary>
public interface IEditorSession : INotifyPropertyChanged
{
    // ---------------------------------------------------------------------------------------------- document

    IEditorDocument Document { get; }
    /// <summary>Document.State. Raised whenever the document changes (every edit, undo, redo, live edit).</summary>
    DocumentState State { get; }
    Layer? ActiveLayer { get; }
    ICanvasController Canvas { get; }
    IAIController AI { get; }

    /// <summary>The window title shown in the title chip and the tab ("Untitled", the file's name).</summary>
    string DocumentName { get; }
    /// <summary>The name an agent or sibling gave the document (overrides DocumentName in the chip).</summary>
    string? ExplicitName { get; }
    /// <summary>A new document nobody has drawn in or changed (EditorSession.isUntouchedBlank).</summary>
    bool IsUntouchedBlank { get; }
    /// <summary>Painting goes to the active layer's mask (Editing mask label in the title chip).</summary>
    bool PaintsMask { get; }
    bool IsExporting { get; }
    /// <summary>What the import of a Photoshop file kept and lost (the Import Report sheet); null for other documents.</summary>
    PSDImportReport? ImportReport { get; }

    /// <summary>Closing the tab: asks about unsaved changes (IDocumentHost.AskToSaveChanges) or, for a linked document
    /// with steps not saved back, the linked question; saves when asked to. True when the tab may close now (the
    /// session has then let go of its files: recovery copy, undo steps on disk).</summary>
    bool TryClose();

    // ---------------------------------------------------------------------------------------------- commands

    bool CanPerform(CommandID id);
    void Perform(CommandID id);
    /// <summary>Checkmark of a toggle menu item (Rulers, Grid, Snap, Navigator, Classic Layout, Hide Interface, ...).</summary>
    bool IsChecked(CommandID id);
    /// <summary>A menu title that depends on the document: "Undo {0}" with the step's name, "Save Back to {0}" while
    /// linked. Null: the descriptor's title.</summary>
    UiText? Title(CommandID id);
    /// <summary>Runs a palette entry (command, tool, preset or saved action); false when it isn't available now.</summary>
    bool RunPaletteItem(PaletteItem item);

    // ---------------------------------------------------------------------------------------------- messages

    /// <summary>A short message over the canvas for a few seconds (EditorSession.toast). The session clears it.</summary>
    UiText? Toast { get; }
    /// <summary>The alert "Glimmer" with OK. Set to null when the alert is dismissed.</summary>
    UiText? ErrorMessage { get; set; }
    /// <summary>Heal Selection asks before filling a large area: the reason, while the question is up.</summary>
    string? HealWarning { get; set; }
    TextNotice? TextNotice { get; set; }
    /// <summary>Set while asking whether a Free Transform may turn this many text layers into pixels.</summary>
    int? RasterizeTextConfirmation { get; set; }
    /// <summary>A sibling app the assistant needed but that isn't running (offers to open it).</summary>
    SiblingApp? OfflineSibling { get; set; }
    void Report(Exception error);
    /// <summary>EditorSession.showToast (a sheet that finished its work: "Exported … (2.4 MB).").</summary>
    void ShowToast(UiText text);

    // ---------------------------------------------------------------------------------------------- chrome

    bool ShowTools { get; set; }
    bool ShowLayersPanel { get; set; }
    /// <summary>Docks the side panel at the right edge with every tab (persisted, "ui.classicLayout").</summary>
    bool ClassicLayout { get; set; }
    InspectorTab InspectorTab { get; set; }
    AISection AISection { get; set; }
    /// <summary>Tab hides every panel for a clean view of the image.</summary>
    bool InterfaceHidden { get; set; }
    bool ShowNavigator { get; set; }
    bool IsPaletteOpen { get; set; }
    bool StartCardDismissed { get; set; }
    /// <summary>Raised to move the keyboard focus to the omnibar ("/" on the canvas). Swift bumps a counter.</summary>
    event EventHandler? OmnibarFocusRequested;
    void RequestOmnibarFocus();
    /// <summary>A layer whose name field should open (Return in the Layers panel); the row clears it.</summary>
    Guid? RenameRequest { get; set; }
    void ShowAI(AISection section);

    // Sheets: a view shows the sheet while the flag is true and sets it false when the sheet closes.
    bool IsExportSheetPresented { get; set; }
    bool IsCanvasSizePresented { get; set; }
    bool IsImageSizePresented { get; set; }
    bool IsLayerStylePresented { get; set; }
    bool IsBatchSheetPresented { get; set; }
    bool IsImportReportPresented { get; set; }
    bool IsPSDExportPresented { get; set; }
    bool IsRawDevelopPresented { get; set; }
    /// <summary>Feather…, Expand… or Contract… while its radius sheet is up.</summary>
    CommandID? SelectionDialog { get; set; }
    /// <summary>The filter whose floating dialog is open (non-modal: the canvas shows the live preview).</summary>
    FilterSettings? FilterDialog { get; }

    // Linked editing (the title chip says these; EditorSession+Linked.swift).
    LinkedImage? LinkedImage { get; }
    BrokenLink? BrokenLink { get; }
    bool Recovered { get; }
    bool KeptFromLastTime { get; }
    string? JournalRoomTrouble { get; }

    // ---------------------------------------------------------------------------------------------- live edits

    /// <summary>Records the pending live edit (slider drag, colour well) as one undo step now.</summary>
    void CommitLiveEdit();
    /// <summary>Discards an in-progress live edit (Cancel in a dialog).</summary>
    void RevertLiveEdit(DocumentState snapshot);
    /// <summary>Applies a change to the active layer now and records one undo step once the control goes quiet.</summary>
    void LiveLayer(string actionName, Func<Layer, Layer> change);

    // ---------------------------------------------------------------------------------------------- thumbnails

    IReadOnlyDictionary<Guid, RgbaImage> Thumbnails { get; }
    IReadOnlyDictionary<Guid, RgbaImage> MaskThumbnails { get; }
    /// <summary>A small flattened preview for the navigator (rebuilt off the main thread once edits pause).</summary>
    RgbaImage? NavigatorImage { get; }
    LevelsHistogramFor? LevelsHistogram { get; }
    Task RefreshLevelsHistogram(Guid layerId);

    // ---------------------------------------------------------------------------------------------- layers panel

    void SetActiveLayer(Guid? id);
    void SetVisibility(Guid id, bool visible);
    void SetExpanded(Guid id, bool expanded);
    void Rename(Guid id, string name);
    void SetBlendMode(BlendMode mode);
    /// <summary>Live opacity change during a slider drag; one undo step on release (CommitLiveEdit).</summary>
    void SetOpacity(double value);
    /// <summary>Moves id so it sits directly above target (null = bottom of the stack).</summary>
    void MoveLayer(Guid id, Guid? above);
    void MoveLayerIntoGroup(Guid id, Guid group);
    void SetSmartFilterEnabled(Guid layerId, int index, bool enabled);
    void EditSmartFilter(Guid layerId, int index);
    void DeleteSmartFilter(Guid layerId, int index);

    // ---------------------------------------------------------------------------------------------- paths & channels

    Guid? ActivePathID { get; set; }
    VectorPath? ActivePath { get; }
    void FillPath(Guid id);
    void StrokePath(Guid id);
    void MakeSelectionFromPath(Guid id);
    void AddTextOnPath(Guid id);
    void DeletePath(Guid id);
    void RenamePath(Guid id, string name);
    /// <summary>Which channels the canvas shows: bits 1 red, 2 green, 4 blue, 8 alpha (15 = the normal view).</summary>
    int ChannelMask { get; set; }
    /// <summary>0 = luminosity of the composite, 1-3 red/green/blue, 4 alpha.</summary>
    void LoadChannelAsSelection(int row);

    // ---------------------------------------------------------------------------------------------- tools & options

    ToolID Tool { get; set; }
    /// <summary>Space held: the Hand tool takes over temporarily.</summary>
    bool TemporaryHand { get; set; }
    ToolID EffectiveTool { get; }
    BrushSettings Brush { get; set; }
    SRGBColor Foreground { get; set; }
    SRGBColor Background { get; set; }
    double ForegroundOpacity { get; set; }
    double BackgroundOpacity { get; set; }
    void SwapColors();
    void ResetColors();
    void AdjustBrushSize(double factor);
    bool EditingMask { get; set; }
    double WandTolerance { get; set; }
    bool WandContiguous { get; set; }
    bool SampleAllLayers { get; set; }
    GradientStyle GradientStyle { get; set; }
    bool GradientToTransparent { get; set; }
    bool GradientReverse { get; set; }
    /// <summary>Custom stops for the Gradient tool; null = foreground to background.</summary>
    ImmutableArray<GradientStop>? GradientStops { get; set; }
    ImmutableArray<GradientStop> CurrentGradientStops { get; }
    /// <summary>Feather applied to selections as the marquee, lasso and wand make them (pixels).</summary>
    double SelectionFeather { get; set; }
    PatternKind PatternKind { get; set; }
    double PatternScale { get; set; }
    double RetouchStrength { get; set; }
    double DodgeExposure { get; set; }
    /// <summary>0 shadows, 1 midtones, 2 highlights.</summary>
    int DodgeRange { get; set; }
    bool CloneAligned { get; set; }
    Vec2? CloneSource { get; }
    ShapeKind ShapeKind { get; set; }
    double ShapeLineWidth { get; set; }
    double ShapeCornerRadius { get; set; }
    double ShapeStrokeWidth { get; set; }
    /// <summary>Fill of new shapes; null follows the foreground colour.</summary>
    SRGBColor? ShapeFillColor { get; set; }
    SRGBColor ShapeStrokeColor { get; set; }
    /// <summary>Crop throws away pixels outside the new canvas (persisted, "crop.deletePixels").</summary>
    bool CropDeletesPixels { get; set; }
    LiquifyEngine.Mode LiquifyMode { get; set; }
    double LiquifySize { get; set; }
    double LiquifyPressure { get; set; }
    TextDefaults TextDefaults { get; set; }
    /// <summary>Feather…, Expand…, Contract… with the radius from SelectionModifySheet.</summary>
    void ModifySelection(CommandID command, double radius);

    // ---------------------------------------------------------------------------------------------- modal tools

    ITransformSession? TransformSession { get; }
    void SetTransformMode(TransformMode mode);
    void SetTransformScaleXPercent(double value);
    void SetTransformScaleYPercent(double value);
    void SetTransformDegrees(double value);
    /// <summary>confirmed: the person agreed that text layers become pixels (RasterizeTextConfirmation).</summary>
    void CommitTransform(bool confirmed = false);
    void CancelTransform();

    ICropSession? CropSession { get; }
    void SetCropAspect(double? aspect);
    void SetCropAngle(double degrees);
    void ApplyCrop();
    /// <summary>Cancel in the options bar and Esc on the canvas (Swift sets cropSession = nil).</summary>
    void CancelCrop();
    /// <summary>The eight handle points of a crop rectangle, in crop space (EditorSession.cropHandles).</summary>
    IReadOnlyList<Vec2> CropHandles(CGRectLike rect);

    /// <summary>Non-null while Liquify is open.</summary>
    object? Liquify { get; }
    void RestoreAllLiquify();
    void CancelLiquify();
    void CommitLiquify();

    // ---------------------------------------------------------------------------------------------- filters

    bool IsPreviewingFilter { get; }
    FilterSettings? LastFilter { get; }
    void PreviewFilter(FilterSettings settings);
    /// <summary>Preview switched off in the dialog: stop the preview task and clear the canvas overrides.</summary>
    void StopFilterPreview();
    void ApplyFilter(FilterSettings settings);
    void CancelFilterDialog();

    // ---------------------------------------------------------------------------------------------- text tool

    /// <summary>The text layer being typed into. The canvas opens its editor over it while this is set and calls CommitText
    /// when it changes away. While it is set the renderer leaves that layer's own pixels out, since the editor shows the
    /// text (Swift: CanvasNSView sets overrides to an empty tile grid for it; here the session does it).</summary>
    Guid? EditingText { get; set; }
    /// <summary>While text is being edited: the style at the caret or at the start of the selection.</summary>
    TextStyle? TextSelectionStyle { get; set; }
    bool TextSelectionIsRange { get; set; }
    /// <summary>The open text editor, which styles the selected words (set by the canvas while editing).</summary>
    ITextStyler? TextStyler { get; set; }
    void UpdateText(Func<TextLayer, TextLayer> change);
    void UpdateTextWarp(TextWarp? warp);
    void SetTextPath(Guid? pathId);
    /// <summary>The editor closed: its text and the style of every stretch (UTF-16 ranges, "\n" between lines), as
    /// TextLayer.SetStyledText takes them. One undo step when anything changed.</summary>
    void CommitText(string text, IReadOnlyList<(TextRange Range, TextStyle Style)> runs, Guid layerId);

    // ---------------------------------------------------------------------------------------------- export & sheets

    ExportOptions ExportOptions { get; set; }
    void Export(ExportOptions options);
    /// <summary>Image Size… (throws when the size is refused).</summary>
    void ResizeImage(int width, int height);
    /// <summary>Canvas Size…: anchor is (column, row), 0-2 each (throws when the size is refused).</summary>
    void ResizeCanvas(int width, int height, int anchorX, int anchorY);
    /// <summary>Places image files as new layers (or as the document when it is untouched).</summary>
    void Place(IReadOnlyList<string> paths);
    /// <summary>Places encoded image data (PNG, JPEG, BMP, TIFF) from the clipboard or a drop.</summary>
    void PlaceImageData(byte[] encoded, string name);
    /// <summary>Paste: Glimmer's own pixels if they are still on the clipboard, else an image. False: nothing to paste.</summary>
    bool Paste();

    // ---------------------------------------------------------------------------------------------- canvas input

    /// <summary>Non-undoable preview of an in-progress tool action (the canvas renderer reads it).</summary>
    LayerOverrides Overrides { get; set; }
    /// <summary>Pointer position over the canvas, for the brush cursor (view points). Null when outside.</summary>
    Vec2? HoverPoint { get; set; }
    Marquee? Marquee { get; set; }
    /// <summary>Lasso being drawn (document points).</summary>
    IReadOnlyList<Vec2>? Lasso { get; }
    DragSegment? GradientDrag { get; set; }
    DragSegment? ShapeDrag { get; set; }
    GuideDrag? GuideDrag { get; set; }
    /// <summary>Where the clone source marker sits for a brush at `point` (a document point), or null.</summary>
    Vec2? CloneMarker(Vec2? point);
    /// <summary>The selection's outline as closed polylines in document pixels, recomputed when the selection changes
    /// (the canvas draws the marching ants from it as an animated overlay, so the renderer never draws them).</summary>
    IReadOnlyList<IReadOnlyList<Vec2>> SelectionOutline { get; }
    /// <summary>Snaps a document point to guides, grid and the document edges when Snap is on.</summary>
    Vec2 Snapped(Vec2 point);

    TextLayer? TextLayerAt(Vec2 point);
    void TypeClick(Vec2 point);
    void BeginMove(Vec2 point);
    void UpdateMove(Vec2 point);
    void EndMove();
    void NudgeActiveLayer(int dx, int dy);
    void BeginMarquee(Vec2 point, InputModifiers modifiers);
    void UpdateMarquee(Vec2 point);
    void EndMarquee();
    void SetCloneSource(Vec2 point);
    void BeginStroke(PointerSample sample, bool erase, bool straightLine);
    void ContinueStroke(PointerSample sample);
    void EndStroke(Vec2 point);
    void BeginLasso(Vec2 point, InputModifiers modifiers);
    void ExtendLasso(Vec2 point);
    void FinishLasso();
    void CancelLasso();
    void PolygonClick(Vec2 point, InputModifiers modifiers, bool doubleClick);
    void MagicWand(Vec2 point, InputModifiers modifiers);
    void PaintBucket(Vec2 point);
    void FinishGradient();
    void SampleColor(Vec2 point, bool intoBackground);
    void TransformMouseDown(Vec2 point, double viewTolerance, bool command);
    void TransformDrag(Vec2 point, bool shift);
    void TransformMouseUp();
    void CropMouseDown(Vec2 point, double viewTolerance);
    void CropDrag(Vec2 point, bool fromCentre, bool constrain);
    void CropMouseUp();
    void PenMouseDown(Vec2 point, double viewTolerance);
    void PenDragged(Vec2 point, bool breakSymmetry);
    void PenMouseUp();
    void FinishPathSegment();
    void DiscardUnfinishedPath();
    void FinishShape();
    void AddGuide(GuideAxis axis, double position);
    void MoveGuide(Guid id, double position, bool live);
    void RemoveGuide(Guid id);
    void LiquifyDrag(Vec2 point, bool start, double pressure);
    void LiquifyMouseUp();
}

/// <summary>The undo history of a document, and its file (the parts of GlimmerDocument the views read).</summary>
public interface IEditorDocument : INotifyPropertyChanged
{
    DocumentState State { get; }
    /// <summary>Step names, oldest first. Each is a catalogue key, or a template with translated parts ("New {0}"
    /// with "Curves"): the views translate them.</summary>
    IReadOnlyList<UiText> History { get; }
    /// <summary>How many steps are applied: 0 = the document as opened.</summary>
    int HistoryIndex { get; }
    /// <summary>Older steps moved to disk to keep memory within budget.</summary>
    int StepsOnDisk { get; }
    /// <summary>Settings › Performance › History states.</summary>
    int HistoryLimit { get; }
    bool HasUnsavedChanges { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    /// <summary>The .glimmer file this document was opened from or saved to; null while untitled.</summary>
    string? FilePath { get; }
    /// <summary>Said once by the window, then set to null: the disk is too full for the whole undo history, or the
    /// document has outgrown this computer's memory.</summary>
    UiText? DiskNotice { get; set; }
    void JumpToHistory(int index);
    void Undo();
    void Redo();
}

/// <summary>Free Transform while it is open (EditorSession+Transform.swift): what the canvas draws and the options
/// bar shows. Points are in document pixels.</summary>
public interface ITransformSession
{
    TransformMode Mode { get; }
    /// <summary>The four corners, clockwise from the top left (TransformSession.corners).</summary>
    IReadOnlyList<Vec2> Corners { get; }
    /// <summary>The four edge midpoints (TransformSession.edges).</summary>
    IReadOnlyList<Vec2> Edges { get; }
    /// <summary>True while a rotation drag is in progress (handle == .rotate): the canvas shows the angle.</summary>
    bool IsRotating { get; }
    /// <summary>Radians, clockwise.</summary>
    double Rotation { get; }
    Vec2 Centre { get; }
    double ScaleX { get; }
    double ScaleY { get; }
    /// <summary>Distort, skew or perspective is in effect (TransformSession.quad != nil).</summary>
    bool IsQuad { get; }
}

/// <summary>Crop while it is open (EditorSession+Crop.swift).</summary>
public interface ICropSession
{
    /// <summary>The crop rectangle in crop space (unrotated).</summary>
    CGRectLike Rect { get; }
    /// <summary>Radians; the straighten angle.</summary>
    double Angle { get; }
    /// <summary>Width / height, or null for free.</summary>
    double? Aspect { get; }
    /// <summary>The rectangle's corners in document pixels.</summary>
    IReadOnlyList<Vec2> Corners { get; }
    /// <summary>Maps a point from crop space to document pixels.</summary>
    Vec2 World(Vec2 cropPoint);
}

/// <summary>Styles the selected words of the open text editor (EditorSession+Text.swift TextStyler). The canvas
/// implements it while a text layer is being edited; the options bar calls it.</summary>
public interface ITextStyler
{
    void Style(bool selectionOnly, string? fontName, double? size, SRGBColor? color);
}
