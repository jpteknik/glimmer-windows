// Ported from App/EditorSession.swift (MARK: Commands), with the commands the Windows app performs itself
// (WindowsCommands: Save, Save As, Save a Copy for Mac, Close, Paste; Undo and Redo, which the Mac's system menu sends).
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;
using Glimmer.Tools;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    public bool CanPerform(CommandID id)
    {
        var path = State.PathOf(State.ActiveLayerID);
        var layer = State.ActiveLayer;
        if (id.AdjustmentKind is not null) return true;
        if (id.FilterKind is not null)
        {
            if (layer?.Content is SmartContent) return true;
            return layer?.IsRaster == true && !layer.Tiles.IsEmpty;
        }
        if (id == CommandID.Liquify) return layer?.IsRaster == true && !layer.Tiles.IsEmpty;
        if (id == CommandID.RepeatFilter)
        {
            if (layer?.Content is SmartContent) return lastFilter is not null;
            return lastFilter is not null && layer?.IsRaster == true;
        }
        if (id == CommandID.DuplicateLayer || id == CommandID.DeleteLayer || id == CommandID.GroupLayers || id == CommandID.LayerStyle)
            return layer is not null;
        if (id == CommandID.BringLayerForward || id == CommandID.BringLayerToFront)
        {
            if (path is null) return false;
            return path[^1] < State.ChildrenOf(path[..^1]).Length - 1;
        }
        if (id == CommandID.SendLayerBackward || id == CommandID.SendLayerToBack) return (path?[^1] ?? 0) > 0;
        if (id == CommandID.UngroupLayers) return layer?.IsGroup == true;
        if (id == CommandID.ToggleClipping)
        {
            if (path is null) return false;
            return layer?.IsClipped == true || path[^1] > 0;
        }
        if (id == CommandID.AddMaskRevealAll || id == CommandID.AddMaskHideAll) return layer is not null && layer.Mask is null;
        if (id == CommandID.ToggleMask || id == CommandID.DeleteMask || id == CommandID.EditMask) return layer?.Mask is not null;
        if (id == CommandID.ConvertToSmartObject) return (layer?.IsRaster == true && !layer.Tiles.IsEmpty) || layer?.IsText == true;
        if (id == CommandID.RasterizeLayer) return layer is not null && !layer.IsRaster && !layer.IsAdjustment;
        if (id == CommandID.Export || id == CommandID.ExportPNG) return !isExporting;
        if (id == CommandID.AutoEnhance || id == CommandID.AutoStraighten || id == CommandID.SuggestCrop || id == CommandID.AutoColor
            || id == CommandID.SelectPeople || id == CommandID.CopyImageText || id == CommandID.TextToLayers || id == CommandID.SelectSky
            || id == CommandID.StraightenDocument)
            return !IsUntouchedBlank;
        if (id == CommandID.ExportForWeb) return !isExporting;
        if (id == CommandID.DevelopRaw)
        {
#if GLIMMER_RAW
            return ActiveLayer?.Raw is not null;
#else
            return false;
#endif
        }
        if (id == CommandID.ImportReport)
        {
#if GLIMMER_PSD
            return ImportReport is not null;
#else
            return false;
#endif
        }
        if (id == CommandID.Deselect || id == CommandID.InvertSelection || id == CommandID.AiRemoveObject || id == CommandID.FeatherSelection
            || id == CommandID.GrowSelection || id == CommandID.ShrinkSelection || id == CommandID.HealSelection)
            return State.Selection is not null;
        // Without a selection they take the whole layer.
        if (id == CommandID.Copy || id == CommandID.Cut) return ActiveLayer?.IsRaster ?? false;
        if (id == CommandID.ClearPixels) return State.Selection is not null && (ActiveLayer?.IsRaster ?? false);
        if (id == CommandID.FillForeground || id == CommandID.FillBackground || id == CommandID.FillPattern) return ActiveLayer?.IsRaster ?? false;
        if (id == CommandID.LayerViaCopy) return ActiveLayer?.IsRaster ?? false;
        if (id == CommandID.FreeTransform)
            return ActiveLayer is { } a && (a.IsRaster || a.IsText || a.IsGroup || a.VectorMask is not null || a.Content is SmartContent);
        if (id == CommandID.ZoomIn || id == CommandID.ZoomOut || id == CommandID.FitToWindow || id == CommandID.ActualSize
            || id == CommandID.RotateViewLeft || id == CommandID.RotateViewRight || id == CommandID.ResetViewRotation)
            return Canvas.CanPerform(id);
        // The Mac's system menu items.
        if (id == CommandID.Undo) return Document.CanUndo || liveEdit is not null;
        if (id == CommandID.Redo) return Document.CanRedo;
        if (id == WindowsCommands.Paste) return CanPaste;
        if (id == WindowsCommands.SaveGlimmerCopy) return linkedImage is not null || brokenLink is not null;
        if (id == WindowsCommands.OpenPackageFolder) return Environment.Opener is not null;
        return true;
    }

    public bool IsChecked(CommandID id)
    {
        if (id == CommandID.ToggleGammaBlending) return State.BlendSpace == BlendSpace.Gamma;
        if (id == CommandID.ToggleClipping) return ActiveLayer?.IsClipped == true;
        if (id == CommandID.ToggleRulers) return State.Aids.RulersVisible;
        if (id == CommandID.ToggleGrid) return State.Aids.Grid.IsVisible;
        if (id == CommandID.ToggleSnap) return State.Aids.Snap;
        if (id == CommandID.ToggleNavigator) return showNavigator;
        if (id == CommandID.ToggleClassicLayout) return classicLayout;
        if (id == CommandID.HideInterface) return interfaceHidden;
        if (id == CommandID.ToggleMask) return ActiveLayer?.Mask?.IsEnabled == false;
        if (id == CommandID.EditMask) return editingMask;
        return false;
    }

    /// <summary>A menu title that depends on the document (the Mac builds these in GlimmerCommands): "Undo {0}" with the
    /// step's name, "Save Back to {0}" while linked. Null: the descriptor's title.</summary>
    public UiText? Title(CommandID id)
    {
        if (id == CommandID.Undo && Document.HistoryIndex > 0 && Document.HistoryIndex <= Document.History.Count)
            return new UiText("Undo {0}", Document.History[Document.HistoryIndex - 1]);
        if (id == CommandID.Redo && Document.HistoryIndex < Document.History.Count)
            return new UiText("Redo {0}", Document.History[Document.HistoryIndex]);
        if (id == WindowsCommands.Save && (linkedImage?.FileName ?? brokenLink?.Link.FileName) is { } name)
            return new UiText("Save Back to {0}", name);
        return null;
    }

    public void Perform(CommandID id)
    {
        if (!CanPerform(id)) return;
        if (id.AdjustmentKind is { } kind)
        {
            AddLayer(new Layer(Environment.LayerName(kind.Title()), new AdjustmentContent(new Adjustment(kind))),
                     new UiText("New {0}", new UiText(kind.Title())));
            return;
        }
        if (id.FilterKind is { } filter)
        {
            OpenFilter(filter);
            return;
        }
        if (commands.Value.TryGetValue(id, out var action)) action(this);
        else Canvas.Perform(id);
    }

    /// <summary>What each command does (the Mac's switch in perform(_:)).</summary>
    private static readonly Lazy<Dictionary<CommandID, Action<EditorSession>>> commands = new(() => new()
    {
        [CommandID.CommandPalette] = s => s.IsPaletteOpen = !s.IsPaletteOpen,
        [CommandID.ToggleClassicLayout] = s =>
        {
            s.ClassicLayout = !s.ClassicLayout;
            // The canvas just changed width: fit the image to it again.
            s.Scheduler.Schedule(TimeSpan.FromMilliseconds(300), () => s.Canvas.Perform(CommandID.FitToWindow));
        },
        [CommandID.ToggleNavigator] = s => s.ShowNavigator = !s.ShowNavigator,
        [CommandID.HideInterface] = s => s.InterfaceHidden = !s.InterfaceHidden,
        [CommandID.FillForeground] = s => s.Fill(s.foreground, s.foregroundOpacity, "Fill"),
        [CommandID.FillBackground] = s => s.Fill(s.background, s.backgroundOpacity, "Fill"),
        [CommandID.FillPattern] = s => s.FillWithPattern(),
        [CommandID.ExportWebProject] = s => s.ExportWebProject(),
        [CommandID.ExportPNG] = s => s.Export(new ExportOptions(ExportFormat.Png)),
        [CommandID.AutoEnhance] = s => s.AI.AutoEnhance(),
        [CommandID.AutoStraighten] = s => s.AI.AutoStraighten(),
        [CommandID.SuggestCrop] = s => s.AI.SuggestCrop(),
        [CommandID.BatchProcess] = s => s.IsBatchSheetPresented = true,
        [CommandID.AutoColor] = s => s.AI.AutoColor(),
        [CommandID.SelectPeople] = s => s.AI.SelectPeople(),
        [CommandID.CopyImageText] = s => s.AI.CopyImageText(),
        [CommandID.TextToLayers] = s => s.AI.TextToLayers(),
        [CommandID.SelectSky] = s => s.AI.SelectSky(),
        [CommandID.StraightenDocument] = s => s.AI.StraightenDocument(),
        [CommandID.ExportForWeb] = s =>
        {
            s.ExportOptions = ExportOptions.Web with { ColorSpace = WorkingColorSpace.SRGB };
            s.IsExportSheetPresented = true;
        },
        [CommandID.NewDocument] = s => s.Environment.Host.ShowNewDocumentWindow(),
        [CommandID.OpenDocument] = s => s.Environment.Opener?.ShowOpenPanel(),
        [CommandID.ShowLayers] = s => s.ShowInspector(InspectorTab.Layers),
        [CommandID.ShowPaths] = s => s.ShowInspector(InspectorTab.Paths),
        [CommandID.ShowChannels] = s => s.ShowInspector(InspectorTab.Channels),
        [CommandID.ShowHistory] = s => s.ShowInspector(InspectorTab.History),
        [CommandID.RepeatFilter] = s => { if (s.lastFilter is { } f) s.ApplyFilter(f); },
        [CommandID.Liquify] = s => s.BeginLiquify(),
        [CommandID.NewLayer] = s =>
            s.AddLayer(new Layer(s.State.NextLayerName(s.Environment.LayerName("Layer")), new RasterContent(TileGrid.Empty)), "New Layer"),
        [CommandID.NewGroup] = s =>
            s.AddLayer(new Layer(s.State.NextLayerName(s.Environment.LayerName("Group")), new GroupContent(new LayerGroup()),
                                 blendMode: BlendMode.PassThrough), "New Group"),
        [CommandID.NewSolidFill] = s =>
            s.AddLayer(new Layer(s.Environment.LayerName("Color Fill"), new FillContent(new Fill.Solid(new SRGBColor(0.2, 0.45, 0.9)))), "New Fill Layer"),
        [CommandID.NewGradientFill] = s =>
        {
            double w = s.State.Width, h = s.State.Height;
            s.AddLayer(new Layer(s.Environment.LayerName("Gradient Fill"),
                                 new FillContent(new Fill.Gradient(new GradientFill(new Vec2(0, h / 2), new Vec2(w, h / 2))))), "New Fill Layer");
        },
        [CommandID.NewPatternFill] = s =>
            s.AddLayer(new Layer(s.Environment.LayerName("Pattern Fill"), new FillContent(new Fill.Pattern(new PatternFill()))), "New Fill Layer"),
        [CommandID.DuplicateLayer] = s => s.Edit("Duplicate Layer", st => st.DuplicateActiveLayer().State),
        [CommandID.DeleteLayer] = s => s.Edit("Delete Layer", st => st.DeleteActiveLayer()),
        [CommandID.BringLayerForward] = s => s.Edit("Bring Forward", st => st.MoveActiveLayer(1).State),
        [CommandID.SendLayerBackward] = s => s.Edit("Send Backward", st => st.MoveActiveLayer(-1).State),
        [CommandID.BringLayerToFront] = s => s.Edit("Bring to Front", st => st.MoveActiveLayer(int.MaxValue / 2).State),
        [CommandID.SendLayerToBack] = s => s.Edit("Send to Back", st => st.MoveActiveLayer(-int.MaxValue / 2).State),
        [CommandID.GroupLayers] = s => s.Edit("Group Layer", st => st.GroupActiveLayer(st.NextLayerName(s.Environment.LayerName("Group"))).State),
        [CommandID.UngroupLayers] = s => s.Edit("Ungroup", st => st.UngroupActiveLayer()),
        [CommandID.ToggleClipping] = s =>
        {
            bool clipped = s.ActiveLayer?.IsClipped == true;
            s.EditActive(clipped ? "Release Clipping Mask" : "Create Clipping Mask", l => l with { IsClipped = !clipped });
        },
        [CommandID.AddMaskRevealAll] = s =>
        {
            if (s.State.Selection is { } selection && s.State.ActiveLayerID is { } id)
                s.Edit("Add Layer Mask", st => st.UpdateLayer(id, l => l with { Mask = selection.AsMask }) with { Selection = null });
            else
                s.EditActive("Add Layer Mask", l => l with { Mask = LayerMask.RevealAll });
        },
        [CommandID.AddMaskHideAll] = s => s.EditActive("Add Layer Mask", l => l with { Mask = LayerMask.HideAll }),
        [CommandID.ToggleMask] = s =>
        {
            bool enabled = s.ActiveLayer?.Mask?.IsEnabled == true;
            s.EditActive(enabled ? "Disable Layer Mask" : "Enable Layer Mask", l => l.Mask is { } m ? l with { Mask = m with { IsEnabled = !enabled } } : l);
        },
        [CommandID.DeleteMask] = s => s.EditActive("Delete Layer Mask", l => l with { Mask = null, VectorMask = null }),
        [CommandID.EditMask] = s => s.EditingMask = !s.EditingMask,
        [CommandID.ConvertToSmartObject] = s => s.ConvertToSmartObject(),
        [CommandID.RasterizeLayer] = s => s.RasterizeActiveLayer(),
        [CommandID.LayerStyle] = s => s.IsLayerStylePresented = true,
        [CommandID.ToggleGammaBlending] = s =>
        {
            bool gamma = s.State.BlendSpace == BlendSpace.Gamma;
            s.Edit(gamma ? "Blend in Linear Light" : "Blend in Gamma Space", st => st with { BlendSpace = gamma ? BlendSpace.Linear : BlendSpace.Gamma });
        },
        [CommandID.ToggleLayersPanel] = s => s.ShowLayersPanel = !s.ShowLayersPanel,
        [CommandID.SelectAll] = s => s.Edit("Select All", st => st with { Selection = Selection.All() }),
        [CommandID.Deselect] = s => s.Edit("Deselect", st => st with { Selection = null }),
        [CommandID.InvertSelection] = s => s.Edit("Select Inverse", st => st with { Selection = st.Selection?.Inverted() }),
        [CommandID.SelectSubject] = s => s.AI.SelectSubject(),
        [CommandID.FeatherSelection] = s => s.SelectionDialog = CommandID.FeatherSelection,
        [CommandID.GrowSelection] = s => s.SelectionDialog = CommandID.GrowSelection,
        [CommandID.ShrinkSelection] = s => s.SelectionDialog = CommandID.ShrinkSelection,
        [CommandID.Copy] = s => s.CopySelection(cut: false),
        [CommandID.Cut] = s => s.CopySelection(cut: true),
        [CommandID.ClearPixels] = s => s.ClearSelectedPixels(),
        [CommandID.LayerViaCopy] = s => s.LayerViaCopy(),
        [CommandID.FreeTransform] = s => s.Tool = ToolID.FreeTransform,
        [CommandID.FlipHorizontal] = s => s.FlipCanvas(CanvasFlip.Horizontal),
        [CommandID.FlipVertical] = s => s.FlipCanvas(CanvasFlip.Vertical),
        [CommandID.RotateRight] = s => s.FlipCanvas(CanvasFlip.RotateRight),
        [CommandID.RotateLeft] = s => s.FlipCanvas(CanvasFlip.RotateLeft),
        [CommandID.Rotate180] = s => s.FlipCanvas(CanvasFlip.Rotate180),
        [CommandID.Crop] = s => s.Tool = ToolID.Crop,
        [CommandID.ImageSize] = s => s.IsImageSizePresented = true,
        [CommandID.CanvasSize] = s => s.IsCanvasSizePresented = true,
        [CommandID.ImportReport] = s =>
        {
#if GLIMMER_PSD
            s.IsImportReportPresented = s.ImportReport is not null;
#endif
        },
        [CommandID.DevelopRaw] = s =>
        {
#if GLIMMER_RAW
            s.IsRawDevelopPresented = s.ActiveLayer?.Raw is not null;
#endif
        },
        [CommandID.ToggleRulers] = s => s.Edit(s.State.Aids.RulersVisible ? "Hide Rulers" : "Show Rulers",
                                                st => st with { Aids = st.Aids with { RulersVisible = !st.Aids.RulersVisible } }),
        [CommandID.ToggleGrid] = s => s.Edit(s.State.Aids.Grid.IsVisible ? "Hide Grid" : "Show Grid",
                                              st => st with { Aids = st.Aids with { Grid = st.Aids.Grid with { IsVisible = !st.Aids.Grid.IsVisible } } }),
        [CommandID.ToggleSnap] = s => s.Edit("Snap", st => st with { Aids = st.Aids with { Snap = !st.Aids.Snap } }),
        [CommandID.ClearGuides] = s => s.Edit("Clear Guides", st => st with { Aids = st.Aids with { Guides = [] } }),
        [CommandID.AiRemoveBackground] = s => s.AI.RemoveBackground(),
        [CommandID.AiRemoveObject] = s => s.AI.Fill(AIPipelines.InpaintMode.Remove, "", null),
#if GLIMMER_HEAL
        [CommandID.HealSelection] = s => s.AI.HealSelection(),
#endif
        [CommandID.ShowAssistant] = s => s.ShowAI(AISection.Assistant),
        [CommandID.AiGenerate] = s => s.ShowAI(AISection.Generate),
        [CommandID.AiEditImage] = s => s.ShowAI(AISection.Assistant),
        [CommandID.AiGenerativeFill] = s => s.ShowAI(AISection.Fill),
        [CommandID.AiExpand] = s => s.ShowAI(AISection.Expand),
        [CommandID.AiColorMatch] = s => s.ShowAI(AISection.ColorMatch),
        [CommandID.AiRelight] = s => s.ShowAI(AISection.Relight),
        [CommandID.AiReflections] = s => s.ShowAI(AISection.Reflections),
        [CommandID.AiHarmonize] = s => s.ShowAI(AISection.Harmonize),
        [CommandID.AiUpscale] = s => s.ShowAI(AISection.Upscale),
        [CommandID.AiDistractions] = s => s.ShowAI(AISection.Cleanup),
        [CommandID.AiPresets] = s => s.ShowAI(AISection.Presets),
        [CommandID.AiLensBlur] = s => s.ShowAI(AISection.LensBlur),
        [CommandID.PlaceImage] = s => s.ChoosePlaceImage(),
        [CommandID.Export] = s => s.IsExportSheetPresented = true,
        // What the Mac's system menus send.
        [CommandID.Undo] = s => { s.CommitLiveEdit(); s.Document.Undo(); },
        [CommandID.Redo] = s => { s.CommitLiveEdit(); s.Document.Redo(); },
        [WindowsCommands.Paste] = s => s.Paste(),
        [WindowsCommands.Save] = s => s.SaveCommand(),
        [WindowsCommands.SaveAs] = s => s.SaveAsCommand(),
        [WindowsCommands.SaveCopyForMac] = s => s.SaveCopyForMac(),
        [WindowsCommands.SaveGlimmerCopy] = s => s.SaveGlimmerCopy(),
        [WindowsCommands.Close] = s => { if (s.TryClose()) s.Environment.Host.CloseDocument(s); },
        [WindowsCommands.LinkedFilesWaiting] = s => s.Environment.Host.ShowLinkedWaiting(),
        [WindowsCommands.OpenPackageFolder] = s => s.Environment.Opener?.OpenPackageFolder(),
    });

    private void ShowInspector(InspectorTab tab)
    {
        ShowLayersPanel = true;
        InspectorTab = tab;
    }

    public void ShowAI(AISection section)
    {
        ShowLayersPanel = true;
        InspectorTab = InspectorTab.AI;
        AISection = section;
    }
}
