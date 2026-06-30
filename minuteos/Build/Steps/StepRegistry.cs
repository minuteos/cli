namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Registry of available build steps.
/// Built-in steps are registered at startup.
/// Future: dynamically loaded from component C# sources.
/// </summary>
public class StepRegistry
{
    private readonly Dictionary<string, IBuildStep> _steps = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IBuildStep step)
    {
        _steps[step.Name] = step;
    }

    public IBuildStep? Get(string name)
    {
        _steps.TryGetValue(name, out var step);
        return step;
    }

    public IEnumerable<IBuildStep> All => _steps.Values;

    /// <summary>
    /// Creates a registry with all built-in steps registered.
    /// </summary>
    public static StepRegistry CreateDefault()
    {
        var registry = new StepRegistry();
        registry.Register(new GitVersionStep());
        registry.Register(new DisassemblyStep());
        registry.Register(new BinaryOutputStep());
        registry.Register(new SizeReportStep());
        registry.Register(new ShellStep());
        registry.Register(new SubBuildStep());
        registry.Register(new TransformStep());
        registry.Register(new TranspileStep());
        // Run-phase steps (resolved out-of-band by run/test, no-op during build).
        registry.Register(new RunStep("run"));
        registry.Register(new RunStep("qemu"));
        registry.Register(new RunStep("renode"));
        return registry;
    }
}
