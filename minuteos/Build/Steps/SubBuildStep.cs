using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Builds another configuration from the same minuteos.yaml and links its output
/// into the current build as a binary "blob" object. This is the declarative
/// replacement for the lib-arm bootloader pattern (a separate image embedded in
/// the main one) and the general "build artifact A, embed it into B" case.
///
///   steps:
///     - name: sub-build
///       phase: PreLink
///       config:
///         configuration: bootloader   # another config to build
///         blob-section: .binboot      # section to place the blob in
///         blob-format: elf32-littlearm # objcopy output target (default ARM)
///         blob-arch: arm
///
/// The blob is exposed by the usual objcopy symbols
/// (_binary_..._start/_end/_size).
/// </summary>
public class SubBuildStep : IBuildStep
{
    public string Name => "sub-build";
    public BuildPhase DefaultPhase => BuildPhase.PreLink;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        if (!context.StepConfig.TryGetValue("configuration", out var subName) || string.IsNullOrWhiteSpace(subName))
            return StepResult.Fail("sub-build requires a 'configuration' to build");
        if (subName == context.Configuration.Name)
            return StepResult.Fail("sub-build configuration cannot be itself");

        var projectRoot = context.Configuration.Layout.ProjectRoot;

        ProjectConfig projectConfig;
        try
        {
            projectConfig = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            return StepResult.Fail(ex.Message);
        }

        BuildConfiguration sub;
        try
        {
            sub = BuildConfiguration.Create(projectConfig, subName, projectRoot,
                new BuildOverrides { OutputSubdir = Path.Combine("sub", subName) });
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"sub-build '{subName}': {ex.Message}");
        }

        context.Logger.LogInformation("  sub-build: {Config}", subName);
        var toolchain = new Toolchain(sub.Profile.ToolchainPrefix ?? "", context.Logger);
        var runner = new BuildRunner(toolchain, StepRegistry.CreateDefault(), context.Logger);
        if (!await runner.BuildAsync(sub, Environment.ProcessorCount, cancellationToken, quiet: true))
            return StepResult.Fail($"sub-build '{subName}' failed");

        // Convert the sub-build's primary output into a linkable blob object.
        var section = context.StepConfig.GetValueOrDefault("blob-section", ".rodata");
        var format = context.StepConfig.GetValueOrDefault("blob-format", "elf32-littlearm");
        var arch = context.StepConfig.GetValueOrDefault("blob-arch", "arm");

        // Name the binary after the sub-config so the objcopy symbols are
        // _binary_<config>_bin_start/_end/_size (e.g. _binary_loader_bin_start).
        var subBin = Path.Combine(Path.GetDirectoryName(sub.PrimaryOutput)!, subName + ".bin");
        var blobObj = Path.Combine(context.Configuration.ObjectDir, subName + ".blob.o");
        Directory.CreateDirectory(Path.GetDirectoryName(blobObj)!);

        var toBin = await toolchain.RunToolAsync(toolchain.ObjCopy,
            ["-O", "binary", sub.PrimaryOutput, subBin], projectRoot, cancellationToken);
        if (!toBin.Success)
            return StepResult.Fail($"objcopy to binary failed: {toBin.StdErr.Trim()}");

        // Run from the .bin's directory and pass the bare filename so the
        // objcopy symbols are the clean _binary_<name>_bin_start/_end/_size.
        var toObj = await toolchain.RunToolAsync(toolchain.ObjCopy,
            ["-I", "binary", "-O", format, "-B", arch, "--rename-section", $".data={section}",
             Path.GetFileName(subBin), blobObj],
            Path.GetDirectoryName(subBin)!, cancellationToken);
        if (!toObj.Success)
            return StepResult.Fail($"objcopy to object failed: {toObj.StdErr.Trim()}");

        context.State.ExtraObjects.Add(blobObj);
        context.Logger.LogInformation("  embedded {Config} as blob in section {Section}", subName, section);
        return StepResult.Ok();
    }
}
