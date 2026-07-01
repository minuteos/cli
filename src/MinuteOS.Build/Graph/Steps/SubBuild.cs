using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph.Steps;

/// <summary>
/// Builds another configuration from the same project (a nested graph) and embeds
/// its image as a linkable blob object - producing a <c>kind=object</c> artifact
/// that <c>gcc:link</c> picks up like any other object. The graph form of the
/// bootloader/embed pattern; "image becomes an object" is just a normal edge.
///
/// Config: <c>configuration</c> (sub config to build), <c>blob-section</c>
/// (default .rodata), <c>blob-format</c> (objcopy target, default elf32-littlearm),
/// <c>blob-arch</c> (default arm).
/// </summary>
public sealed class SubBuildStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "sub-build";

    public StepSignature Signature => StepSignature.Source(consumes: null, [("kind", "object")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        if (!config.TryGetValue("configuration", out var subName) || string.IsNullOrWhiteSpace(subName))
            yield break;
        if (subName == ctx.Config.Name)
            yield break;

        var section = config.GetValueOrDefault("blob-section", ".rodata");
        var format = config.GetValueOrDefault("blob-format", "elf32-littlearm");
        var arch = config.GetValueOrDefault("blob-arch", "arm");
        var projectRoot = ctx.Config.ProjectRoot;
        var blobObj = Path.Combine(ctx.Config.ObjectDir, subName + ".blob.o");

        BuildConfiguration? sub = null;
        string? loadError = null;
        try
        {
            sub = BuildConfiguration.Create(ProjectConfig.Load(projectRoot), subName, projectRoot,
                new BuildOverrides { OutputSubdir = Path.Combine("sub", subName) });
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        if (sub == null)
        {
            yield return new BuildAction($"sub-build:{subName}", [], [],
                _ => Task.FromResult(ActionResult.Fail($"sub-build '{subName}': {loadError}")));
            yield break;
        }

        var subImage = Artifact.File(sub.PrimaryOutput, ("kind", "sub-image"));
        var subBin = Path.Combine(Path.GetDirectoryName(sub.PrimaryOutput)!, subName + ".bin");

        // Action 1: build the sub-config as a nested graph (its own cache). Always
        // runs, but is cheap when the nested build is up to date.
        yield return new BuildAction($"sub-build:{subName}/build", [], [subImage], async actx =>
        {
            if (!actx.Quiet)
                actx.Logger.LogInformation("  sub-build: {Config}", subName);
            var subToolchain = new Toolchain(sub.Settings.Scalar("gcc.toolchain-prefix") ?? "", actx.Logger);
            if (!await GraphRunner.BuildAsync(sub, subToolchain, actx.Logger, actx.CancellationToken, quiet: true))
                return ActionResult.Fail($"sub-build '{subName}' failed");
            return ActionResult.Ok();
        })
        {
            AlwaysRun = true,
        };

        // Action 2: embed the image as a blob object. Cache-gated on the sub image,
        // so an unchanged sub-build doesn't re-objcopy or force a parent relink.
        // Naming the .bin after the sub-config gives clean _binary_<config>_bin_*
        // symbols.
        yield return new BuildAction($"sub-build:{subName}/embed", [subImage], [Artifact.File(blobObj, ("kind", "object"))], async actx =>
        {
            var toBin = await actx.Toolchain.RunToolAsync(actx.Toolchain.ObjCopy,
                ["-O", "binary", sub.PrimaryOutput, subBin], projectRoot, actx.CancellationToken);
            if (!toBin.Success)
                return ActionResult.Fail($"objcopy to binary failed: {toBin.StdErr.Trim()}");

            var toObj = await actx.Toolchain.RunToolAsync(actx.Toolchain.ObjCopy,
                ["-I", "binary", "-O", format, "-B", arch, "--rename-section", $".data={section}",
                 Path.GetFileName(subBin), blobObj],
                Path.GetDirectoryName(subBin)!, actx.CancellationToken);
            if (!toObj.Success)
                return ActionResult.Fail($"objcopy to object failed: {toObj.StdErr.Trim()}");

            if (!actx.Quiet)
                actx.Logger.LogInformation("  embedded {Config} as blob in section {Section}", subName, section);
            return ActionResult.Ok();
        })
        {
            ConfigKey = $"{format} {arch} {section}",
        };
    }
}
