namespace MinuteOS.Build.Steps;

/// <summary>
/// When a configured step runs, relative to the build. Retained as the YAML
/// vocabulary for <see cref="StepReference.Phase"/> (and to mark <c>Run</c>-phase
/// steps as out-of-graph). The task-graph engine orders steps by artifact
/// properties, not phases, so for graph steps this is advisory except for
/// distinguishing the <c>Run</c> phase.
/// </summary>
public enum BuildPhase
{
    /// <summary>Before compilation (e.g. source generation).</summary>
    PreBuild,

    /// <summary>After compilation, before linking.</summary>
    PreLink,

    /// <summary>After linking the primary output.</summary>
    PostBuild,

    /// <summary>
    /// Executes the linked image (host directly, or via an emulator). NOT part of
    /// the build; resolved out-of-band by <c>minuteos run</c> / <c>test</c>.
    /// </summary>
    Run,
}
