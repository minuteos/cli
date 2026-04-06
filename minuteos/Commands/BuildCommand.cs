using MinuteOS.Cli.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("build", Description = "Build a minuteos project")]
public class BuildCommand : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration name to build (omit to build all)")]
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

        // Determine which configurations to build
        var configNames = Configuration != ""
            ? [Configuration]
            : projectConfig.ConfigurationNames.ToList();

        if (configNames.Count == 0)
        {
            Logger.LogError("No configurations defined in {File}", ProjectConfig.FileName);
            return 1;
        }

        var success = true;
        foreach (var configName in configNames)
        {
            Logger.LogInformation("=== Building configuration: {Name} ===", configName);
            Logger.LogInformation("");

            BuildConfiguration config;
            try
            {
                config = BuildConfiguration.Create(projectConfig, configName, projectRoot);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to resolve configuration '{Name}': {Message}", configName, ex.Message);
                success = false;
                continue;
            }

            var toolchain = new Toolchain(config.Profile.ToolchainPrefix ?? "", Logger);
            var parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount;
            var runner = new BuildRunner(toolchain, Logger);

            if (!await runner.BuildAsync(config, parallelism, cancellationToken))
                success = false;

            Logger.LogInformation("");
        }

        return success ? 0 : 1;
    }
}
