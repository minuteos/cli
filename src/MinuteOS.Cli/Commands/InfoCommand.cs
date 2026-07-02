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

    [Option("--json", Description = "Emit the resolved debug/config model as JSON (machine-readable; consumed by minute-debug)")]
    public bool Json { get; set; }

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

        if (Json)
        {
            // The single source of truth for debug configuration: the extension
            // resolves `{ config: "<name>" }` launch configs by invoking this.
            var models = new System.Text.Json.Nodes.JsonArray();
            foreach (var configName in configNames)
                models.Add(MinuteDebugConfig.Describe(
                    BuildConfiguration.Create(projectConfig, configName, projectRoot)));
            var output = models.Count == 1 ? models[0]! : models;
            Console.WriteLine(output.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return Task.FromResult(0);
        }

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

            var sources = new SourceCollector().CollectSources(config.SourceDirs, projectRoot);

            Console.WriteLine($"=== {configName} ===");
            Console.WriteLine($"  Target:       {config.Target}");
            Console.WriteLine($"  Targets:      {string.Join(" -> ", config.Targets)}");
            Console.WriteLine($"  Config:       {config.Config}");
            Console.WriteLine($"  Output:       {config.PrimaryOutput}");
            Console.WriteLine($"  Toolchain:    {config.Settings.Scalar("gcc.toolchain-prefix") ?? "(default)"}");
            Console.WriteLine($"  Components:   {string.Join(", ", config.Components)}");
            Console.WriteLine($"  Sources:      {sources.Count} files");
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
