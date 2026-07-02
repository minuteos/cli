using Microsoft.Extensions.Logging;
using MinuteOS.Debug;
using MinuteOS.Debug.Dap;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Runs the minuteos debug adapter: the C# port of the minute-debug
/// extension's DAP session, speaking the Debug Adapter Protocol over stdio.
/// This is how the debug engine crosses the extension's in-process boundary -
/// VS Code (or any DAP client) starts <c>minuteos dap</c> as a
/// DebugAdapterExecutable and talks to it over the pipe. Launch requests
/// accept either inline arguments (program/gdb/server) or
/// <c>{ "config": "&lt;name&gt;" }</c> resolved through the build system.
/// </summary>
[Command("dap", Description = "Run the debug adapter (DAP over stdio; for editors/IDEs)")]
public class DapCommand
{
    [Option("--verbose-log", Description = "Trace adapter/GDB traffic to stderr")]
    public bool VerboseLog { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        // stdout carries the protocol; all diagnostics go to stderr.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var logger = new StderrLogger(VerboseLog ? LogLevel.Trace : LogLevel.Warning);

        using var connection = new DapConnection(input, output);
        await using var session = new DebugSession(connection, logger);
        try
        {
            await session.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }
}
