// The console a command-line action reports to. Glimmer.exe is a windowed app with no console of its own: started from a
// terminal it attaches to the terminal's console; output the caller redirected to a file or a pipe already has its
// handles and is used as it is; started from Explorer or the Task Scheduler there is nowhere to write, and nothing is.
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Glimmer.App.Platform;

public static class ConsoleOutput
{
    public static (TextWriter Output, TextWriter Error) Attach()
    {
        if (!IsValid(GetStdHandle(StdOutput)) && !IsValid(GetStdHandle(StdError))) AttachConsole(AttachParentProcess);
        // Opened after attaching, so they write to the console's handles (Console.Out would have been made without one).
        // The console companion (Glimmer.com) sets GLIMMER_OUTPUT_ENCODING=utf-8 and reads the pipe as UTF-8, so Swedish
        // characters in a file name survive; the BOM is left out because it would be the first bytes of the output.
        var encoding = string.Equals(Environment.GetEnvironmentVariable("GLIMMER_OUTPUT_ENCODING"), "utf-8", StringComparison.OrdinalIgnoreCase)
            ? new UTF8Encoding(false)
            : Console.OutputEncoding;
        var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
        var error = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };
        return (output, error);
    }

    private static bool IsValid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    private const int StdOutput = -11, StdError = -12;
    private const uint AttachParentProcess = unchecked((uint)-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
