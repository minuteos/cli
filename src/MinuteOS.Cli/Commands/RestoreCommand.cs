using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("restore", Description = "Restore external dependencies (init git submodules, clone declared repos)")]
public class RestoreCommand : LoggingCommand
{
    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        ProjectConfig project;
        try
        {
            project = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("{Message}", ex.Message);
            return 1;
        }

        var results = await DependencyRestorer.RestoreAsync(project, projectRoot, Logger, cancellationToken);

        var anyFailure = false;
        foreach (var r in results)
        {
            if (r.Ok)
                Logger.LogInformation("  {Name}: {Status}", r.Name, r.Status);
            else
            {
                anyFailure = true;
                Logger.LogWarning("  {Name}: {Status}", r.Name, r.Status);
            }
        }

        if (results.Count == 0)
            Logger.LogInformation("No declared dependencies; any git submodules were initialized.");

        return anyFailure ? 1 : 0;
    }
}
