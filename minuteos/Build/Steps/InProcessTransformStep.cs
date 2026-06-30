namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Base class for in-process source generators (run inside the builder, not as an
/// external CLI). A subclass implements <see cref="GenerateAsync"/>; the base
/// discovers inputs and registers the generated sources/headers back into the
/// build.
///
/// Unlike the external <see cref="TransformStep"/>, this runs the generator on
/// <b>every</b> build and hands it the <b>full</b> input set — a whole-program
/// transpiler needs all files for context. Dependency analysis is the generator's
/// job: it decides what (if anything) actually changed, and to keep the
/// downstream <c>.d</c>-based compile cache effective it should only rewrite the
/// outputs whose content changed (leaving unchanged files' timestamps intact).
/// <see cref="GenerateAsync"/> returns the generator's <b>complete</b> current
/// output set, which the base registers for compilation/linking.
/// </summary>
public abstract class InProcessTransformStep : IBuildStep
{
    public abstract string Name { get; }
    public virtual BuildPhase DefaultPhase => BuildPhase.PreBuild;

    /// <summary>Default value for the generated-subdir <c>id</c> (overridable in config).</summary>
    protected abstract string DefaultId { get; }

    /// <summary>Default input glob(s) this generator consumes (overridable in config).</summary>
    protected abstract string DefaultInputs { get; }

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var cfg = context.StepConfig;
        var id = cfg.GetValueOrDefault("id", DefaultId);
        var inputsPattern = cfg.TryGetValue("inputs", out var pat) && !string.IsNullOrWhiteSpace(pat)
            ? pat : DefaultInputs;

        var build = context.Configuration;
        var generatedDir = cfg.TryGetValue("generated-subdir", out var sub) && !string.IsNullOrWhiteSpace(sub)
            ? Path.Combine(build.OutputRoot, sub)
            : Path.Combine(build.OutputRoot, "generated", id);

        // Discover the FULL input set across all source dirs. The generator gets
        // everything — incrementality is its responsibility, not the step's.
        var inputs = TransformSupport.DiscoverInputs(build.SourceDirs, inputsPattern);
        if (inputs.Count == 0)
        {
            context.Logger.LogDebug("  {Name}[{Id}]: no inputs matching '{Pattern}'", Name, id, inputsPattern);
            return StepResult.Ok();
        }

        Directory.CreateDirectory(generatedDir);

        IReadOnlyList<string> outputs;
        try
        {
            outputs = await GenerateAsync(context, inputs, generatedDir, cancellationToken);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"{Name} '{id}' failed: {ex.Message}");
        }

        return TransformSupport.Register(context, outputs, generatedDir, id);
    }

    /// <summary>
    /// Translates <paramref name="inputs"/> (the complete input set) into compilable
    /// sources under <paramref name="generatedDir"/>. Returns the complete set of
    /// current output files (sources and headers). Should only rewrite outputs whose
    /// content changed so the downstream compile cache stays effective.
    /// </summary>
    protected abstract Task<IReadOnlyList<string>> GenerateAsync(
        StepContext context, IReadOnlyList<string> inputs, string generatedDir, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> only if it
    /// differs from what's already there, so unchanged outputs keep their mtime and
    /// the downstream compile is skipped. Returns true if the file was (re)written.
    /// </summary>
    protected static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }
}
