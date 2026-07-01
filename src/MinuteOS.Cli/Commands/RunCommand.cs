using System.Diagnostics;
using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("run", Description = "Build a configuration and run its output (host directly, or via the configured emulator)")]
public class RunCommand : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration to run (default: the first one)")]
    public string Configuration { get; set; } = "";

    [Option("--jobs", "-j", Description = "Number of parallel compilation jobs")]
    public int Jobs { get; set; }

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        ProjectConfig projectConfig;
        try
        {
            projectConfig = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("{Message}", ex.Message);
            return 1;
        }

        var configName = Configuration != "" ? Configuration : projectConfig.ConfigurationNames.FirstOrDefault();
        if (configName == null)
        {
            Logger.LogError("No configurations defined in {File}.", ProjectConfig.FileName);
            return 1;
        }

        BuildConfiguration config;
        try
        {
            config = BuildConfiguration.Create(projectConfig, configName, projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to resolve configuration '{Name}': {Message}", configName, ex.Message);
            return 1;
        }

        // Build the primary output through the task-graph engine.
        var toolchain = new Toolchain(config.Settings.Scalar("gcc.toolchain-prefix") ?? "", Logger);
        var parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount;
        if (!await MinuteOS.Build.Graph.GraphRunner.BuildAsync(config, toolchain, Logger, cancellationToken, parallelism))
            return 1;

        // Launch it - directly for host, or via the configuration's run step (qemu/renode).
        var spec = RunSpecResolver.Resolve(config, config.PrimaryOutput, filter: null);
        var (program, args) = (spec.Program, spec.Args);

        Logger.LogInformation("");
        Logger.LogInformation("Running: {Program} {Args}", program, string.Join(' ', args));
        Logger.LogInformation("");

        return await LaunchAsync(program, args, projectRoot, cancellationToken);
    }

    /// <summary>
    /// Runs the process with inherited stdio (live output) and returns its exit code.
    /// </summary>
    private async Task<int> LaunchAsync(string program, List<string> args, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to start '{Program}': {Message}", program, ex.Message);
            return 1;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return 130; // 128 + SIGINT
            }
            return process.ExitCode;
        }
    }
}
