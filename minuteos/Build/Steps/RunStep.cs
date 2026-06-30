namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Declares how to execute the linked image. This replaces the special top-level
/// <c>test-runner</c> field with an ordinary, target-overridable step: the default
/// runs the image directly (host), a target overrides it to launch an emulator.
///
///   # cortex-m3 target.yaml
///   steps:
///     - name: qemu               # or "run" / "renode"
///       phase: Run
///       config:
///         args: '-machine lm3s6965evb -nographic -semihosting -kernel "{image}"'
///         timeout: "30"
///
/// Registered under <c>run</c>, <c>qemu</c>, and <c>renode</c> (qemu/renode just
/// supply a default <c>command</c>). It is NOT part of the build pipeline - the
/// <c>Run</c> phase is excluded from building; <c>minuteos run</c>/<c>test</c>
/// resolve it via <see cref="RunSpecResolver"/> after the build. Its
/// <see cref="ExecuteAsync"/> is therefore a no-op.
///
/// Config: <c>command</c> (empty =&gt; run the image directly), <c>args</c> (a
/// shell-style string; tokens honor double quotes), <c>timeout</c> (seconds).
/// Placeholders in args: {image} / {binary} (the image path), {filter}.
/// </summary>
public class RunStep : IBuildStep
{
    public string Name { get; }
    public BuildPhase DefaultPhase => BuildPhase.Run;

    public RunStep(string name) => Name = name;

    public Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult.Ok());
}
