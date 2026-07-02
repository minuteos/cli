using Microsoft.Extensions.Logging;
using MinuteOS.Build.Graph.Steps;

namespace MinuteOS.Build.Graph;

/// <summary>
/// Drives a build through the task-graph <see cref="BuildEngine"/>: assembles the
/// native (gcc) bundle plus the project's configured steps and runs the graph.
/// This is the build engine for <c>build</c>/<c>run</c>/<c>test</c>.
/// </summary>
public static class GraphRunner
{
    public static async Task<bool> BuildAsync(
        BuildConfiguration config, Toolchain toolchain, ILogger logger,
        BuildOptions? options = null,
        IReadOnlyDictionary<string, IBuildStepFactory>? stepFactories = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BuildOptions();
        var quiet = options.Quiet;

        if (!quiet)
        {
            logger.LogInformation("Project:      {Name}", config.OutputName);
            logger.LogInformation("Target:       {Target}", config.Target);
            logger.LogInformation("Config:       {Config}", config.Config);
            logger.LogInformation("Output:       {Output}", config.PrimaryOutput);
            logger.LogInformation("");
        }

        var steps = new List<IGraphStep>
        {
            new GccScanStep(),
            new CsScanStep(),
            new GccCompileStep(),
            new GccLinkStep(),
        };

        // Map the project's configured steps to graph steps. Ordering is by
        // artifact properties, so phases are ignored; Run-phase steps are
        // out-of-graph (run/test invoke them). Not-yet-ported steps are skipped.
        foreach (var stepRef in config.StepRefs)
        {
            var cfg = stepRef.Config ?? new Dictionary<string, string>();
            // Consumer-registered steps win over the built-ins, letting a project
            // override or extend the catalog.
            var graphStep = stepFactories != null && stepFactories.TryGetValue(stepRef.Name, out var factory)
                ? factory.Create(cfg)
                : MapConfiguredStep(stepRef);
            if (graphStep != null)
                steps.Add(graphStep);
            else if (stepRef.Phase != MinuteOS.Build.Steps.BuildPhase.Run && !RunStepNames.Contains(stepRef.Name))
                logger.LogWarning("Unknown build step '{Name}'; skipping.", stepRef.Name);
        }

        var engine = new BuildEngine(toolchain, logger, options);
        var ok = await engine.RunAsync(steps, config, cancellationToken, quiet);

        if (ok && !quiet)
        {
            logger.LogInformation("");
            logger.LogInformation(options.DryRun
                ? "Dry run complete (nothing was built)."
                : "Build succeeded: {Output}", config.PrimaryOutput);
        }
        return ok;
    }

    private static readonly HashSet<string> RunStepNames =
        new(StringComparer.OrdinalIgnoreCase) { "run", "qemu", "renode", "exec" };

    /// <summary>Maps a configured step reference to a graph step, or null if N/A.</summary>
    private static IGraphStep? MapConfiguredStep(StepReference stepRef)
    {
        var cfg = stepRef.Config ?? new Dictionary<string, string>();
        return stepRef.Name.ToLowerInvariant() switch
        {
            "gcc:objcopy" or "binary-output" => new GccObjcopyStep(cfg),
            "disassembly" => new DisassemblyStep(),
            "size" or "size-report" => new SizeStep(),
            "transpile" => new TranspileStep(),
            "transform" => new TransformStep(cfg),
            "sub-build" => new SubBuildStep(cfg),
            "git-version" => new GitVersionStep(cfg),
            "shell" => new ShellStep(cfg),
            _ => null,
        };
    }
}
