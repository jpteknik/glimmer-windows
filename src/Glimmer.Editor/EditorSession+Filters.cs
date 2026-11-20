// Ported from App/EditorSession+Filters.swift
using System.Collections.Immutable;
using Glimmer.Core;
using Glimmer.Render;

namespace Glimmer.Editor;

public sealed partial class EditorSession
{
    private FilterEngine? FilterEngine => filterEngineCache ??= new FilterEngine();

    // MARK: Filters

    private (Layer Layer, SmartObject Smart)? ActiveSmart =>
        ActiveLayer is { Content: SmartContent s } layer ? (layer, s.Smart) : null;

    /// <summary>Opens the filter dialog (filters without settings run immediately). On a smart object the filter becomes a
    /// smart filter.</summary>
    public void OpenFilter(FilterKind kind)
    {
        CommitLiveEdit();
        smartFilterEditIndex = null;
        var settings = lastFilter?.Kind == kind ? lastFilter : new FilterSettings(kind);
        if (kind.Fields().IsEmpty)
        {
            ApplyFilter(settings);
        }
        else
        {
            FilterDialog = settings;
            PreviewFilter(settings);
        }
    }

    /// <summary>Re-opens a smart filter's settings.</summary>
    public void EditSmartFilter(Guid layerId, int index)
    {
        CommitLiveEdit();
        if (State.LayerById(layerId) is not { Content: SmartContent s } || index < 0 || index >= s.Smart.Filters.Length) return;
        SetActiveLayer(layerId);
        smartFilterEditIndex = index;
        FilterDialog = s.Smart.Filters[index].Settings;
        PreviewFilter(s.Smart.Filters[index].Settings);
    }

    /// <summary>Live preview on the active layer (debounced, computed off the main thread).</summary>
    public void PreviewFilter(FilterSettings settings)
    {
        filterPreviewTask?.Dispose();
        int generation = ++filterPreviewGeneration;
        if (ActiveSmart is { } active)
        {
            // Smart objects preview by temporarily changing their filter list.
            smartFilterPreviewBase ??= State;
            var filters = active.Smart.Filters;
            if (smartFilterEditIndex is { } i && i >= 0 && i < filters.Length)
                filters = filters.SetItem(i, filters[i] with { Settings = settings });
            else if (smartFilterPreviewBase?.LayerById(active.Layer.Id)?.Content is SmartContent original)
                filters = original.Smart.Filters.Add(new SmartFilter(settings));
            Document.State = State.UpdateLayer(active.Layer.Id, l => l.Content is SmartContent sc
                ? l with { Content = new SmartContent(sc.Smart with { Filters = filters }) }
                : l);
            return;
        }
        if (FilterEngine is not { } engine || ActiveLayer is not { IsRaster: true } layer) return;
        var tiles = layer.Tiles;
        var frame = State.Bounds;
        var selection = State.Selection;
        var id = layer.Id;
        IsPreviewingFilter = true;
        filterPreviewTask = Scheduler.Schedule(TimeSpan.FromMilliseconds(60), () =>
            Scheduler.Background(() => engine.Apply(settings, tiles, frame, selection), result =>
            {
                Overrides = new LayerOverrides(pixels: [new(id, result)]);
                IsPreviewingFilter = false;
            }, () => generation != filterPreviewGeneration, _ => IsPreviewingFilter = false));
    }

    /// <summary>Preview switched off in the dialog: stop the preview and clear the canvas overrides.</summary>
    public void StopFilterPreview()
    {
        filterPreviewTask?.Dispose();
        filterPreviewGeneration++;
        IsPreviewingFilter = false;
        Overrides = LayerOverrides.None;
        if (smartFilterPreviewBase is { } basis) Document.State = basis;
        smartFilterPreviewBase = null;
    }

    public void CancelFilterDialog()
    {
        filterPreviewTask?.Dispose();
        filterPreviewGeneration++;
        FilterDialog = null;
        IsPreviewingFilter = false;
        Overrides = LayerOverrides.None;
        if (smartFilterPreviewBase is { } basis)
        {
            Document.State = basis;
            smartFilterPreviewBase = null;
        }
        smartFilterEditIndex = null;
    }

    public void ApplyFilter(FilterSettings settings)
    {
        filterPreviewTask?.Dispose();
        filterPreviewGeneration++;
        FilterDialog = null;
        IsPreviewingFilter = false;
        if (ActiveSmart is { } active)
        {
            var before = smartFilterPreviewBase ?? State;
            smartFilterPreviewBase = null;
            var filters = before.LayerById(active.Layer.Id)?.Content is SmartContent original ? original.Smart.Filters : active.Smart.Filters;
            if (smartFilterEditIndex is { } i && i >= 0 && i < filters.Length) filters = filters.SetItem(i, filters[i] with { Settings = settings });
            else filters = filters.Add(new SmartFilter(settings));
            smartFilterEditIndex = null;
            Document.State = before;
            LastFilter = settings;
            Edit("Smart Filter", s => s.UpdateLayer(active.Layer.Id, l => l.Content is SmartContent sc
                ? l with { Content = new SmartContent(sc.Smart with { Filters = filters }) }
                : l));
            return;
        }
        if (FilterEngine is not { } engine || ActiveLayer is not { IsRaster: true } layer)
        {
            Overrides = LayerOverrides.None;
            ErrorMessage = "Filters work on pixel layers and smart objects. Rasterize the layer first.";
            return;
        }
        try
        {
            var result = engine.Apply(settings, layer.Tiles, State.Bounds, State.Selection);
            LastFilter = settings;
            Edit(new UiText(settings.Kind.Title()), s => s.UpdateLayer(layer.Id, l => l with { Tiles = result }));
        }
        catch (Exception error)
        {
            Report(error);
        }
        Overrides = LayerOverrides.None;
    }

    public void SetSmartFilterEnabled(Guid layerId, int index, bool enabled) =>
        Edit(enabled ? "Show Smart Filter" : "Hide Smart Filter", s => s.UpdateLayer(layerId, l =>
            l.Content is SmartContent sc && index >= 0 && index < sc.Smart.Filters.Length
                ? l with { Content = new SmartContent(sc.Smart with { Filters = sc.Smart.Filters.SetItem(index, sc.Smart.Filters[index] with { IsEnabled = enabled }) }) }
                : l));

    public void DeleteSmartFilter(Guid layerId, int index) =>
        Edit("Delete Smart Filter", s => s.UpdateLayer(layerId, l =>
            l.Content is SmartContent sc && index >= 0 && index < sc.Smart.Filters.Length
                ? l with { Content = new SmartContent(sc.Smart with { Filters = sc.Smart.Filters.RemoveAt(index) }) }
                : l));

    /// <summary>Preset filter steps (web ids) run on the active pixel layer, as one undo step.</summary>
    public bool ApplyFilterSteps(IReadOnlyList<(string Name, ImmutableDictionary<string, double> Parameters)> steps)
    {
        if (FilterEngine is not { } engine || ActiveLayer is not { IsRaster: true } layer) return false;
        var tiles = layer.Tiles;
        foreach (var (id, parameters) in steps)
        {
            if (FilterSettings.FromId(id, parameters) is not { } f) continue;
            try { tiles = engine.Apply(f, tiles, State.Bounds, State.Selection); }
            catch { return false; }
        }
        Edit("Preset Filters", s => s.UpdateLayer(layer.Id, l => l with { Tiles = tiles }));
        return true;
    }
}
