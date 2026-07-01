namespace MinuteOS.Build.Graph;

/// <summary>
/// Extension point: creates a graph step for a configured step of a given
/// <see cref="Name"/> (the name used in a project's <c>steps:</c> list). Register
/// implementations in DI and they become available to the build runner alongside
/// the built-in steps:
///
/// <code>
/// services.AddMinuteosBuild()
///         .AddSingleton&lt;IBuildStepFactory, MyStepFactory&gt;();
/// </code>
///
/// The per-reference <c>config</c> map is the step's YAML <c>config:</c> block.
/// </summary>
public interface IBuildStepFactory
{
    /// <summary>The step name matched against a project's <c>steps: - name:</c>.</summary>
    string Name { get; }

    /// <summary>Creates the step instance for one configured reference.</summary>
    IGraphStep Create(IReadOnlyDictionary<string, string> config);
}
