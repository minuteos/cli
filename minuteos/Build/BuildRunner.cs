using Microsoft.Extensions.Logging;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Orchestrates the full build pipeline:
/// PreBuild steps -> Compile -> PreLink steps -> Link -> PostBuild steps
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
        // In quiet mode (per-suite test builds) only warnings/errors are logged.
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

        // Resolve steps by phase
        var stepsByPhase = ResolveSteps(config);

        // Build state accumulates contributions from steps
        var state = new BuildState();

        // === PreBuild phase ===
        if (!await RunStepsAsync(BuildPhase.PreBuild, stepsByPhase, config, state, cancellationToken, quiet))
            return false;

        // Merge step contributions into sources and defines
        var allSources = new List<SourceFile>(config.Sources);
        allSources.AddRange(state.GeneratedSources);

        var allDefines = new List<string>(config.Defines);
        allDefines.AddRange(state.ExtraDefines);

        if (allSources.Count == 0)
        {
            _logger.LogWarning("No source files found.");
            return false;
        }

        // === Precompiled header ===
        if (config.Pch != null)
        {
            Directory.CreateDirectory(config.ObjectDir);
            if (NeedsRebuild(config.Pch, config.PchGchFile))
            {
                Info("  precompiling {Pch}", Path.GetFileName(config.Pch));
                var pchResult = await _toolchain.CompilePchAsync(config, cancellationToken);
                if (!string.IsNullOrWhiteSpace(pchResult.StdErr))
                    _logger.LogWarning("{StdErr}", pchResult.StdErr.TrimEnd());
                if (!pchResult.Success)
                {
                    _logger.LogError("Failed to compile precompiled header: exit code {ExitCode}", pchResult.ExitCode);
                    return false;
                }
            }
        }

        // === Compile phase ===
        Info("Compiling {Count} files...", allSources.Count);

        var objectFiles = new List<string>();
        var compileTasks = new List<(SourceFile Source, string ObjectPath)>();

        foreach (var source in allSources)
        {
            var objPath = config.GetObjectPath(source);
            compileTasks.Add((source, objPath));
            objectFiles.Add(objPath);
        }

        // Add any extra objects from steps
        objectFiles.AddRange(state.ExtraObjects);

        var semaphore = new SemaphoreSlim(parallelism);
        var errors = new List<string>();

        var tasks = compileTasks.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.ObjectPath)!);

                if (!NeedsRebuild(item.Source.FullPath, item.ObjectPath))
                {
                    _logger.LogDebug("Skipping (up-to-date): {Source}", item.Source.RelativePath);
                    return;
                }

                Info("  {Compiler} -c {Source}",
                    item.Source.Language == SourceLanguage.C ? "gcc" : "g++",
                    item.Source.RelativePath);

                var result = await _toolchain.CompileAsync(item.Source, item.ObjectPath, config, cancellationToken);

                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    _logger.LogWarning("{StdErr}", result.StdErr.TrimEnd());

                if (!result.Success)
                {
                    lock (errors)
                        errors.Add($"Failed to compile {item.Source.RelativePath}: exit code {result.ExitCode}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                _logger.LogError("{Error}", error);
            return false;
        }

        // === PreLink phase ===
        if (!await RunStepsAsync(BuildPhase.PreLink, stepsByPhase, config, state, cancellationToken, quiet))
            return false;

        // === Link phase ===
        Info("");
        Info("Linking...");

        Directory.CreateDirectory(Path.GetDirectoryName(config.PrimaryOutput)!);

        var linkResult = await _toolchain.LinkAsync(
            objectFiles, config.PrimaryOutput, config,
            state.ExtraLinkFlags.Count > 0 ? state.ExtraLinkFlags : null,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(linkResult.StdErr))
            _logger.LogWarning("{StdErr}", linkResult.StdErr.TrimEnd());

        if (!linkResult.Success)
        {
            _logger.LogError("Linking failed with exit code {ExitCode}", linkResult.ExitCode);
            return false;
        }

        // === PostBuild phase ===
        if (!await RunStepsAsync(BuildPhase.PostBuild, stepsByPhase, config, state, cancellationToken, quiet))
            return false;

        Info("");
        Info("Build succeeded: {Output}", config.PrimaryOutput);
        return true;
    }

    private Dictionary<BuildPhase, List<(IBuildStep Step, StepReference Ref)>> ResolveSteps(BuildConfiguration config)
    {
        var result = new Dictionary<BuildPhase, List<(IBuildStep, StepReference)>>();

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
            {
                list = [];
                result[phase] = list;
            }
            list.Add((step, stepRef));
        }

        return result;
    }

    private async Task<bool> RunStepsAsync(
        BuildPhase phase,
        Dictionary<BuildPhase, List<(IBuildStep Step, StepReference Ref)>> stepsByPhase,
        BuildConfiguration config,
        BuildState state,
        CancellationToken cancellationToken,
        bool quiet = false)
    {
        if (!stepsByPhase.TryGetValue(phase, out var steps) || steps.Count == 0)
            return true;

        if (!quiet)
            _logger.LogInformation("[{Phase}]", phase);

        foreach (var (step, stepRef) in steps)
        {
            if (!quiet)
                _logger.LogInformation("  Running step: {Name}", step.Name);

            var context = new StepContext
            {
                Configuration = config,
                Toolchain = _toolchain,
                Logger = _logger,
                StepConfig = stepRef.Config ?? new(),
                State = state,
            };

            var result = await step.ExecuteAsync(context, cancellationToken);
            if (!result.Success)
            {
                _logger.LogError("Step '{Name}' failed: {Message}", step.Name, result.Message);
                return false;
            }

            if (result.Message != null)
                _logger.LogDebug("  {Message}", result.Message);
        }

        return true;
    }

    /// <summary>
    /// Checks if a source needs recompilation by comparing the object file's
    /// mtime against all dependencies listed in the .d file (generated by -MMD).
    /// Falls back to source-vs-object comparison if no .d file exists.
    /// </summary>
    private static bool NeedsRebuild(string sourcePath, string objectPath)
    {
        if (!File.Exists(objectPath))
            return true;

        var objectTime = File.GetLastWriteTimeUtc(objectPath);

        // Try to use the .d dependency file for accurate header tracking
        var depPath = DepFile.GetDepPath(objectPath);
        var deps = DepFile.Parse(depPath);

        if (deps != null)
        {
            foreach (var dep in deps)
            {
                if (!File.Exists(dep))
                    return true; // dependency deleted, must rebuild

                if (File.GetLastWriteTimeUtc(dep) > objectTime)
                    return true;
            }
            return false;
        }


        // Fallback: no .d file yet (first build), compare source directly
        return File.GetLastWriteTimeUtc(sourcePath) > objectTime;
    }
}
