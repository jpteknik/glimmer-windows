// New file, no Swift original. Entry point of Glimmer.com: see Companion for what it does.
using System.Text;

namespace Glimmer.Console;

internal static class Program
{
    private static int Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        // In a console the console's own encoding shows å ä ö as the user's code page expects. Redirected to a file or a
        // pipe the bytes are UTF-8 without a BOM, as Glimmer.exe writes them under GLIMMER_OUTPUT_ENCODING.
        var output = System.Console.IsOutputRedirected ? Utf8Writer(System.Console.OpenStandardOutput()) : System.Console.Out;
        var error = System.Console.IsErrorRedirected ? Utf8Writer(System.Console.OpenStandardError()) : System.Console.Error;
        return Companion.Execute(args, Environment.ProcessPath, output, error, cancel.Token);
    }

    private static StreamWriter Utf8Writer(Stream stream) => new(stream, new UTF8Encoding(false)) { AutoFlush = true };
}
