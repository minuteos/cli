using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("clean", Description = "Clean build artifacts")]
public class CleanCommand : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration name to clean (omit to clean all)")]
    public string Configuration { get; set; } = "";

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);
        var outDir = Path.Combine(projectRoot, "out");

        if (Configuration != "")
        {
            var configDir = Path.Combine(outDir, Configuration);
            if (Directory.Exists(configDir))
            {
                Logger.LogInformation("Removing {Dir}", configDir);
                Directory.Delete(configDir, recursive: true);
                Logger.LogInformation("Cleaned '{Config}' build artifacts.", Configuration);
            }
            else
            {
                Logger.LogInformation("Nothing to clean for '{Config}'.", Configuration);
            }
        }
        else
        {
            if (Directory.Exists(outDir))
            {
                Logger.LogInformation("Removing {Dir}", outDir);
                Directory.Delete(outDir, recursive: true);
                Logger.LogInformation("Cleaned all build artifacts.");
            }
            else
            {
                Logger.LogInformation("Nothing to clean.");
            }
        }

        return Task.FromResult(0);
    }
}
