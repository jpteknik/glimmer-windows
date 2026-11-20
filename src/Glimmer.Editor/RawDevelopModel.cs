// Ported from App/RawDevelopSheet.swift (RawDraftWorker, RawDevelopModel, and the sheet's develop and cancel)
using System.Diagnostics;
using Glimmer.Core;
using Glimmer.Formats;
using SkiaSharp;

namespace Glimmer.Editor;

/// <summary>Draws drafts of a RAW file away from the UI thread, one at a time, always for the latest settings asked for:
/// settings asked for while a draft is being made replace each other, so a slider dragged quickly costs one draft, not
/// one per step. The decoder (RawDraft) is made on the first draft and kept.</summary>
internal sealed class RawDraftWorker(byte[] data, Action<RawDraftWorker.Result> deliver)
{
    public sealed record Result(SKBitmap? Image, RawSettings Settings, double Seconds, Exception? Error);

    private readonly Lock gate = new();
    private RawDraft? draft;          // touched by the running draw only
    private RawSettings? wanted;      // behind gate
    private bool busy, stopped;       // behind gate

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            wanted = null;
        }
    }

    public void Request(RawSettings settings)
    {
        bool start;
        lock (gate)
        {
            if (stopped) return;
            wanted = settings;
            start = !busy;
            busy = true;
        }
        if (start) _ = Task.Run(Run);
    }

    private void Run()
    {
        while (true)
        {
            RawSettings settings;
            lock (gate)
            {
                if (wanted is not { } next || stopped)
                {
                    busy = false;
                    return;
                }
                settings = next;
                wanted = null;
            }
            var began = Stopwatch.StartNew();
            Result result;
            try
            {
                draft ??= new RawDraft(data);
                result = new Result(draft.Picture(settings), settings, began.Elapsed.TotalSeconds, null);
            }
            catch (Exception error)
            {
                result = new Result(null, settings, began.Elapsed.TotalSeconds, error);
            }
            bool keep;
            lock (gate) keep = !stopped;
            if (keep) deliver(result);
            else result.Image?.Dispose();
        }
    }
}

/// <summary>What Layer › Develop… shows and changes (RawDevelopModel), with the sheet's develop and cancel.</summary>
public sealed class RawDevelopModel : ObservableObject, IRawDevelopModel
{
    /// <summary>How long the final development may take before it is given up (a 50-megapixel file took 3 to 15 s when
    /// measured on a Mac).</summary>
    public static readonly TimeSpan DevelopLimit = TimeSpan.FromSeconds(90);

    private readonly EditorSession session;
    private readonly RawSource source;
    private readonly Glimmer.Formats.RawInfo decoder;
    private RawDraftWorker? worker;
    private RawSettings settings;
    private RawSettings? shown;
    private RgbaImage? picture;
    private bool followsBehind, isDeveloping, isPickingNeutral;
    private string? problem;
    private int drafts;
    private Guid? developRun;

    /// <summary>Null when the layer has no RAW file, or the file can't be read.</summary>
    public static RawDevelopModel? Make(EditorSession session, Layer layer)
    {
        if (layer.Raw is not { } raw) return null;
        try
        {
            return new RawDevelopModel(session, layer, raw, RawDevelopment.Info(raw.Data));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private RawDevelopModel(EditorSession session, Layer layer, RawSource raw, Glimmer.Formats.RawInfo info)
    {
        this.session = session;
        LayerID = layer.Id;
        source = raw;
        decoder = info;
        Info = new RawInfo(info.Width, info.Height, info.Temperature, info.Tint, info.Contrast, info.Sharpness, info.LuminanceNoise, info.ColorNoise,
                           info.LensCorrection, info.SupportsContrast, info.SupportsSharpness, info.SupportsLuminanceNoise, info.SupportsColorNoise,
                           info.SupportsHighlightRecovery, info.SupportsLensCorrection);
        PixelsChanged = !RawLayer.PixelsAreAsDeveloped(layer);
        settings = raw.Settings;
        worker = NewWorker();
        // Started when the sheet opens, before any control is touched: the first render is the slow one.
        AskForDraft();
    }

    public Guid LayerID { get; }
    public string FileName => source.FileName;
    public long FileBytes => source.Data.Length;
    public RawInfo Info { get; }
    public bool PixelsChanged { get; }

    public RawSettings Settings
    {
        get => settings;
        set
        {
            if (settings == value) return;
            settings = value;
            Raise(nameof(Settings), nameof(Temperature), nameof(Tint), nameof(IsBehind));
            AskForDraft();
        }
    }

    public double Temperature
    {
        get => settings.Temperature ?? decoder.Temperature;
        set => Settings = settings with { Temperature = value };
    }

    public double Tint
    {
        get => settings.Tint ?? decoder.Tint;
        set => Settings = settings with { Tint = value };
    }

    public RgbaImage? Picture { get => picture; private set => Set(ref picture, value); }
    /// <summary>True while the picture on screen is not yet the one the controls say.</summary>
    public bool IsBehind => shown != settings;
    public bool FollowsBehind { get => followsBehind; private set => Set(ref followsBehind, value); }
    public string? Problem { get => problem; private set => Set(ref problem, value); }
    public bool IsDeveloping { get => isDeveloping; private set => Set(ref isDeveloping, value); }
    public bool IsPickingNeutral { get => isPickingNeutral; set => Set(ref isPickingNeutral, value); }

    /// <summary>Called when the sheet closes with the model open: the drafts stop.</summary>
    public event Action? Finished;

    private RawDraftWorker NewWorker() => new(source.Data, result => session.Environment.Scheduler.Post(() => Deliver(result)));

    private void Deliver(RawDraftWorker.Result made)
    {
        if (made.Error is { } error)
        {
            Problem = PersonMessage.Text(error).ToString();
            return;
        }
        using var image = made.Image!;
        Picture = Bgra(image);
        shown = made.Settings;
        Problem = null;
        Raise(nameof(IsBehind));
        // A draft is the real development; for a very large file one takes longer than a tenth of a second and the picture
        // follows a moment behind. Said once it has been seen, not guessed from the file's size. The first picture is not
        // counted.
        drafts++;
        if (drafts > 1 && made.Seconds > 0.1) FollowsBehind = true;
    }

    /// <summary>An 8-bit premultiplied BGRA copy for the view.</summary>
    private static RgbaImage Bgra(SKBitmap image)
    {
        using var converted = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using (var canvas = new SKCanvas(converted)) canvas.DrawBitmap(image, 0, 0);
        return new RgbaImage(converted.Width, converted.Height, converted.RowBytes, converted.Bytes);
    }

    private void AskForDraft() => worker?.Request(settings);

    /// <summary>The draft's decoder is let go while the final development runs, and made again if that is cancelled or fails.</summary>
    private void StopDrafts() => worker?.Stop();

    private void ResumeDrafts()
    {
        worker = NewWorker();
        AskForDraft();
    }

    /// <summary>The camera's own white balance and the decoder's own values; the process stays the one the picture was
    /// developed with.</summary>
    public void ResetToAsShot() => Settings = new RawSettings { Process = settings.Process };

    /// <summary>White balance from a point of the draft that should be grey (0…1 from the picture's top left).</summary>
    public void PickNeutral(double x, double y)
    {
        IsPickingNeutral = false;
        var data = source.Data;
        var current = settings;
        var point = new Vec2(x * Info.Width, y * Info.Height);
        (double Temperature, double Tint)? found = null;
        session.Environment.Scheduler.RunInBackground(() =>
        {
            try { found = RawDevelopment.WhiteBalance(data, current, point); }
            catch (Exception) { found = null; }
        }, () =>
        {
            if (found is not { } balance)
            {
                Problem = "Couldn't read a white balance from that point.";
                return;
            }
            Settings = settings with { Temperature = balance.Temperature, Tint = balance.Tint };
        });
    }

    /// <summary>Develops at full size and replaces the layer as one undo step ("Develop RAW"). True when done; false when it
    /// failed or was given up after the time limit (Problem says why, and nothing changed).</summary>
    public Task<bool> DevelopAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (session.State.LayerById(LayerID) is not { } layer)
        {
            done.SetResult(false);
            return done.Task;
        }
        var developWith = settings;
        var run = Guid.NewGuid();
        developRun = run;
        IsDeveloping = true;
        Problem = null;
        StopDrafts();   // one decoder at a time on this file
        var scheduler = session.Environment.Scheduler;
        // The limit: the sheet is never held by a development that does not come back. What it was doing is abandoned
        // (its result is thrown away if it ever arrives) and nothing is changed.
        scheduler.Schedule(DevelopLimit, () =>
        {
            if (developRun != run) return;
            developRun = null;
            IsDeveloping = false;
            Problem = "Developing took too long and was given up. Nothing was changed. You can try again.";
            ResumeDrafts();
            done.TrySetResult(false);
        });
        (Layer Layer, double DecoderSeconds, double TileSeconds)? made = null;
        Exception? failure = null;
        scheduler.RunInBackground(() =>
        {
            try { made = RawLayer.DevelopedTimed(layer, developWith); }
            catch (Exception e) { failure = e; }
        }, () =>
        {
            // Cancelled or given up meanwhile: the document stays as it was.
            if (developRun != run) return;
            developRun = null;
            if (made is { } result)
            {
                session.Edit("Develop RAW", state => state.UpdateLayer(LayerID, _ => result.Layer));
                IsDeveloping = false;
                Finished?.Invoke();
                done.TrySetResult(true);
                return;
            }
            IsDeveloping = false;
            Problem = PersonMessage.Text(failure!).ToString();
            ResumeDrafts();
            done.TrySetResult(false);
        });
        return done.Task;
    }

    /// <summary>Cancel at any moment: a development that is running is abandoned and the document stays as it was.</summary>
    public void Cancel()
    {
        developRun = null;
        IsDeveloping = false;
        StopDrafts();
        Finished?.Invoke();
    }
}

public sealed partial class EditorSession
{
    private PhotoshopDocument? photoshop;

    /// <summary>File › Export › Photoshop File…: what the sheet needs from the document (the Import Report is
    /// ImportReport).</summary>
    public IPhotoshopDocument Photoshop => photoshop ??= new PhotoshopDocument(this);

    /// <summary>Layer › Develop…: a model for the active layer's RAW file; null when it has none (or it can't be read).</summary>
    public IRawDevelopModel? MakeRawDevelopModel() => ActiveLayer is { } layer ? RawDevelopModel.Make(this, layer) : null;
}

/// <summary>File › Export › Photoshop File…: the document's part (PSDExport.data(for:depth:flattened:)).</summary>
public sealed class PhotoshopDocument(EditorSession session) : IPhotoshopDocument
{
    public bool BlendsInLinearLight => session.State.BlendSpace == BlendSpace.Linear;

    public Task<(byte[] Data, PSDExportReport Report)> PreparePSDExportAsync(int depth, bool flattened, CancellationToken cancellation)
    {
        var snapshot = session.State;
        return Task.Run(() => PSDExport.Data(snapshot, depth, flattened), cancellation);
    }
}
