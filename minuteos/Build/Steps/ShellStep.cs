using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Runs an arbitrary command line through the system shell. This is the generic
/// extensibility primitive: any Make recipe can be expressed declaratively as a
/// shell step in component.yaml/target.yaml, e.g.
///
///   steps:
///     - name: shell
///       phase: PostBuild
///       config:
///         command: "{objcopy} -O binary {output} {output-base}.bin"
///
/// Placeholders:
///   {output}      - the primary output (e.g. .../app.axf)
///   {output-base} - the primary output without extension (.../app)
///   {output-dir}  - the output directory
///   {name}        - the output name
///   {objcopy} {objdump} {size} {cc} {cxx} - toolchain programs (with prefix)
/// </summary>
public class ShellStep : IBuildStep
{
    public string Name => "shell";
    public BuildPhase DefaultPhase => BuildPhase.PostBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        if (!context.StepConfig.TryGetValue("command", out var template) || string.IsNullOrWhiteSpace(template))
            return StepResult.Fail("shell step requires a 'command' in its config");

        var command = Substitute(template, context);
        context.Logger.LogInformation("  $ {Command}", command);

        var result = await context.Toolchain.RunShellAsync(
            command, context.Configuration.Layout.ProjectRoot, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.StdOut))
            context.Logger.LogDebug("{Out}", result.StdOut.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            context.Logger.LogWarning("{Err}", result.StdErr.TrimEnd());

        return result.Success
            ? StepResult.Ok()
            : StepResult.Fail($"command exited {result.ExitCode}: {command}");
    }

    private static string Substitute(string template, StepContext ctx)
    {
        var c = ctx.Configuration;
        var t = ctx.Toolchain;
        var outputBase = Path.Combine(c.OutputRoot, c.OutputName);
        return template
            .Replace("{output-base}", outputBase)
            .Replace("{output-dir}", c.OutputRoot)
            .Replace("{output}", c.PrimaryOutput)
            .Replace("{name}", c.OutputName)
            .Replace("{objcopy}", t.ObjCopy)
            .Replace("{objdump}", t.ObjDump)
            .Replace("{size}", t.Size)
            .Replace("{cc}", t.CC)
            .Replace("{cxx}", t.CXX);
    }
}
