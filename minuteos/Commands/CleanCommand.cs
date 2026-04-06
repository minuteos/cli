using System.ComponentModel;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("clean", Description = "Clean build artifacts")]
public class CleanCommand : LoggingCommand
{
    [Option("--target", "-t", Description = "Target platform (default: host)")]
    [DefaultValue("host")]
    public string Target { get; set; } = "host";

    [Option("--config", "-c", Description = "Build configuration to clean (default: all configs)")]
    public string? Config { get; set; }

    [Option("--all", Description = "Remove entire out/ directory")]
    public bool All { get; set; }

    [Option("--project", "-p", Description = "Project root directory (default: current directory)")]
    public string? ProjectDir { get; set; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectDir ?? Directory.GetCurrentDirectory();
        projectRoot = Path.GetFullPath(projectRoot);

        if (All)
        {
            var outDir = Path.Combine(projectRoot, "out");
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
            return Task.FromResult(0);
        }

        var targetDir = Path.Combine(projectRoot, "out", Target);

        if (Config != null)
        {
            var configDir = Path.Combine(targetDir, Config);
            if (Directory.Exists(configDir))
            {
                Logger.LogInformation("Removing {Dir}", configDir);
                Directory.Delete(configDir, recursive: true);
                Logger.LogInformation("Cleaned {Target}/{Config} build artifacts.", Target, Config);
            }
            else
            {
                Logger.LogInformation("Nothing to clean.");
            }
        }
        else
        {
            if (Directory.Exists(targetDir))
            {
                Logger.LogInformation("Removing {Dir}", targetDir);
                Directory.Delete(targetDir, recursive: true);
                Logger.LogInformation("Cleaned {Target} build artifacts.", Target);
            }
            else
            {
                Logger.LogInformation("Nothing to clean.");
            }
        }

        return Task.FromResult(0);
    }
}
