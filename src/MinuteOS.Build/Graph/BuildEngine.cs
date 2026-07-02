using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph;

/// <summary>
/// The toolchain-agnostic core: wires steps into a graph by property match,
/// topologically orders them, and runs their actions in dependency waves with a
/// fingerprint cache (docs/design/task-graph.md). Supports dry-run (report what
/// would run, execute nothing) and explain (log each action's stale reason).
/// </summary>
public sealed class BuildEngine
{
    private readonly Toolchain _toolchain;
    private readonly ILogger _logger;
    private readonly int _parallelism;
    private readonly IFingerprinter _fingerprinter;
    private readonly bool _dryRun;
    private readonly bool _explain;
    private readonly object _sync = new(); // guards pool / cache / seen across concurrent steps

    // Dry-run only: outputs of actions that would run, so downstream actions can be
    // reported as stale-by-dependency even though those outputs weren't rebuilt.
    private readonly HashSet<string> _wouldChange = new(StringComparer.Ordinal);

    public BuildEngine(Toolchain toolchain, ILogger logger, BuildOptions? options = null)
    {
        options ??= new BuildOptions();
        _toolchain = toolchain;
        _logger = logger;
        _parallelism = options.Parallelism > 0 ? options.Parallelism : Environment.ProcessorCount;
        _fingerprinter = options.Fingerprinter ?? new MtimeSizeFingerprinter();
        _dryRun = options.DryRun;
        _explain = options.Explain;
    }

    public async Task<bool> RunAsync(
        IReadOnlyList<IGraphStep> steps,
        BuildConfiguration config,
        CancellationToken cancellationToken,
        bool quiet = false)
    {
        var ordered = TopologicalOrder(steps);
        var cache = BuildCache.Load(Path.Combine(config.OutputRoot, ".cache"), _fingerprinter);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var actionContext = new ActionContext
        {
            Toolchain = _toolchain,
            Logger = _logger,
            ProjectRoot = config.ProjectRoot,
            Quiet = quiet,
            CancellationToken = cancellationToken,
        };

        // Settings is an ambient artifact: augmenter steps (those producing
        // kind=settings, e.g. git-version) run first; each yields a new bag with
        // their additions merged, so every reader's Plan sees the fully-merged
        // settings. The bag stays immutable - safe to share across parallel actions.
        var settings = config.Settings;
        foreach (var augmenter in ordered.Where(IsAugmenter))
        {
            var augmented = await RunAugmenterAsync(augmenter, config, settings, seen, actionContext);
            if (augmented == null)
                return false;
            settings = augmented;
        }

        // The artifact pool grows as steps run; each step consumes what matches its
        // selectors and contributes its outputs (lazy fan-out - Plan runs only once
        // the step's inputs are materialized).
        var pool = new List<Artifact>();

        // Step-level scheduling in dependency waves: independent steps (e.g.
        // disassembly/objcopy/size after link, or a sub-build alongside compiles)
        // run concurrently. A shared gate caps total concurrent actions; shared
        // state (pool/cache/seen) is mutated under a lock.
        var mainSteps = ordered.Where(s => !IsAugmenter(s)).ToList();
        var indegree = mainSteps.ToDictionary(s => s, _ => 0);
        var successors = mainSteps.ToDictionary(s => s, _ => new List<IGraphStep>());
        foreach (var producer in mainSteps)
            foreach (var consumer in mainSteps)
                if (!ReferenceEquals(producer, consumer) && Produces(producer, consumer))
                {
                    successors[producer].Add(consumer);
                    indegree[consumer]++;
                }

        using var actionGate = new SemaphoreSlim(_parallelism);
        var stepDone = new HashSet<IGraphStep>();
        while (stepDone.Count < mainSteps.Count)
        {
            var wave = mainSteps.Where(s => !stepDone.Contains(s) && indegree[s] == 0).ToList();
            if (wave.Count == 0)
                throw new InvalidOperationException("Build step graph has a cycle.");

            var results = await Task.WhenAll(wave.Select(step =>
                RunStepAsync(step, config, settings, pool, cache, seen, actionContext, actionGate)));
            if (results.Any(ok => !ok))
                return false;

            foreach (var step in wave)
            {
                stepDone.Add(step);
                foreach (var consumer in successors[step])
                    indegree[consumer]--;
            }
        }

        // Drop vanished actions, delete their orphaned outputs, persist the cache.
        // A dry run must leave the cache and outputs untouched.
        if (!_dryRun)
        {
            cache.RetainOnly(seen);
            foreach (var orphan in cache.Orphans())
            {
                try { File.Delete(orphan); _logger.LogDebug("Removed orphan {File}", orphan); }
                catch { /* best effort */ }
            }
            cache.Save();
        }

        return true;
    }

    /// <summary>
    /// Plans a step (snapshotting the pool for its inputs under the lock) and runs
    /// its actions. Called concurrently for independent steps in a wave.
    /// </summary>
    private async Task<bool> RunStepAsync(
        IGraphStep step, BuildConfiguration config, Settings settings, List<Artifact> pool,
        BuildCache cache, HashSet<string> seen, ActionContext actionContext, SemaphoreSlim gate)
    {
        List<Artifact> inputs;
        lock (_sync)
            inputs = pool.Where(a => step.Signature.Consumes.Any(a.Matches)).ToList();

        var planContext = new PlanContext
        {
            Config = config,
            Settings = settings,
            Toolchain = _toolchain,
            Logger = _logger,
            Inputs = inputs,
        };

        List<BuildAction> actions;
        try { actions = step.Plan(planContext).ToList(); }
        catch (Exception ex)
        {
            _logger.LogError("Step '{Step}' failed to plan: {Message}", step.Name, ex.Message);
            return false;
        }

        return await RunStepActionsAsync(actions, pool, cache, seen, actionContext, gate);
    }

    /// <summary>
    /// Runs a step's actions in dependency waves: actions whose intra-step inputs
    /// are all satisfied run together, capped by the shared <paramref name="gate"/>.
    /// This keeps ordering (e.g. PCH before the compiles that -include it) while
    /// running independent compiles concurrently. Only the action Run bodies run in
    /// parallel; cache/pool/seen mutations are serialized under the lock, so this is
    /// safe to call concurrently for independent steps.
    /// </summary>
    private async Task<bool> RunStepActionsAsync(
        List<BuildAction> actions, List<Artifact> pool, BuildCache cache,
        HashSet<string> seen, ActionContext actionContext, SemaphoreSlim gate)
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

            // Cache check (under lock); collect the ones that actually run.
            var toRun = new List<BuildAction>();
            lock (_sync)
            {
                foreach (var action in ready)
                {
                    seen.Add(action.Label);
                    var reason = action.AlwaysRun ? "always runs" : cache.GetStaleReason(action, action.ConfigKey);
                    if (_dryRun && reason == null)
                    {
                        // Up to date per the cache, but stale if an input would be
                        // rebuilt by an upstream action this dry run reported.
                        var dep = action.Inputs.FirstOrDefault(i => _wouldChange.Contains(i.Id));
                        if (dep != null)
                            reason = $"depends on rebuilt {Path.GetFileName(dep.Id)}";
                    }
                    if (reason == null)
                    {
                        pool.AddRange(action.Outputs);
                        continue;
                    }
                    if (_dryRun)
                    {
                        // Report; publish declared outputs so downstream steps still
                        // plan (dynamic outputs can't be predicted without running).
                        _logger.LogInformation("would run: {Label}  ({Reason})", action.Label, reason);
                        // AlwaysRun actions (scans, report writers) republish rather
                        // than change their outputs - don't propagate through them.
                        if (!action.AlwaysRun)
                            foreach (var o in action.Outputs)
                                _wouldChange.Add(o.Id);
                        pool.AddRange(action.Outputs);
                        continue;
                    }
                    if (_explain)
                        _logger.LogInformation("  {Label}: {Reason}", action.Label, reason);
                    foreach (var output in action.Outputs)
                        EnsureDirectory(output.Id);
                    toRun.Add(action);
                }
            }

            // Run this wave concurrently (only the Run bodies; shared concurrency cap).
            var results = await Task.WhenAll(toRun.Select(async action =>
            {
                await gate.WaitAsync(actionContext.CancellationToken);
                try { return (action, result: await SafeRun(action, actionContext)); }
                finally { gate.Release(); }
            }));

            // Apply results (under lock): fail fast, record, publish.
            lock (_sync)
            {
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
            }

            foreach (var a in ready)
                done.Add(a);
            pending.RemoveAll(done.Contains);
        }

        return true;
    }

    /// <summary>A settings augmenter produces <c>kind=settings</c> (e.g. git-version).</summary>
    private static bool IsAugmenter(IGraphStep step) =>
        step.Signature.Produces.Any(p => p.GetValueOrDefault("kind") == "settings");

    /// <summary>
    /// Runs an augmenter's actions and merges their <see cref="ActionResult.SettingsAdditions"/>
    /// into the working bag. Augmenters always run (their contribution, e.g. a git
    /// hash, can change every build) and don't participate in the artifact pool.
    /// </summary>
    private async Task<Settings?> RunAugmenterAsync(
        IGraphStep step, BuildConfiguration config, Settings working, HashSet<string> seen, ActionContext actionContext)
    {
        var planContext = new PlanContext
        {
            Config = config,
            Settings = working,
            Toolchain = _toolchain,
            Logger = _logger,
            Inputs = [],
        };

        List<BuildAction> actions;
        try { actions = step.Plan(planContext).ToList(); }
        catch (Exception ex)
        {
            _logger.LogError("Augmenter '{Step}' failed to plan: {Message}", step.Name, ex.Message);
            return null;
        }

        foreach (var action in actions)
        {
            seen.Add(action.Label);
            var result = await SafeRun(action, actionContext);
            if (result == null || !result.Success)
            {
                _logger.LogError("Augmenter '{Label}' failed: {Message}", action.Label, result?.Message ?? "threw");
                return null;
            }
            if (result.SettingsAdditions != null)
                working = working.With(result.SettingsAdditions);
        }
        return working;
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

    /// <summary>Steps in execution order (for inspection, e.g. `minuteos graph`).</summary>
    public static IReadOnlyList<IGraphStep> Order(IReadOnlyList<IGraphStep> steps) =>
        TopologicalOrder(steps);

    /// <summary>The wired producer→consumer edges (for inspection).</summary>
    public static IEnumerable<(IGraphStep Producer, IGraphStep Consumer)> Edges(IReadOnlyList<IGraphStep> steps) =>
        from p in steps
        from c in steps
        where !ReferenceEquals(p, c) && Produces(p, c)
        select (p, c);

    /// <summary>A settings augmenter (runs before readers; see the engine docs).</summary>
    public static bool IsSettingsAugmenter(IGraphStep step) => IsAugmenter(step);

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
