// New for Windows: a start-up log and crash reports in %LOCALAPPDATA%\Glimmer\Logs, so a failure is never silent. A WPF
// app that throws while starting otherwise just disappears (the first build did exactly that: a missing resource ended
// the process before any window, and only a crash dump said why).
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Glimmer.App.Platform;

public static class CrashLog
{
    /// <summary>%LOCALAPPDATA%\Glimmer\Logs.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimmer", "Logs");

    private static bool started;

    /// <summary>Catches what nothing else catches: the UI thread, other threads and unobserved tasks.</summary>
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Write(error, "background thread");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "unobserved task");
            e.SetObserved();
        };
    }

    /// <summary>Start-up has finished: from now on an unexpected error is shown and the app keeps running.</summary>
    public static void MarkStarted() => started = true;

    /// <summary>One line in startup.log (time-stamped). Never throws.</summary>
    public static void Note(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "startup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // A log that can't be written must not take the app down with it.
        }
    }

    /// <summary>Writes the whole exception to crash-&lt;time&gt;.txt and returns the path (null if it couldn't be written).</summary>
    public static string? Write(Exception error, string where)
    {
        Note($"Error ({where}): {error.GetType().FullName}: {error.Message}");
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path,
                $"Glimmer {typeof(CrashLog).Assembly.GetName().Version} on {Environment.OSVersion} ({Environment.ProcessorCount} cores)" +
                $"{Environment.NewLine}Where: {where}{Environment.NewLine}{Environment.NewLine}{error}{Environment.NewLine}");
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Start-up failed: say so, with the reason and where the details are.</summary>
    public static void Fatal(Exception error, string where)
    {
        string? path = Write(error, where);
        Show("Glimmer couldn't start.", error, path);
    }

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? path = Write(e.Exception, "UI thread");
        if (!started) return;   // during start-up the exception reaches App.OnStartup, which reports it and quits
        Show("Something went wrong in Glimmer. Your documents are still open; save your work.", e.Exception, path);
        e.Handled = true;
    }

    private static void Show(string headline, Exception error, string? path)
    {
        string details = path is null ? "" : $"{Environment.NewLine}{Environment.NewLine}Details: {path}";
        try
        {
            MessageBox.Show($"{headline}{Environment.NewLine}{Environment.NewLine}{error.GetType().Name}: {error.Message}{details}",
                "Glimmer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // No message box possible (e.g. no desktop): the log above is all there is.
        }
    }
}
