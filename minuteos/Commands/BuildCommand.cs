using System.ComponentModel;
using MinuteOS.Cli.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("build", Description = "Build a minuteos project")]
public class BuildCommand : LoggingCommand
{
    [Option("--target", "-t", Description = "Target platform (default: host)")]
    [DefaultValue("host")]
    public string Target { get; set; } = "host";

    [Option("--config", "-c", Description = "Build configuration (Release, Debug, Trace)")]
    [DefaultValue("Release")]
    public string Config { get; set; } = "Release";

    [Option("--components", Description = "Components to build (comma-separated, default: kernel)")]
    public string? Components { get; set; }

    [Option("--toolchain-prefix", Description = "Toolchain prefix (e.g., arm-none-eabi-)")]
    [DefaultValue("")]
    public string ToolchainPrefix { get; set; } = "";

    [Option("--jobs", "-j", Description = "Number of parallel compilation jobs")]
    [DefaultValue(0)]
    public int Jobs { get; set; }

    [Option("--project", "-p", Description = "Project root directory (default: current directory)")]
    public string? ProjectDir { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectDir ?? Directory.GetCurrentDirectory();
        projectRoot = Path.GetFullPath(projectRoot);

        var components = Components?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ["kernel"];

        Logger.LogInformation("minuteos build");
        Logger.LogInformation("==============");
        Logger.LogInformation("");

        BuildConfiguration config;
        try
        {
            config = BuildConfiguration.Create(
                projectRoot,
                target: Target,
                config: Config,
                components: components);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to configure build: {Message}", ex.Message);
            return 1;
        }

        var parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount;
        var toolchain = new Toolchain(ToolchainPrefix, Logger);
        var runner = new BuildRunner(toolchain, Logger);

        var success = await runner.BuildAsync(config, parallelism, cancellationToken);
        return success ? 0 : 1;
    }
}
