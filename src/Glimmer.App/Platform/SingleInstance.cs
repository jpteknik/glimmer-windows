// One Glimmer per user and session, as on the Mac where the system sends a file to the running app: a second copy
// started from Explorer (double-clicking a .glimmer, an image, "Open with") hands its files to the first over a named
// pipe and exits, and the first opens them in new tabs and comes to the front. The pipe accepts only the same user.
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Glimmer.App.Platform;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stopping = new();
    private readonly string pipeName;

    private SingleInstance(Mutex mutex, bool isFirst, string name)
    {
        this.mutex = mutex;
        IsFirst = isFirst;
        pipeName = name;
    }

    /// <summary>True for the copy that owns the name; the others hand their files over.</summary>
    public bool IsFirst { get; }

    /// <summary>Per user (SID) and per Windows session, so two people signed in on one PC each have their own Glimmer.</summary>
    private static string BaseName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        return $"Glimmer-{sid}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    }

    public static SingleInstance Claim()
    {
        var name = BaseName();
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool created);
        return new SingleInstance(mutex, created, name);
    }

    /// <summary>Second copy: sends the files (absolute paths) to the first and lets it take the foreground. False when the
    /// first can't be reached within the timeout (it is still starting, or hung); the caller then runs on its own.</summary>
    public bool HandOff(IReadOnlyList<string> paths, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var message = JsonSerializer.SerializeToUtf8Bytes(new Message([.. paths.Select(Path.GetFullPath)]));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                pipe.Connect((int)Math.Max(1, Math.Min(1000, (deadline - DateTime.UtcNow).TotalMilliseconds)));
                // The first copy may bring its window forward only if this one, which the person just started, allows it.
                if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid)) AllowSetForegroundWindow(pid);
                var length = BitConverter.GetBytes(message.Length);
                pipe.Write(length);
                pipe.Write(message);
                pipe.Flush();
                return pipe.ReadByte() == 1;
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(100); }
        }
        return false;
    }

    /// <summary>First copy: answers every later copy until the app ends. `open` is called on a pool thread with the
    /// files (possibly none: then the window only comes forward).</summary>
    public void Listen(Action<IReadOnlyList<string>> open)
    {
        if (!IsFirst) throw new InvalidOperationException("Only the first copy listens.");
        _ = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                                      PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stopping.Token);
                    var header = new byte[4];
                    await pipe.ReadExactlyAsync(header, stopping.Token);
                    int length = BitConverter.ToInt32(header);
                    if (length is < 0 or > 1 << 20) continue;           // not ours
                    var body = new byte[length];
                    await pipe.ReadExactlyAsync(body, stopping.Token);
                    var message = JsonSerializer.Deserialize<Message>(body);
                    pipe.WriteByte(1);
                    await pipe.FlushAsync(stopping.Token);
                    open(message?.Paths ?? []);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or JsonException or EndOfStreamException)
                {
                    // A copy that went away mid-message, or something that isn't Glimmer: wait for the next one.
                }
            }
        });
    }

    public void Dispose()
    {
        stopping.Cancel();
        if (IsFirst)
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        mutex.Dispose();
    }

    private sealed record Message(string[] Paths);

    /// <summary>The files on a command line: everything that isn't an option ("--language sv" takes a value).</summary>
    public static IReadOnlyList<string> PathsIn(string[] args)
    {
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--language") { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            paths.Add(args[i]);
        }
        return paths;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
