using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Graph.Steps;

/// <summary>
/// Runs an arbitrary command through the system shell - the generic escape hatch.
/// Consumes the linked image so it orders after link (like the legacy PostBuild
/// default); declares no outputs and always runs (arbitrary side effects).
///
/// Config: <c>command</c>. Placeholders: {output} (the image), {output-base}
/// (without extension), {output-dir}, {name}, {objcopy} {objdump} {size} {cc} {cxx}.
/// </summary>
public sealed class ShellStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "shell";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        if (!config.TryGetValue("command", out var template) || string.IsNullOrWhiteSpace(template))
            yield break;

        var image = ctx.Inputs.FirstOrDefault();
        var imagePath = image?.Id ?? ctx.Config.PrimaryOutput;
        var c = ctx.Config;
        var t = ctx.Toolchain;
        var command = template
            .Replace("{output-base}", Path.Combine(c.OutputRoot, c.OutputName))
            .Replace("{output-dir}", c.OutputRoot)
            .Replace("{output}", imagePath)
            .Replace("{name}", c.OutputName)
            .Replace("{objcopy}", t.ObjCopy)
            .Replace("{objdump}", t.ObjDump)
            .Replace("{size}", t.Size)
            .Replace("{cc}", t.CC)
            .Replace("{cxx}", t.CXX);

        yield return new BuildAction($"shell: {command}", image != null ? [image] : [], [], async actx =>
        {
            if (!actx.Quiet)
                actx.Logger.LogInformation("  $ {Command}", command);
            var r = await actx.Toolchain.RunShellAsync(command, actx.ProjectRoot, actx.CancellationToken);
            if (!string.IsNullOrWhiteSpace(r.StdOut)) actx.Logger.LogDebug("{Out}", r.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(r.StdErr)) actx.Logger.LogWarning("{Err}", r.StdErr.TrimEnd());
            return r.Success ? ActionResult.Ok() : ActionResult.Fail($"command exited {r.ExitCode}: {command}");
        })
        {
            AlwaysRun = true,
        };
    }
}
