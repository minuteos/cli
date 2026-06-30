using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// The frozen step contract (docs/design/task-graph.md). A step declares what it
/// consumes/produces by artifact properties, and - once its inputs are
/// materialized - expands into concrete <see cref="BuildAction"/>s. The engine
/// knows no toolchain; all toolchain knowledge lives in the steps.
/// </summary>
public interface IGraphStep
{
    string Name { get; }

    StepSignature Signature { get; }

    /// <summary>
    /// Expand into concrete actions, given the materialized input artifacts
    /// (already matched to this step's selectors) and the resolved settings.
    /// </summary>
    IEnumerable<BuildAction> Plan(PlanContext context);
}

/// <summary>
/// Static declaration used to wire the step graph: the selectors a step consumes
/// and property-templates it produces (matched by kind for ordering).
/// </summary>
public sealed record StepSignature(
    IReadOnlyList<Selector> Consumes,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Produces)
{
    public static StepSignature Source(
        IEnumerable<Selector>? consumes, params (string Key, string Value)[][] produces) =>
        new(
            consumes?.ToList() ?? [],
            produces.Select(p => (IReadOnlyDictionary<string, string>)p.ToDictionary(x => x.Key, x => x.Value)).ToList());
}

/// <summary>Context handed to <see cref="IGraphStep.Plan"/>.</summary>
public sealed class PlanContext
{
    public required BuildConfiguration Config { get; init; }
    public required Settings Settings { get; init; }
    public required Toolchain Toolchain { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>The materialized artifacts matching this step's consume selectors.</summary>
    public required IReadOnlyList<Artifact> Inputs { get; init; }
}

/// <summary>Context handed to a <see cref="BuildAction"/> when it runs.</summary>
public sealed class ActionContext
{
    public required Toolchain Toolchain { get; init; }
    public required ILogger Logger { get; init; }
    public required string ProjectRoot { get; init; }
    public required bool Quiet { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// One concrete unit of work: declared input/output files plus a run function. The
/// engine schedules, fingerprints, and (later) parallelizes actions.
/// </summary>
public sealed record BuildAction(
    string Label,
    IReadOnlyList<Artifact> Inputs,
    IReadOnlyList<Artifact> Outputs,
    Func<ActionContext, Task<ActionResult>> Run)
{
    /// <summary>
    /// A fingerprint of config that isn't a file input (e.g. the compile command
    /// line). When it changes, the action is rebuilt even if inputs are unchanged.
    /// </summary>
    public string? ConfigKey { get; init; }

    /// <summary>
    /// Forces execution every build regardless of the cache. Used by dynamic-output
    /// steps (e.g. a whole-program transpiler) whose declared outputs aren't known
    /// until they run; they still record their produced set for orphan cleanup.
    /// </summary>
    public bool AlwaysRun { get; init; }
}

/// <summary>
/// Result of running an action. <see cref="DiscoveredInputs"/> carries depfile-style
/// dynamic deps; <see cref="ProducedArtifacts"/> overrides the declared outputs when
/// the produced set is dynamic (e.g. a transpiler).
/// </summary>
public sealed record ActionResult(
    bool Success,
    string? Message = null,
    IReadOnlyList<string>? DiscoveredInputs = null,
    IReadOnlyList<Artifact>? ProducedArtifacts = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? SettingsAdditions = null)
{
    public static ActionResult Ok(IReadOnlyList<Artifact>? produced = null) => new(true, ProducedArtifacts: produced);
    public static ActionResult Fail(string message) => new(false, message);
}
