// Ported from App/BatchSheet.swift (BatchRunner; the sheet itself is the app's)
using Glimmer.AI;
using Glimmer.Core;
using Glimmer.Formats;

namespace Glimmer.Editor;

/// <summary>Runs a recipe over many files, one at a time away from the UI thread, with the same code the command line
/// uses (HeadlessEditor). Rating runs a few photos at a time.</summary>
public sealed class BatchRunner(EditorEnvironment environment) : ObservableObject, IBatchRunner
{
    private readonly List<BatchOutcome> outcomes = [];
    private readonly List<BatchOutcome> failures = [];
    /// <summary>Whether each processed file succeeded, by file: a row looks itself up here, not through all outcomes.</summary>
    private readonly Dictionary<string, bool> succeeded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhotoRating> ratings = new(StringComparer.Ordinal);
    private bool isRunning, isRating;
    private int done, total, rated;
    private CancellationTokenSource? cancellation;

    private IUiScheduler Scheduler => environment.Scheduler;

    public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value); }
    public int Done { get => done; private set => Set(ref done, value); }
    public int Total { get => total; private set => Set(ref total, value); }
    public IReadOnlyList<BatchOutcome> Outcomes => outcomes;
    public IReadOnlyList<BatchOutcome> Failures => failures;
    public bool? Succeeded(string path) => succeeded.TryGetValue(path, out var ok) ? ok : null;

    public void Start(IReadOnlyList<string> files, BatchRecipe recipe, ExportOptions options, string destination, string suffix)
    {
        if (IsRunning || files.Count == 0) return;
        var action = recipe is BatchRecipe.Action(var id)
            ? Glimmer.AI.SavedActions.Load(new SettingsSavedActionStore(environment.Settings)).FirstOrDefault(a => a.Id == id)
            : null;
        outcomes.Clear();
        succeeded.Clear();
        failures.Clear();
        Done = 0;
        Total = files.Count;
        IsRunning = true;
        Raise(nameof(Outcomes), nameof(Failures));
        var cancel = new CancellationTokenSource();
        cancellation = cancel;
        Next(0);

        void Next(int index)
        {
            if (index >= files.Count || cancel.IsCancellationRequested)
            {
                IsRunning = false;
                return;
            }
            var path = files[index];
            string? output = null;
            Exception? error = null;
            Scheduler.RunInBackground(() =>
            {
                try { output = Process(path, recipe, action, options, destination, suffix); }
                catch (Exception e) { error = e; }
            }, () =>
            {
                if (error is null)
                {
                    outcomes.Add(new BatchOutcome(path, output, null));
                    succeeded[path] = true;
                }
                else
                {
                    var failed = new BatchOutcome(path, null, error.Message);
                    outcomes.Add(failed);
                    failures.Add(failed);
                    succeeded[path] = false;
                }
                Done++;
                Raise(nameof(Outcomes), nameof(Failures));
                Next(index + 1);
            });
        }
    }

    public void Cancel() => cancellation?.Cancel();

    // MARK: Rating

    /// <summary>How many photos are rated at the same time.</summary>
    public const int RatingWidth = 4;

    public bool IsRating { get => isRating; private set => Set(ref isRating, value); }
    public int Rated { get => rated; private set => Set(ref rated, value); }
    public PhotoRating? Rating(string path) => ratings.TryGetValue(path, out var r) ? r : null;

    /// <summary>Rates the photos on this computer (aesthetics and sharpness), a few at a time.</summary>
    public void Rate(IReadOnlyList<string> files)
    {
        if (IsRating || IsRunning) return;
        var todo = new Queue<string>(files.Where(f => !ratings.ContainsKey(f)));
        if (todo.Count == 0) return;
        IsRating = true;
        Rated = 0;
        var cancel = new CancellationTokenSource();
        cancellation = cancel;
        int running = 0;
        for (int i = 0; i < RatingWidth && todo.Count > 0; i++) StartOne(todo.Dequeue());

        void StartOne(string path)
        {
            running++;
            PhotoRating? rating = null;
            Scheduler.RunInBackground(() =>
            {
                if (cancel.IsCancellationRequested) return;
                try
                {
                    if (Thumbnail(path) is { } image)
                    {
                        var r = PhotoQuality.Rate(image);
                        rating = new PhotoRating(r.Stars, r.IsBlurry, r.IsUtility, r.Aesthetics, r.Sharpness, r.Score);
                    }
                }
                catch (Exception)
                {
                    // A photo that can't be read is left unrated.
                }
            }, () =>
            {
                running--;
                if (rating is { } found) ratings[path] = found;
                Rated++;
                if (!cancel.IsCancellationRequested && todo.Count > 0) StartOne(todo.Dequeue());
                else if (running == 0) IsRating = false;
            });
        }
    }

    /// <summary>A copy of about 1024 pixels on its longest side.</summary>
    internal static RGBAImage? Thumbnail(string path)
    {
        var decoded = ImageImporter.Image(path);
        var state = new DocumentState(decoded.Width, decoded.Height, layers: [new Layer("", new RasterContent(ImageImporter.Tiles(decoded)))]);
        return AIController.Composite(state).Limited(1024);
    }

    /// <summary>One file through the recipe, written into <paramref name="destination"/>; returns the file written.</summary>
    public static string Process(string path, BatchRecipe recipe, SavedAction? action, ExportOptions options, string destination, string suffix)
    {
        var state = HeadlessEditor.Document(path);
        state = recipe switch
        {
            BatchRecipe.ConvertOnly => state,
            BatchRecipe.AutoEnhance => HeadlessEditor.AutoEnhance(state),
            BatchRecipe.RemoveBackground => HeadlessEditor.RemoveBackground(state),
            BatchRecipe.Preset(var id) => HeadlessEditor.ApplyPreset(Preset.All.FirstOrDefault(p => p.Id == id)
                                                                     ?? throw new HeadlessEditor.Failure("That preset no longer exists."), state),
            BatchRecipe.Action => HeadlessEditor.Run(action ?? throw new HeadlessEditor.Failure("That action no longer exists."), state),
            _ => state,
        };
        var data = HeadlessEditor.Encoded(state, options);
        var output = UniquePath(destination, Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\')) + suffix, HeadlessEditor.Extension(options.Format));
        WriteAtomically(output, data);
        return output;
    }

    /// <summary>base.ext in folder, numbered if that name is taken.</summary>
    public static string UniquePath(string folder, string name, string extension)
    {
        var candidate = Path.Combine(folder, $"{name}.{extension}");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++) candidate = Path.Combine(folder, $"{name} {n}.{extension}");
        return candidate;
    }

    internal static void WriteAtomically(string path, byte[] data)
    {
        var temporary = path + ".writing";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, path, overwrite: true);
    }
}
