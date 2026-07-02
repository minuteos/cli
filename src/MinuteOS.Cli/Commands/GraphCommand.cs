using MinuteOS.Build;
using MinuteOS.Build.Graph;
using MinuteOS.Build.Steps;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("graph", Description = "Show the build step graph for a configuration")]
public class GraphCommand : LoggingCommand
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

            var steps = GraphRunner.AssembleSteps(config, Logger);
            var ordered = BuildEngine.Order(steps);
            var edges = BuildEngine.Edges(steps).ToLookup(e => e.Producer, e => e.Consumer);
            var augmenters = ordered.Where(BuildEngine.IsSettingsAugmenter).ToList();

            Console.WriteLine($"=== {configName} ===");
            Console.WriteLine();
            Console.WriteLine("Steps (execution order):");
            foreach (var step in ordered)
            {
                var role = BuildEngine.IsSettingsAugmenter(step) ? "  [settings augmenter]" : "";
                Console.WriteLine($"  {step.Name,-16}{Describe(step.Signature)}{role}");
                foreach (var consumer in edges[step])
                    Console.WriteLine($"    {"",-14}-> {consumer.Name}");
            }

            var runSteps = config.StepRefs
                .Where(s => s.Phase == BuildPhase.Run || GraphRunner.RunStepNames.Contains(s.Name))
                .Select(s => s.Name)
                .Distinct()
                .ToList();
            if (runSteps.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Run steps (out of graph, resolved by run/test): {string.Join(", ", runSteps)}");
            }

            Console.WriteLine();
        }

        return Task.FromResult(0);
    }

    private static string Describe(StepSignature sig)
    {
        var consumes = string.Join(" | ", sig.Consumes.Select(s =>
            string.Join(",", s.Required.Select(kv => $"{kv.Key}={kv.Value}"))));
        var produces = string.Join(" | ", sig.Produces.Select(p =>
            string.Join(",", p.Select(kv => $"{kv.Key}={kv.Value}"))));

        return (consumes, produces) switch
        {
            ("", "") => "",
            ("", _) => $"produces {produces}",
            (_, "") => $"consumes {consumes}",
            _ => $"consumes {consumes}  ->  produces {produces}",
        };
    }
}
