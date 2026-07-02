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

    [Option("--dry-run", "-n", Description = "Report what would run and why, without building")]
    public bool DryRun { get; set; }

    [Option("--explain", Description = "Log why each action runs (its stale reason)")]
    public bool Explain { get; set; }

    [Option("--watch", "-w", Description = "Rebuild whenever project files change (Ctrl+C to stop)")]
    public bool Watch { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        while (true)
        {
            var exit = await BuildOnceAsync(projectRoot, cancellationToken);

            if (!Watch || cancellationToken.IsCancellationRequested)
                return exit;

            Logger.LogInformation("Watching {Root} for changes... (Ctrl+C to stop)", projectRoot);
            var changed = await WaitForChangeAsync(projectRoot, cancellationToken);
            if (changed == null)
                return exit; // cancelled
            Logger.LogInformation("Changed: {Path}", Path.GetRelativePath(projectRoot, changed));
        }
    }

    /// <summary>One full build of the requested configurations (project config reloaded
    /// each time, so YAML edits take effect under --watch).</summary>
    private async Task<int> BuildOnceAsync(string projectRoot, CancellationToken cancellationToken)
    {
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
            var options = new BuildOptions
            {
                Parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount,
                Fingerprinter = Hash ? new ContentHashFingerprinter() : null,
                DryRun = DryRun,
                Explain = Explain,
            };
            if (!await GraphRunner.BuildAsync(config, toolchain, Logger, options, cancellationToken: cancellationToken))
                success = false;

            Logger.LogInformation("");
        }

        return success ? 0 : 1;
    }

    /// <summary>
    /// Waits for a relevant file change under the project root (build outputs,
    /// VCS internals, and editor temp files ignored), with a short debounce so a
    /// burst of saves triggers one rebuild. Null when cancelled.
    /// </summary>
    private static async Task<string?> WaitForChangeAsync(string root, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChange(string fullPath)
        {
            if (!IsIgnored(root, fullPath))
                tcs.TrySetResult(fullPath);
        }

        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        watcher.Changed += (_, e) => OnChange(e.FullPath);
        watcher.Created += (_, e) => OnChange(e.FullPath);
        watcher.Deleted += (_, e) => OnChange(e.FullPath);
        watcher.Renamed += (_, e) => OnChange(e.FullPath);
        watcher.EnableRaisingEvents = true;

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            var path = await tcs.Task;
            await Task.Delay(250, cancellationToken);   // debounce a burst of saves
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsIgnored(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        var sep = Path.DirectorySeparatorChar;
        return rel.StartsWith("out" + sep) || rel == "out"
            || rel.StartsWith(".git" + sep) || rel == ".git"
            || rel.Contains(sep + "obj" + sep) || rel.Contains(sep + "bin" + sep)
            || rel.EndsWith("~") || rel.EndsWith(".swp") || rel.EndsWith(".tmp");
    }
}
