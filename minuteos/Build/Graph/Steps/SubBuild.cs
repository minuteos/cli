using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Graph.Steps;

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
        var projectRoot = ctx.Config.Layout.ProjectRoot;
        var blobObj = Path.Combine(ctx.Config.ObjectDir, subName + ".blob.o");

        yield return new BuildAction($"sub-build:{subName}", [], [Artifact.File(blobObj, ("kind", "object"))], async actx =>
        {
            ProjectConfig projectConfig;
            BuildConfiguration sub;
            try
            {
                projectConfig = ProjectConfig.Load(projectRoot);
                sub = BuildConfiguration.Create(projectConfig, subName, projectRoot,
                    new BuildOverrides { OutputSubdir = Path.Combine("sub", subName) });
            }
            catch (Exception ex)
            {
                return ActionResult.Fail($"sub-build '{subName}': {ex.Message}");
            }

            if (!actx.Quiet)
                actx.Logger.LogInformation("  sub-build: {Config}", subName);

            // Nested graph build (has its own cache under out/<sub>/.cache).
            var subToolchain = new Toolchain(sub.Settings.Scalar("gcc.toolchain-prefix") ?? "", actx.Logger);
            if (!await GraphRunner.BuildAsync(sub, subToolchain, actx.Logger, actx.CancellationToken, quiet: true))
                return ActionResult.Fail($"sub-build '{subName}' failed");

            // Image -> raw binary -> blob object. Name the .bin after the sub-config
            // so the objcopy symbols are _binary_<config>_bin_start/_end/_size.
            var subBin = Path.Combine(Path.GetDirectoryName(sub.PrimaryOutput)!, subName + ".bin");
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
            AlwaysRun = true,
        };
    }
}
