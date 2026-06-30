using Microsoft.Extensions.Logging;
using MinuteOS.Cli.Build.Graph.Steps;

namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// Drives a build through the task-graph <see cref="BuildEngine"/> using the native
/// (gcc) step bundle. Experimental — stage 1 covers the scan→compile→link path;
/// configured steps (objcopy, transpile, sub-build, …) port onto the same engine
/// next. Selected via <c>minuteos build --graph</c>.
/// </summary>
public static class GraphRunner
{
    public static async Task<bool> BuildAsync(
        BuildConfiguration config, Toolchain toolchain, ILogger logger,
        CancellationToken cancellationToken, bool quiet = false)
    {
        if (!quiet)
        {
            logger.LogInformation("Project:      {Name}", config.OutputName);
            logger.LogInformation("Target:       {Target}", config.Target);
            logger.LogInformation("Config:       {Config}", config.Config);
            logger.LogInformation("Output:       {Output}", config.PrimaryOutput);
            logger.LogInformation("Engine:       task-graph (experimental)");
            logger.LogInformation("");
        }

        var steps = new List<IGraphStep>
        {
            new GccScanStep(),
            new GccCompileStep(),
            new GccLinkStep(),
        };

        var engine = new BuildEngine(toolchain, logger);
        var ok = await engine.RunAsync(steps, config, cancellationToken, quiet);

        if (ok && !quiet)
        {
            logger.LogInformation("");
            logger.LogInformation("Build succeeded: {Output}", config.PrimaryOutput);
        }
        return ok;
    }
}
