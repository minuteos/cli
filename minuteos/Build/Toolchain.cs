using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build;

/// <summary>
/// GCC-based toolchain for compiling minuteos projects.
/// Compiler flags are driven by the configuration profile.
/// </summary>
public class Toolchain
{
    private readonly ILogger _logger;
    public string Prefix { get; }

    public string CC => Prefix + "gcc";
    public string CXX => Prefix + "g++";
    public string ObjCopy => Prefix + "objcopy";
    public string ObjDump => Prefix + "objdump";
    public string Size => Prefix + "size";

    public Toolchain(string prefix = "", ILogger? logger = null)
    {
        Prefix = prefix;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<CompilationResult> SizeAsync(string elfPath, CancellationToken cancellationToken = default)
    {
        return await RunAsync(Size, [elfPath], Path.GetDirectoryName(elfPath)!, cancellationToken);
    }

    /// <summary>
    /// Runs an arbitrary toolchain program. Used by build steps.
    /// </summary>
    public Task<CompilationResult> RunToolAsync(
        string program,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
        => RunAsync(program, arguments, workingDirectory, cancellationToken);

    /// <summary>
    /// Runs a command line through the system shell (so make-style recipes with
    /// pipes/redirects work). Used by the generic shell build step.
    /// </summary>
    public Task<CompilationResult> RunShellAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");
        return RunAsync(shell, [flag, command], workingDirectory, cancellationToken);
    }

    private async Task<CompilationResult> RunAsync(
        string program,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var argString = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        _logger.LogDebug("{Program} {Args}", program, argString);

        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {program}");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CompilationResult(
            process.ExitCode,
            stdout,
            stderr,
            $"{program} {argString}");
    }
}

public record CompilationResult(int ExitCode, string StdOut, string StdErr, string Command)
{
    public bool Success => ExitCode == 0;
}
