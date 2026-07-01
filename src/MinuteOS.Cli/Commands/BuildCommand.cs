using MinuteOS.Build;
using MinuteOS.Build.Graph;
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

    [Option("--hash", Description = "Use content-hash fingerprints (mtime+size by default)")]
    public bool Hash { get; set; }

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

        var missing = DependencyRestorer.MissingDependencies(projectConfig, projectRoot);
        if (missing.Count > 0)
            Logger.LogWarning("Missing dependencies: {Deps}. Run 'minuteos restore'.", string.Join(", ", missing));

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

            var toolchain = new Toolchain(config.Settings.Scalar("gcc.toolchain-prefix") ?? "", Logger);
            var parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount;

            var options = new BuildOptions
            {
                Parallelism = parallelism,
                Fingerprinter = Hash ? new ContentHashFingerprinter() : null,
            };
            if (!await GraphRunner.BuildAsync(config, toolchain, Logger, options, cancellationToken: cancellationToken))
                success = false;

            Logger.LogInformation("");
        }

        return success ? 0 : 1;
    }
}
