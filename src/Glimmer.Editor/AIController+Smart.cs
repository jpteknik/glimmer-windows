// Ported from App/AIController+Smart.swift
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Tools;

namespace Glimmer.Editor;

/// <summary>One-click corrections worked out on this computer itself: free, private and instant, unlike the OpenRouter
/// jobs.</summary>
public sealed partial class AIController
{
    private SmartAnalysis.Insights? insights;
    private int? insightsKey;
    private int insightsGeneration;

    /// <summary>On-device analysis of the open image.</summary>
    internal SmartAnalysis.Insights? Insights
    {
        get => insights;
        private set
        {
            insights = value;
            Raise(nameof(SmartSuggestions), nameof(HorizonDegrees));
        }
    }

    public double? HorizonDegrees => insights?.HorizonDegrees;

    /// <summary>The analysis of the current image, computed when the image has changed since the last one. Flattening and
    /// analysis run off the UI thread (like the navigator), so this never stalls the canvas, even on huge documents.</summary>
    internal async Task<SmartAnalysis.Insights> CurrentInsightsAsync()
    {
        int key = DocumentKey(session.State);
        if (key == insightsKey && insights is { } known) return known;
        var snapshot = session.State;
        var found = await Detached(() => SmartAnalysis.InsightsAsync(Composite(snapshot)));
        insightsKey = key;
        Insights = found;
        return found;
    }

    /// <summary>Runs <paramref name="then"/> with the current analysis; errors are reported.</summary>
    internal void WithInsights(Action<SmartAnalysis.Insights> then)
    {
        _ = Run();

        async Task Run()
        {
            try { then(await CurrentInsightsAsync()); }
            catch (Exception error) { session.Report(error); }
        }
    }

    /// <summary>Image analysis for the omnibar, without side effects.</summary>
    public void RefreshInsights()
    {
        if (session.IsUntouchedBlank) return;
        int generation = ++insightsGeneration;
        _ = Run();

        async Task Run()
        {
            // A newer refresh supersedes this one (the original cancelled the task it replaced).
            try { if (generation == insightsGeneration) await CurrentInsightsAsync(); }
            catch (Exception) { }
        }
    }

    public void AutoEnhance()
    {
        _ = Run();

        async Task Run()
        {
            try
            {
                if (!await PerformAutoEnhanceAsync())
                    session.ShowToast("The image already looks balanced. Auto Enhance found nothing to change.");
            }
            catch (Exception error)
            {
                session.Report(error);
            }
        }
    }

    /// <summary>Auto Enhance: automatic tone and colour corrections as an editable group of adjustment layers at the top of
    /// the stack. Returns false when the image needs nothing.</summary>
    internal async Task<bool> PerformAutoEnhanceAsync()
    {
        var recipe = (await CurrentInsightsAsync()).Enhance;
        var adjustments = recipe.Adjustments;
        if (!recipe.IsNoticeable || adjustments.Count == 0) return false;
        var children = adjustments.Select(a => new Layer(LayerName(a.Kind.Title()), new AdjustmentContent(a)));
        var group = new Layer(LayerName("Auto Enhance"), new GroupContent(new LayerGroup(children, isExpanded: false)), blendMode: BlendMode.PassThrough);
        session.Document.Edit("Auto Enhance", s => (s with { ActiveLayerID = s.Layers.LastOrDefault()?.Id }).InsertLayerAboveActive(group));
        return true;
    }

    /// <summary>Auto Straighten: finds a tilted horizon and opens Crop with the box rotated to level it, so the result can be
    /// checked before it's applied.</summary>
    public void AutoStraighten()
    {
        WithInsights(found =>
        {
            if (found.HorizonDegrees is not { } tilt)
            {
                session.ShowToast("Glimmer found no tilted horizon to straighten.");
                return;
            }
            if (session.Tool != ToolID.Crop) session.Tool = ToolID.Crop;
            else session.BeginCrop();
            // A horizon rising to the right needs the box turned the same way (counterclockwise on screen).
            session.SetCropAngle(-tilt);
        });
    }

    /// <summary>Suggest Crop: frames the salient part of the image at the crop's aspect.</summary>
    public void SuggestCrop()
    {
        WithInsights(found =>
        {
            var s = session;
            if (s.Tool != ToolID.Crop) s.Tool = ToolID.Crop;
            if (s.CropSession is not { } c) return;
            if (SmartAnalysis.SuggestedCrop(found.SalientRect ?? default, s.State.Width, s.State.Height, c.Aspect, found.FaceRects) is not { } r)
            {
                s.ShowToast("The whole image already frames the subject well.");
                return;
            }
            s.CropSessionValue = c with { Angle = 0, BaseSize = null, Pivot = default, Rect = new CGRectLike(r.MinX, r.MinY, r.MaxX, r.MaxY) };
        });
    }

    public void AutoColor()
    {
        _ = Run();

        async Task Run()
        {
            try
            {
                if (!await PerformAutoColorAsync()) session.ShowToast("No color cast to correct.");
            }
            catch (Exception error)
            {
                session.Report(error);
            }
        }
    }

    /// <summary>Auto Color: neutralises a colour cast with a Color Balance layer. Returns false when there is no cast worth
    /// correcting.</summary>
    internal async Task<bool> PerformAutoColorAsync()
    {
        if ((await CurrentInsightsAsync()).Enhance.ColorCast is not { } cast || !SmartAnalysis.IsNoticeableCast(cast, threshold: 0.012)) return false;
        session.Document.Edit("Auto Color", s => (s with { ActiveLayerID = s.Layers.LastOrDefault()?.Id })
            .InsertLayerAboveActive(new Layer(LayerName("Auto Color"), new AdjustmentContent(SmartAnalysis.CastCorrection(cast)))));
        return true;
    }

    public void SelectPeople()
    {
        // The Mac does this with Vision; here it needs two models, asked about before anything runs.
        if (!HasModels("select_people", SelectPeople)) return;
        _ = Run();

        async Task Run()
        {
            try
            {
                await PerformSelectPeopleAsync();
            }
            catch (SegmentationError e) when (e.Equals(SegmentationError.NoSubject()))
            {
                session.ShowToast("No people found in the image.");
            }
            catch (Exception error)
            {
                session.Report(error);
            }
        }
    }

    /// <summary>Select People: person segmentation of the whole image.</summary>
    internal async Task PerformSelectPeopleAsync()
    {
        var snapshot = session.State;
        var selection = await Detached(async () =>
        {
            var mask = await SubjectSegmentation.PeopleMaskAsync(Composite(snapshot)).ConfigureAwait(false);
            return Selection.From(Unit(mask), snapshot.Bounds);
        });
        session.Document.Edit("Select People", s => s with { Selection = selection });
    }

    public void SelectSky()
    {
        if (ModelStore.Shared.Missing(SkyModels).Count > 0)
        {
            WithModels(SkyModels, SelectSky);
            return;
        }
        _ = Run();

        async Task Run()
        {
            try
            {
                if (!await PerformSelectSkyAsync()) session.ShowToast("Glimmer found no sky along the top of the image.");
            }
            catch (Exception error)
            {
                session.Report(error);
            }
        }
    }

    /// <summary>What Select Sky needs downloaded (the register's select_sky entries).</summary>
    internal static IReadOnlyList<OnDeviceModel> SkyModels => ModelsFor("select_sky");

    /// <summary>Select Sky: the depth map and colour continuity find the sky reaching the top edge; Segment Anything then
    /// draws its exact outline (ridges, cables, branches). The image encoding is shared with Magic Select.</summary>
    internal async Task<bool> PerformSelectSkyAsync()
    {
        var snapshot = session.State;
        int key = DocumentKey(snapshot);
        var magic = selector;
        IsSegmenting = true;
        try
        {
            var selection = await Detached(async () =>
            {
                var image = Composite(snapshot);
                // Depth and prompts on a small copy (both models work far below 4K anyway).
                var small = image.Limited(1024);
                var depth = await DepthEstimator.DepthAsync(small).ConfigureAwait(false);
                double k = image.Width / (double)small.Width;
                if (SkySelection.Prompts(small, depth) is not { } prompts) return null;
                var points = prompts.Select(p => new MagicSelector.Point(p.Location * k, p.Positive)).ToList();
                await magic.PrepareAsync(image, key).ConfigureAwait(false);
                var mask = await magic.SegmentAsync(points).ConfigureAwait(false);
                return Selection.From(Unit(mask), snapshot.Bounds);
            });
            if (selection is null) return false;
            session.Document.Edit("Select Sky", s => s with { Selection = selection });
            return true;
        }
        finally
        {
            IsSegmenting = false;
        }
    }

    /// <summary>Straighten Document: finds a document, sign, whiteboard or screen and opens it flattened to a rectangle as a
    /// new document (this one is left alone).</summary>
    public void StraightenDocument()
    {
        var snapshot = session.State;
        _ = Run();

        async Task Run()
        {
            LinearImage? image;
            try
            {
                image = await Detached(() =>
                {
                    var full = LinearComposite(snapshot);
                    var probe = Composite(snapshot);
                    return DocumentDetection.QuadIn(probe) is { } quad ? DocumentDetection.Corrected(full, quad) : null;
                });
            }
            catch (Exception error)
            {
                session.Report(error);
                return;
            }
            if (image is null)
            {
                session.ShowToast("Glimmer found no document, sign or screen to straighten.");
                return;
            }
            try
            {
                var tiles = TileGrid.Empty.WriteRegion(new PixelRect(0, 0, image.Width, image.Height), image.Pixels);
                var layer = new Layer(LayerName("Straightened"), new RasterContent(tiles));
                CreateDocument(new DocumentState(image.Width, image.Height, layers: [layer]));
            }
            catch (Exception error)
            {
                session.Report(error);
            }
        }
    }

    /// <summary>Reads the text in the image (the text recognizer the app gives OnDeviceServices: Windows' OCR).</summary>
    private void RecognizeText(Action<IReadOnlyList<ImageText.Line>, RGBAImage> then)
    {
        var snapshot = session.State;
        _ = Run();

        async Task Run()
        {
            IReadOnlyList<ImageText.Line> lines;
            RGBAImage image;
            try
            {
                (lines, image) = await Detached(async () =>
                {
                    var picture = Composite(snapshot);
                    return (await ImageText.LinesAsync(picture).ConfigureAwait(false), picture);
                });
            }
            catch (Exception error)
            {
                // Without a recognizer: TextRecognitionUnavailableError, whose words say what to install.
                session.Report(error);
                return;
            }
            if (lines.Count == 0) session.ShowToast("Glimmer found no text in the image.");
            else then(lines, image);
        }
    }

    public void CopyImageText()
    {
        RecognizeText((lines, _) =>
        {
            session.Environment.Clipboard.SetText(ImageText.PlainText(lines));
            session.ShowToast(new UiText("Copied {0} lines of text.", lines.Count));
        });
    }

    /// <summary>Image Text to Layers: every line of text becomes an editable text layer at its place, sized to cover it and
    /// coloured like it, in one group.</summary>
    public void TextToLayers()
    {
        RecognizeText((lines, image) =>
        {
            var layers = lines.Select(l => ImageText.TextLayer(l, image)).ToList();
            var group = new Layer(LayerName("Text from Image"), new GroupContent(new LayerGroup(layers, isExpanded: true)), blendMode: BlendMode.PassThrough);
            session.Document.Edit("Image Text to Layers", s => (s with { ActiveLayerID = s.Layers.LastOrDefault()?.Id }).InsertLayerAboveActive(group));
            session.ShowToast(new UiText("{0} text layers added. Paint out or remove the original text underneath to replace it.", lines.Count));
        });
    }

    /// <summary>Commands that suit this image, best first, for the omnibar.</summary>
    public IReadOnlyList<CommandID> SmartSuggestions
    {
        get
        {
            if (insights is not { } found || insightsKey != DocumentKey(session.State)) return [];
            var ids = new List<CommandID>();
            if (found.HorizonDegrees is not null) ids.Add(CommandID.AutoStraighten);
            if (found.Enhance.IsNoticeable && (found.Tone.IsFlat || found.Tone.IsDark || found.Tone.IsBright || found.Tone.IsMuted))
                ids.Add(CommandID.AutoEnhance);
            if (!found.Enhance.IsNoticeable && SmartAnalysis.IsNoticeableCast(found.Enhance.ColorCast)) ids.Add(CommandID.AutoColor);
            if (found.FaceCount > 0)
            {
                ids.Add(CommandID.SelectPeople);
                ids.Add(CommandID.AiLensBlur);
            }
            if (found.HasText) ids.Add(CommandID.TextToLayers);
            if (SmartAnalysis.SuggestedCrop(found.SalientRect ?? default, session.State.Width, session.State.Height, null, found.FaceRects) is not null)
                ids.Add(CommandID.SuggestCrop);
            if (found.Enhance.IsNoticeable && !ids.Contains(CommandID.AutoEnhance)) ids.Add(CommandID.AutoEnhance);
            return [.. ids.Take(4)];
        }
    }
}
