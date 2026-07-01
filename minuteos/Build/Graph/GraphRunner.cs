using Microsoft.Extensions.Logging;
using MinuteOS.Cli.Build.Graph.Steps;

namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// Drives a build through the task-graph <see cref="BuildEngine"/>: assembles the
/// native (gcc) bundle plus the project's configured steps and runs the graph.
/// This is the build engine for <c>build</c>/<c>run</c>/<c>test</c>.
/// </summary>
public static class GraphRunner
{
    public static async Task<bool> BuildAsync(
        BuildConfiguration config, Toolchain toolchain, ILogger logger,
        CancellationToken cancellationToken, int parallelism = 0, bool quiet = false,
        IFingerprinter? fingerprinter = null)
    {
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
            var graphStep = MapConfiguredStep(stepRef);
            if (graphStep != null)
                steps.Add(graphStep);
            else if (stepRef.Phase != MinuteOS.Cli.Build.Steps.BuildPhase.Run && !RunStepNames.Contains(stepRef.Name))
                logger.LogWarning("Step '{Name}' is not yet ported to the graph engine; skipping.", stepRef.Name);
        }

        var engine = new BuildEngine(toolchain, logger, parallelism, fingerprinter);
        var ok = await engine.RunAsync(steps, config, cancellationToken, quiet);

        if (ok && !quiet)
        {
            logger.LogInformation("");
            logger.LogInformation("Build succeeded: {Output}", config.PrimaryOutput);
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
