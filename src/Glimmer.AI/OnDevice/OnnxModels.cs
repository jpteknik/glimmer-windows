// New in the port, no Swift file of its own: what the Mac gets from `ModelStore.load` (an MLModel per package, made once
// and kept) and from RealESRGAN's bundled-model loader, for ONNX Runtime. Sessions run on the CPU, on Windows and on
// Linux alike; ONNX Runtime spreads one run over the cores itself. The exception is the bundled Real-ESRGAN model, which
// may also run on the graphics card (RunBundled; the provider is found and registered in OnnxGpu.cs), with the CPU
// session as its fallback.
using Microsoft.ML.OnnxRuntime;

namespace Glimmer.AI;

internal static class OnnxModels
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, InferenceSession> Sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// The options every session gets: all graph optimisations (which also fold a float16 model's weight casts once),
    /// and no memory arena, so the memory of a run (over a gigabyte for some models at 1024²) goes back when it ends
    /// instead of staying with the app.
    /// </summary>
    public static SessionOptions Options() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        EnableCpuMemArena = false,
    };

    /// <summary>
    /// The session for <paramref name="file"/> in <paramref name="package"/> of <paramref name="model"/>, found through
    /// <paramref name="store"/> (which never downloads: a model that isn't here is <see cref="ModelMissingError"/>), made
    /// once and kept. A file ONNX Runtime can't load is <see cref="ModelUnavailableError"/>.
    /// </summary>
    public static async Task<InferenceSession> SessionAsync(ModelStore store, OnDeviceModel model, string package, string file, CancellationToken ct)
    {
        var dir = await store.LocateAsync(package, model, ct).ConfigureAwait(false);
        var path = Path.Combine(dir, file);
        lock (Gate)
        {
            if (Sessions.TryGetValue(path, out var cached)) return cached;
        }
        ct.ThrowIfCancellationRequested();
        InferenceSession made;
        try
        {
            using var options = Options();
            made = new InferenceSession(path, options);
        }
        catch (OnnxRuntimeException e)
        {
            throw new ModelUnavailableError(model, e.Message);
        }
        lock (Gate)
        {
            if (Sessions.TryGetValue(path, out var other))
            {
                made.Dispose();
                return other;
            }
            Sessions[path] = made;
            return made;
        }
    }

    /// <summary>A session for a model that is part of the app (an embedded resource), made once and kept.</summary>
    public static InferenceSession Bundled(string resource)
    {
        lock (Gate)
        {
            if (Sessions.TryGetValue("resource:" + resource, out var cached)) return cached;
            var model = ResourceBytes(resource);
            using var options = Options();
            var made = new InferenceSession(model, options);
            Sessions["resource:" + resource] = made;
            return made;
        }
    }

    /// <summary>The bytes of a model that is part of the app; a missing one is <see cref="FileNotFoundException"/>.</summary>
    private static byte[] ResourceBytes(string resource)
    {
        using var stream = typeof(OnnxModels).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("The model is missing from the app.", resource);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Closes the sessions of the files under <paramref name="folder"/> (before a model is removed or replaced).</summary>
    public static void Release(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        lock (Gate)
        {
            foreach (var key in Sessions.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                Sessions[key].Dispose();
                Sessions.Remove(key);
            }
        }
    }

    /// <summary>One input of a run: a float or 64-bit integer tensor of the given shape.</summary>
    public readonly record struct Input(string Name, Array Data, long[] Shape)
    {
        public static Input Floats(string name, float[] data, params long[] shape) => new(name, data, shape);
        public static Input Longs(string name, long[] data, params long[] shape) => new(name, data, shape);
    }

    /// <summary>The float outputs <paramref name="outputs"/> of one run, each with its shape.</summary>
    public static (float[] Data, long[] Shape)[] Run(InferenceSession session, IReadOnlyList<Input> inputs, params string[] outputs)
    {
        var values = new List<OrtValue>();
        try
        {
            foreach (var input in inputs)
            {
                values.Add(input.Data switch
                {
                    float[] f => OrtValue.CreateTensorValueFromMemory(f, input.Shape),
                    long[] l => OrtValue.CreateTensorValueFromMemory(l, input.Shape),
                    _ => throw new ArgumentException("Only float and long inputs.", nameof(inputs)),
                });
            }
            using var options = new RunOptions();
            using var results = session.Run(options, inputs.Select(i => i.Name).ToArray(), values, outputs);
            var o = new (float[] Data, long[] Shape)[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
                o[i] = (results[i].GetTensorDataAsSpan<float>().ToArray(), results[i].GetTensorTypeAndShape().Shape);
            return o;
        }
        finally
        {
            foreach (var v in values) v.Dispose();
        }
    }

    // MARK: The graphics card, for bundled models

    private static readonly object GpuGate = new();
    private static readonly Dictionary<string, InferenceSession> GpuSessions = new(StringComparer.Ordinal);
    private static volatile bool useGpu;
    private static volatile string? gpuFailure;
    private static volatile string? gpuDevice;
    private static int gpuRuns;

    /// <summary>
    /// Whether bundled models (Real-ESRGAN) may run on the graphics card. Off by default, so the tests and Linux stay on
    /// the CPU; the app switches it on at start-up. GLIMMER_GPU=0 keeps the CPU even when it is on. Downloaded models
    /// always run on the CPU, where their outputs are tested.
    /// </summary>
    public static bool UseGpu { get => useGpu; set => useGpu = value; }

    /// <summary>
    /// What runs bundled models, in words: "graphics card: &lt;device&gt;" once a session on it works (or "graphics card:
    /// not tried yet"), otherwise "CPU: &lt;reason&gt;" (switched off, GLIMMER_GPU=0, or why the graphics card failed).
    /// </summary>
    public static string GpuStatus
    {
        get
        {
            if (gpuFailure is { } failure) return "CPU: " + failure;
            if (!useGpu) return "CPU: the graphics card is switched off";
            if (CpuForced) return "CPU: GLIMMER_GPU=0";
            return "graphics card: " + (gpuDevice ?? "not tried yet");
        }
    }

    /// <summary>Runs completed on the graphics card since the process started or the last <see cref="ResetGpu"/>.</summary>
    internal static int GpuRuns => Volatile.Read(ref gpuRuns);

    /// <summary>For tests: replaces the run on the graphics card, to make one fail.</summary>
    internal static Func<InferenceSession, IReadOnlyList<Input>, string[], (float[] Data, long[] Shape)[]>? GpuRunOverride { get; set; }

    private static bool CpuForced => Environment.GetEnvironmentVariable("GLIMMER_GPU") == "0";

    /// <summary>
    /// One run of a model that is part of the app: on the graphics card when <see cref="UseGpu"/> is on and it works,
    /// otherwise exactly <c>Run(Bundled(resource), …)</c>. The first failure on the graphics card (no provider, no device,
    /// a session it can't make, an error or a value that isn't a number in a run) closes it for the rest of the process,
    /// and that run is made again on the CPU, so a result is never lost.
    /// </summary>
    public static (float[] Data, long[] Shape)[] RunBundled(string resource, IReadOnlyList<Input> inputs, params string[] outputs)
    {
        if (useGpu && gpuFailure is null && !CpuForced && RunOnGpu(resource, inputs, outputs) is { } result) return result;
        return Run(Bundled(resource), inputs, outputs);
    }

    private static (float[] Data, long[] Shape)[]? RunOnGpu(string resource, IReadOnlyList<Input> inputs, string[] outputs)
    {
        // One run at a time on the graphics card; the lock also keeps a session from being closed while it runs.
        lock (GpuGate)
        {
            if (gpuFailure is not null) return null;
            if (!GpuSessions.TryGetValue(resource, out var session))
            {
                session = GpuSession(resource);
                if (session is null) return null;
                GpuSessions[resource] = session;
            }
            try
            {
                var result = GpuRunOverride is { } replaced ? replaced(session, inputs, outputs) : Run(session, inputs, outputs);
                foreach (var (data, _) in result)
                {
                    foreach (float v in data)
                    {
                        if (!float.IsFinite(v))
                        {
                            CloseGpu("the graphics card returned a value that is not a number");
                            return null;
                        }
                    }
                }
                gpuRuns++;
                return result;
            }
            catch (Exception e)
            {
                CloseGpu("the graphics card failed in a run: " + e.Message);
                return null;
            }
        }
    }

    /// <summary>A session on the graphics card for a bundled model, or null (and the graphics card closed) when there is none.</summary>
    private static InferenceSession? GpuSession(string resource)
    {
        var model = ResourceBytes(resource);  // a missing model is FileNotFoundException, as on the CPU
        OrtEpDevice? device;
        string detail;
        try { device = OnnxGpu.Device(out detail); }
        catch (Exception e)
        {
            device = null;
            detail = "the graphics card could not be reached: " + e.Message;
        }
        if (device is null)
        {
            CloseGpu(detail);
            return null;
        }
        try
        {
            using var options = Options();
            // Every node on the graphics card or no session: a graph split between the two would copy each layer's
            // output back and forth, and the status would name a graphics card that isn't doing the work. (All 73 nodes
            // of Real-ESRGAN were placed on it when measured.)
            options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
            options.AppendExecutionProvider(OrtEnv.Instance(), [device], new Dictionary<string, string>());
            var made = new InferenceSession(model, options);
            gpuDevice = detail;
            return made;
        }
        catch (Exception e)
        {
            CloseGpu("the graphics card could not load the model: " + e.Message);
            return null;
        }
    }

    /// <summary>Closes the graphics card for the rest of the process and keeps the reason (under <see cref="GpuGate"/>).</summary>
    private static void CloseGpu(string reason)
    {
        gpuFailure = reason;
        gpuDevice = null;
        foreach (var session in GpuSessions.Values) session.Dispose();
        GpuSessions.Clear();
    }

    /// <summary>
    /// For tests: forgets the graphics card's sessions, failure and run count, so it is tried again. The provider library
    /// stays registered (ONNX Runtime keeps it for the process).
    /// </summary>
    internal static void ResetGpu()
    {
        lock (GpuGate)
        {
            foreach (var session in GpuSessions.Values) session.Dispose();
            GpuSessions.Clear();
            gpuFailure = null;
            gpuDevice = null;
            gpuRuns = 0;
        }
    }
}
