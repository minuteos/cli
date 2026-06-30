using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// The toolchain-agnostic core: wires steps into a graph by property match,
/// topologically orders them, and runs their actions. Stage 1 is sequential with a
/// per-action up-to-date check; parallelism and a fingerprint cache layer on later
/// behind the same step contract (docs/design/task-graph.md).
/// </summary>
public sealed class BuildEngine
{
    private readonly Toolchain _toolchain;
    private readonly ILogger _logger;
    private readonly int _parallelism;

    public BuildEngine(Toolchain toolchain, ILogger logger, int parallelism = 0)
    {
        _toolchain = toolchain;
        _logger = logger;
        _parallelism = parallelism > 0 ? parallelism : Environment.ProcessorCount;
    }

    public async Task<bool> RunAsync(
        IReadOnlyList<IGraphStep> steps,
        BuildConfiguration config,
        CancellationToken cancellationToken,
        bool quiet = false)
    {
        var ordered = TopologicalOrder(steps);
        var cache = BuildCache.Load(Path.Combine(config.OutputRoot, ".cache"));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The artifact pool grows as steps run; each step consumes what matches its
        // selectors and contributes its outputs (lazy fan-out - Plan runs only once
        // the step's inputs are materialized).
        var pool = new List<Artifact>();

        foreach (var step in ordered)
        {
            var inputs = pool.Where(a => step.Signature.Consumes.Any(a.Matches)).ToList();

            var planContext = new PlanContext
            {
                Config = config,
                Settings = config.Settings,
                Toolchain = _toolchain,
                Logger = _logger,
                Inputs = inputs,
            };

            List<BuildAction> actions;
            try
            {
                actions = step.Plan(planContext).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError("Step '{Step}' failed to plan: {Message}", step.Name, ex.Message);
                return false;
            }

            var actionContext = new ActionContext
            {
                Toolchain = _toolchain,
                Logger = _logger,
                ProjectRoot = config.Layout.ProjectRoot,
                Quiet = quiet,
                CancellationToken = cancellationToken,
            };

            if (!await RunStepActionsAsync(actions, pool, cache, seen, actionContext))
                return false;
        }

        // Drop vanished actions, delete their orphaned outputs, persist the cache.
        cache.RetainOnly(seen);
        foreach (var orphan in cache.Orphans())
        {
            try { File.Delete(orphan); _logger.LogDebug("Removed orphan {File}", orphan); }
            catch { /* best effort */ }
        }
        cache.Save();

        return true;
    }

    /// <summary>
    /// Runs a step's actions in dependency waves: actions whose intra-step inputs
    /// are all satisfied run together, capped at the parallelism limit. This keeps
    /// ordering (e.g. PCH before the compiles that -include it) while running the
    /// independent compiles concurrently. Cache checks and pool/cache mutation
    /// happen single-threaded between waves; only the action Run bodies run in
    /// parallel, so no shared state is touched concurrently.
    /// </summary>
    private async Task<bool> RunStepActionsAsync(
        List<BuildAction> actions, List<Artifact> pool, BuildCache cache,
        HashSet<string> seen, ActionContext actionContext)
    {
        // Intra-step producer map: which action in this step produces each artifact.
        var producedHere = new Dictionary<string, BuildAction>(StringComparer.Ordinal);
        foreach (var a in actions)
            foreach (var o in a.Outputs)
                producedHere[o.Id] = a;

        var pending = new List<BuildAction>(actions);
        var done = new HashSet<BuildAction>();

        while (pending.Count > 0)
        {
            // Ready = actions whose intra-step dependencies are all done.
            var ready = pending.Where(a => a.Inputs.All(i =>
                !producedHere.TryGetValue(i.Id, out var dep) || done.Contains(dep))).ToList();

            if (ready.Count == 0)
                throw new InvalidOperationException("Cycle among a step's actions.");

            // Cache check (single-threaded); collect the ones that actually run.
            var toRun = new List<BuildAction>();
            foreach (var action in ready)
            {
                seen.Add(action.Label);
                if (!action.AlwaysRun && cache.IsUpToDate(action, action.ConfigKey))
                {
                    pool.AddRange(action.Outputs);
                    continue;
                }
                foreach (var output in action.Outputs)
                    EnsureDirectory(output.Id);
                toRun.Add(action);
            }

            // Run this wave concurrently (only the Run bodies; cap concurrency).
            using var gate = new SemaphoreSlim(_parallelism);
            var results = await Task.WhenAll(toRun.Select(async action =>
            {
                await gate.WaitAsync(actionContext.CancellationToken);
                try { return (action, result: await SafeRun(action, actionContext)); }
                finally { gate.Release(); }
            }));

            // Apply results (single-threaded): fail fast, record, publish.
            foreach (var (action, result) in results)
            {
                if (result == null || !result.Success)
                {
                    _logger.LogError("Action '{Label}' failed: {Message}", action.Label, result?.Message ?? "threw");
                    return false;
                }
                cache.Record(action, result, action.ConfigKey);
                pool.AddRange(result.ProducedArtifacts ?? action.Outputs);
            }

            foreach (var a in ready)
                done.Add(a);
            pending.RemoveAll(done.Contains);
        }

        return true;
    }

    private async Task<ActionResult?> SafeRun(BuildAction action, ActionContext ctx)
    {
        try { return await action.Run(ctx); }
        catch (Exception ex)
        {
            _logger.LogError("Action '{Label}' threw: {Message}", action.Label, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Orders steps so that every producer precedes a consumer of its output kind.
    /// An edge A→B exists when some B selector matches some A produce-template.
    /// </summary>
    private static List<IGraphStep> TopologicalOrder(IReadOnlyList<IGraphStep> steps)
    {
        var incoming = steps.ToDictionary(s => s, _ => 0);
        var edges = steps.ToDictionary(s => s, _ => new List<IGraphStep>());

        foreach (var producer in steps)
        foreach (var consumer in steps)
        {
            if (ReferenceEquals(producer, consumer))
                continue;
            if (Produces(producer, consumer))
            {
                edges[producer].Add(consumer);
                incoming[consumer]++;
            }
        }

        // Kahn's algorithm; ties broken by original order for determinism.
        var ready = new Queue<IGraphStep>(steps.Where(s => incoming[s] == 0));
        var ordered = new List<IGraphStep>();
        while (ready.Count > 0)
        {
            var step = ready.Dequeue();
            ordered.Add(step);
            foreach (var next in edges[step])
                if (--incoming[next] == 0)
                    ready.Enqueue(next);
        }

        if (ordered.Count != steps.Count)
            throw new InvalidOperationException("Build step graph has a cycle.");

        return ordered;
    }

    /// <summary>True when any consumer selector matches any producer output template.</summary>
    private static bool Produces(IGraphStep producer, IGraphStep consumer) =>
        consumer.Signature.Consumes.Any(selector =>
            producer.Signature.Produces.Any(template =>
                selector.Required.All(kv => template.TryGetValue(kv.Key, out var v) && v == kv.Value)));

    private static bool IsFile(string id) => !id.Contains(':') || id.Length > 1 && id[1] == ':'; // path, not "value:..."

    private static void EnsureDirectory(string id)
    {
        if (!IsFile(id))
            return;
        var dir = Path.GetDirectoryName(id);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
