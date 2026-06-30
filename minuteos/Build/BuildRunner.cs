using Microsoft.Extensions.Logging;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Executes the build as an ordered pipeline of steps over a shared
/// <see cref="BuildState"/>. The default pipeline is:
///
///   [PreBuild ext steps] -> gcc:compile -> [PreLink ext steps] -> gcc:link -> [PostBuild ext steps]
///
/// Compile and link are themselves steps (<see cref="GccCompileStep"/> /
/// <see cref="GccLinkStep"/>) that read the toolchain-agnostic <see cref="Settings"/>
/// bag - the core no longer knows about GCC. Extension steps contributed by
/// components/targets/project slot in by their phase.
/// </summary>
public class BuildRunner
{
    private readonly Toolchain _toolchain;
    private readonly StepRegistry _stepRegistry;
    private readonly ILogger _logger;

    public BuildRunner(Toolchain toolchain, StepRegistry stepRegistry, ILogger logger)
    {
        _toolchain = toolchain;
        _stepRegistry = stepRegistry;
        _logger = logger;
    }

    public async Task<bool> BuildAsync(BuildConfiguration config, int parallelism, CancellationToken cancellationToken, bool quiet = false)
    {
        void Info(string message, params object[] args)
        {
            if (!quiet)
                _logger.LogInformation(message, args);
        }

        Info("Project:      {Name}", config.OutputName);
        Info("Target:       {Target}", config.Target);
        Info("Config:       {Config}", config.Config);
        Info("Components:   {Components}", string.Join(", ", config.Components));
        Info("Sources:      {Count} files", config.Sources.Count);
        Info("Output:       {Output}", config.PrimaryOutput);
        if (config.StepRefs.Count > 0)
            Info("Steps:        {Steps}", string.Join(", ", config.StepRefs.Select(s => s.Name)));
        Info("");

        var state = new BuildState();

        // Assemble the ordered pipeline: extension steps by phase, with the
        // built-in gcc compile/link steps at their fixed positions.
        var byPhase = ResolveSteps(config);
        var pipeline = new List<(IBuildStep Step, StepReference? Ref)>();
        pipeline.AddRange(byPhase.GetValueOrDefault(BuildPhase.PreBuild, []));
        pipeline.Add((new GccCompileStep(), null));
        pipeline.AddRange(byPhase.GetValueOrDefault(BuildPhase.PreLink, []));
        pipeline.Add((new GccLinkStep(), null));
        pipeline.AddRange(byPhase.GetValueOrDefault(BuildPhase.PostBuild, []));

        foreach (var (step, stepRef) in pipeline)
        {
            // Built-in gcc steps log their own progress; announce extension steps.
            if (stepRef != null && !quiet)
                _logger.LogInformation("  Running step: {Name}", step.Name);

            var context = new StepContext
            {
                Configuration = config,
                Toolchain = _toolchain,
                Logger = _logger,
                StepConfig = stepRef?.Config ?? new(),
                State = state,
                Parallelism = parallelism,
                Quiet = quiet,
            };

            var result = await step.ExecuteAsync(context, cancellationToken);
            if (!result.Success)
            {
                if (result.Message != null)
                    _logger.LogError("Step '{Name}' failed: {Message}", step.Name, result.Message);
                return false;
            }

            if (result.Message != null)
                _logger.LogDebug("  {Message}", result.Message);
        }

        Info("");
        Info("Build succeeded: {Output}", config.PrimaryOutput);
        return true;
    }

    private Dictionary<BuildPhase, List<(IBuildStep Step, StepReference? Ref)>> ResolveSteps(BuildConfiguration config)
    {
        var result = new Dictionary<BuildPhase, List<(IBuildStep, StepReference?)>>();

        foreach (var stepRef in config.StepRefs)
        {
            var step = _stepRegistry.Get(stepRef.Name);
            if (step == null)
            {
                _logger.LogWarning("Unknown build step '{Name}', skipping", stepRef.Name);
                continue;
            }

            var phase = stepRef.Phase ?? step.DefaultPhase;
            if (!result.TryGetValue(phase, out var list))
                result[phase] = list = [];
            list.Add((step, stepRef));
        }

        return result;
    }
}
