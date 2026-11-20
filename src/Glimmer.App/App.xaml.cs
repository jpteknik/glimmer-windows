// Ported from App/GlimmerApp.swift (the application entry; the document window is Shell/MainWindow) and the launch half
// of App/DocumentOpener.swift. One copy per user and session: a second start hands its files to the first
// (Platform/SingleInstance.cs). Start-up hands the editor its environment once: settings, the UI thread, the window as
// document host, dialogs, clipboard, sound, key storage, OCR, the image decoder, and the folders under
// %LOCALAPPDATA%\Glimmer (Models, Cache\AIResults, Recovery); then it starts the local agent server. Arguments that are
// a command (Glimmer.exe --export in out, the Mac's Shortcuts actions) run headless instead, with no window.
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Glimmer.App.Localization;
using Glimmer.App.Platform;
using Glimmer.App.Shell;

namespace Glimmer.App;

public partial class App : Application
{
    private SingleInstance? instance;
    private AgentServer? agents;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);
        CrashLog.Note($"Starting Glimmer {typeof(App).Assembly.GetName().Version} ({e.Args.Length} argument(s))");
        try
        {
            Start(e);
            CrashLog.MarkStarted();
            CrashLog.Note("Started.");
        }
        catch (Exception error)
        {
            CrashLog.Fatal(error, "start-up");
            Shutdown(1);
        }
    }

    private void Start(StartupEventArgs e)
    {
        // Pictures SkiaSharp can't read (TIFF, HEIC, JPEG XR, camera RAW) go through Windows' own codecs.
        ImageImporter.PlatformDecoder = new WicImageDecoder();
        // Camera RAW files are decoded by the same WIC path (Windows' RAW codec); Glimmer develops the result itself.
        RawDevelopment.Decoder = ImageImporter.PlatformDecoder;
        // HEIC is written by Windows' own HEIF encoder (HEIF and HEVC Video Extensions from the Microsoft Store); without
        // them HEIC isn't offered. Whether it works is found the first time something asks (Export, Batch Process), by
        // writing a small HEIC, not at start-up.
        ImageExporter.PlatformEncoder = new WicHeifEncoder();
        // Real-ESRGAN on the graphics card (ONNX Runtime's WebGPU provider over Direct3D 12). Any failure there moves the
        // rest of the run to the CPU by itself; GLIMMER_GPU=0 keeps the CPU. RealESRGAN.GpuStatus says which is used.
        RealESRGAN.UseGpu = true;

        // The installer's (src/Glimmer.Setup) two steps: the file types registered or taken back exactly as Settings ›
        // General does it, by the installed Glimmer.exe itself, with no window. Not commands a person types.
        if (e.Args is ["--register-file-types"] or ["--unregister-file-types"])
        {
            try
            {
                Loc.Initialize();
                if (e.Args[0] == "--register-file-types") FileAssociations.RegisterDocuments();
                else FileAssociations.Unregister();
                Shutdown(0);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
            {
                CrashLog.Note($"{e.Args[0]} failed: {error.Message}");
                Shutdown(1);
            }
            return;
        }

        // Glimmer.exe --auto-enhance in.jpg out.jpg and the other commands (the Mac's Shortcuts actions): no window, no
        // hand-over to a running copy, and the exit status is the command's.
        if (GlimmerCommandLine.IsCommand(e.Args))
        {
            Shutdown(RunCommandLine(e.Args));
            return;
        }

        var paths = SingleInstance.PathsIn(e.Args);
        instance = SingleInstance.Claim();
        if (!instance.IsFirst && instance.HandOff(paths))
        {
            Shutdown();
            return;
        }

        Loc.Initialize(LanguageArgument(e.Args));
        var settings = JsonAppSettings.ForCurrentUser();
        AppServices.Settings = settings;
        ThemeResources.Apply(this);

        // What Vision does on the Mac: text (Windows.Media.Ocr) and faces (Windows.Media.FaceAnalysis).
        OnDeviceServices.TextRecognizer = new WindowsTextRecognizer();
        OnDeviceServices.FaceDetector = new WindowsFaceDetector();

        var window = new MainWindow();
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer");
        var environment = new EditorEnvironment(settings, new SynchronizationContextScheduler(), window, AppServices.FileDialogs, AppServices.Clipboard,
                                                new AppSoundPlayer(), temporaryFolder: null, dataFolder: data)
        {
            Localize = Loc.T,
            MoveToRecycleBin = RecycleBin.Move,
            // API keys in Windows Credential Manager, one item per service ("Glimmer/<slug>").
            Keys = new ProviderKeys(CredentialSecretStore.For),
            Log = message => System.Diagnostics.Debug.WriteLine("Glimmer: " + message),
        };
        // Under data the editor keeps Recovery and the AI result cache (Cache\AIResults, AISetup.ResultCache).
        var opener = new DocumentOpener(environment);
        AppServices.Opener = opener;
        AppServices.LinkedFiles = opener.LinkedFilesWaiting;
        // %LOCALAPPDATA%\Glimmer\Models; also makes it ModelStore.Shared for the AI.
        AppServices.Models = new ModelLibrary(environment, Path.Combine(data, "Models"));
        // Settings › AI: keys through the environment's ProviderKeys (Credential Manager), never logged.
        AppServices.AISettings = new AISettingsService(environment);
        // File › Batch Process…: a runner per sheet, as the Mac's sheet owns its own.
        AppServices.BatchRunners = () => new BatchRunner(environment);
        AppServices.Documents = window;
        MainWindow = window;
        window.Show();

        // The local API for agents (its guide at http://127.0.0.1:<port>/glimmer, loopback only), and the splash that
        // points agents to it; started where the Mac starts it, before linked documents come back.
        agents = new AgentServer(opener) { OpenSettings = window.OpenSettings };
        agents.Start();
        AppServices.AgentServer = agents;

        // When .glimmer opens with this copy, its File Explorer thumbnails are this copy's too (a newer Glimmer brings a
        // newer handler). Registry and a small file only, away from the window; it never stops the start.
        if (FileAssociations.IsDefaultForDocuments)
        {
            _ = Task.Run(() =>
            {
                try { ThumbnailHandler.Register(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    CrashLog.Note("Thumbnail handler not refreshed: " + error.Message);
                }
            });
        }

        // Undo leftovers are cleaned, linked documents come back; what a stop left unsaved is offered.
        foreach (var entry in opener.Launch()) window.OfferRecovered(opener, entry);
        if (paths.Count > 0) opener.Open(paths);
        // The Mac's DocumentGroup opens an untitled document (GlimmerDocument's default: 1920 × 1080, white).
        if (window.DocumentCount == 0) opener.CreateDocument(DocumentState.New(1920, 1080, DocumentState.Background.White));
        ShowAtLaunch(window, e.Args);

        if (instance.IsFirst)
        {
            instance.Listen(handed => Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (handed.Count > 0) opener.Open(handed);
                window.BringToFront();
            }));
        }
    }

    /// <summary>DocumentOpener's launch order: Welcome the first time, otherwise Agent Access while "agent.splashAtLaunch"
    /// is on (the Mac's -skipWelcome / -skipAgentSplash are --skip-welcome / --skip-agent-splash here).</summary>
    private static async void ShowAtLaunch(MainWindow window, string[] args)
    {
        bool welcome = !AppServices.Settings.Get(AppPreferences.WelcomeShownKey, false) && !args.Contains("--skip-welcome");
        if (welcome)
        {
            await Task.Delay(800);
            window.ShowWelcomeWindow();
        }
        else if (AppServices.AgentServer is not null && AppServices.Settings.Get(Views.Sheets.AgentSplashWindow.ShowAtLaunchKey, true) && !args.Contains("--skip-agent-splash"))
        {
            await Task.Delay(1000);
            window.ShowAgentSplash();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        agents?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A command-line action, with its words in the console it was started from (a windowed app has none of its
    /// own). The work runs off the UI thread, so its own awaits never wait for the dispatcher this call blocks.</summary>
    private static int RunCommandLine(string[] args)
    {
        var (output, error) = ConsoleOutput.Attach();
        var settings = JsonAppSettings.ForCurrentUser();
        try
        {
            return Task.Run(() => GlimmerCommandLine.Run(args, settings, output, error)).GetAwaiter().GetResult();
        }
        finally
        {
            output.Flush();
            error.Flush();
        }
    }

    /// <summary>"--language sv" or "--language en" (for trying the other language without changing Windows).</summary>
    private static string? LanguageArgument(string[] args)
    {
        int i = Array.IndexOf(args, "--language");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
