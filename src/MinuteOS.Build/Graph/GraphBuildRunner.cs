using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph;

/// <summary>
/// DI-friendly <see cref="IBuildRunner"/>: constructs the gcc toolchain from the
/// configuration's settings and drives the task-graph engine (via
/// <see cref="GraphRunner"/>). Registered by <c>AddMinuteosBuild</c>.
/// </summary>
public sealed class GraphBuildRunner : IBuildRunner
{
    private readonly ILogger<GraphBuildRunner> _logger;
    private readonly IReadOnlyDictionary<string, IBuildStepFactory> _stepFactories;

    public GraphBuildRunner(ILogger<GraphBuildRunner> logger, IEnumerable<IBuildStepFactory> stepFactories)
    {
        _logger = logger;
        // Last registration for a name wins.
        _stepFactories = stepFactories.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.Last());
    }

    public Task<bool> BuildAsync(
        IBuildConfiguration config, BuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (config is not BuildConfiguration concrete)
            throw new ArgumentException(
                $"Expected a {nameof(BuildConfiguration)} from {nameof(BuildConfiguration)}.Create.", nameof(config));

        options ??= new BuildOptions();
        var toolchain = new Toolchain(config.Settings.Scalar("gcc.toolchain-prefix") ?? "", _logger);
        return GraphRunner.BuildAsync(concrete, toolchain, _logger, options, _stepFactories, cancellationToken);
    }
}
