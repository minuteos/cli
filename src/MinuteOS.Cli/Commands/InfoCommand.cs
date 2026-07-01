using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("info", Description = "Show resolved project configuration")]
public class InfoCommand : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration name (omit to show all)")]
    public string Configuration { get; set; } = "";

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
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
            return Task.FromResult(1);
        }

        var configNames = Configuration != ""
            ? [Configuration]
            : projectConfig.ConfigurationNames.ToList();

        foreach (var configName in configNames)
        {
            BuildConfiguration config;
            try
            {
                config = BuildConfiguration.Create(projectConfig, configName, projectRoot);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to resolve '{Name}': {Message}", configName, ex.Message);
                continue;
            }

            Console.WriteLine($"=== {configName} ===");
            Console.WriteLine($"  Target:       {config.Target}");
            Console.WriteLine($"  Targets:      {string.Join(" -> ", config.Targets)}");
            Console.WriteLine($"  Config:       {config.Config}");
            Console.WriteLine($"  Output:       {config.PrimaryOutput}");
            Console.WriteLine($"  Toolchain:    {config.Profile.ToolchainPrefix ?? "(default)"}");
            Console.WriteLine($"  Components:   {string.Join(", ", config.Components)}");
            Console.WriteLine($"  Sources:      {config.Sources.Count} files");
            Console.WriteLine($"  Defines:      {string.Join(", ", config.Defines)}");

            if (config.Profile.ArchFlags is { Count: > 0 } archFlags)
                Console.WriteLine($"  Arch flags:   {string.Join(" ", archFlags)}");
            if (config.LdScript != null)
                Console.WriteLine($"  LD script:    {config.LdScript}");
            if (config.StepRefs.Count > 0)
                Console.WriteLine($"  Steps:        {string.Join(", ", config.StepRefs.Select(s => s.Name))}");

            Console.WriteLine("  Settings:");
            foreach (var (key, values) in config.Settings.All.OrderBy(kv => kv.Key))
                Console.WriteLine($"    {key}: {string.Join(" ", values)}");

            Console.WriteLine();
        }

        Console.WriteLine($"Project:    {projectConfig.Name}");
        Console.WriteLine($"Root:       {projectRoot}");

        var layout = new ProjectLayout(projectRoot, projectConfig.Name);
        Console.WriteLine($"Lib roots:  {string.Join(", ", layout.LibRoots.Select(r => Path.GetRelativePath(projectRoot, r)))}");

        return Task.FromResult(0);
    }
}
