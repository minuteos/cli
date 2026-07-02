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
        if (!await MinuteOS.Build.Graph.GraphRunner.BuildAsync(config, toolchain, Logger,
                new BuildOptions { Parallelism = parallelism }, cancellationToken: cancellationToken))
            return 1;

        // Launch it - directly for host, or via the configuration's run step (qemu/renode).
        var spec = RunSpecResolver.Resolve(config, config.PrimaryOutput, filter: null);
        var (program, args) = (spec.Program, spec.Args);

        Logger.LogInformation("");
        Logger.LogInformation("Running: {Program} {Args}", program, string.Join(' ', args));
        Logger.LogInformation("");

        return await Interactive.RunAsync(program, args, projectRoot, cancellationToken);
    }
}
