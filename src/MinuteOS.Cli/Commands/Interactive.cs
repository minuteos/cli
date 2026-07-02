using System.Diagnostics;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Launches processes with inherited stdio (live, interactive output) - used by
/// run/flash/erase/debug, unlike the toolchain's captured execution.
/// </summary>
internal static class Interactive
{
    /// <summary>Runs to completion and returns the exit code (130 on cancel).</summary>
    public static async Task<int> RunAsync(
        string program, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        using var process = Start(program, args, workingDirectory);
        if (process == null)
            return 1;

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return 130; // 128 + SIGINT
        }
        return process.ExitCode;
    }

    /// <summary>Starts a long-running process (e.g. a gdb server); null on failure.</summary>
    public static Process? Start(string program, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    public static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
}
