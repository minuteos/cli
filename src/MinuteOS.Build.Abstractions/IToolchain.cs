namespace MinuteOS.Build;

/// <summary>
/// The toolchain surface a build step uses: program names and process execution.
/// The gcc implementation lives in MinuteOS.Build; a different toolchain provides
/// its own <see cref="IToolchain"/> (and its own steps).
/// </summary>
public interface IToolchain
{
    string CC { get; }
    string CXX { get; }
    string ObjCopy { get; }
    string ObjDump { get; }
    string Size { get; }

    /// <summary>Runs an arbitrary toolchain program.</summary>
    Task<CompilationResult> RunToolAsync(
        string program, IEnumerable<string> arguments, string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Runs a command line through the system shell (pipes/redirects work).</summary>
    Task<CompilationResult> RunShellAsync(string command, string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Reports the size of a linked image.</summary>
    Task<CompilationResult> SizeAsync(string elfPath, CancellationToken cancellationToken = default);
}

/// <summary>Result of running a toolchain program.</summary>
public record CompilationResult(int ExitCode, string StdOut, string StdErr, string Command)
{
    public bool Success => ExitCode == 0;
}
