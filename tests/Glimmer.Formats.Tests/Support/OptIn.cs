// New file, no Swift original: the Swift suites that need files from outside the repository are enabled with
// `.enabled(if: ProcessInfo.processInfo.environment[...] != nil)`; this is that condition as an xUnit attribute.

namespace Glimmer.Formats.Tests;

/// <summary>A test that runs only when the environment variable names something (a file or folder outside the
/// repository).</summary>
public sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string variable)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable))) Skip = $"{variable} is not set (opt-in)";
    }
}
