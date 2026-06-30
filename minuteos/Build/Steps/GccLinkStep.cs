using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Links the objects into the primary image, reading toolchain flags from the
/// <see cref="Settings"/> bag. Reads the <c>Objects</c> slot (plus step-contributed
/// extra objects), writes the <c>Image</c> slot. Built-in pipeline step; a target
/// swaps it out by listing a different link step in its pipeline.
/// </summary>
public class GccLinkStep : IBuildStep
{
    public string Name => "gcc:link";
    public BuildPhase DefaultPhase => BuildPhase.PreLink;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var config = context.Configuration;
        var toolchain = context.Toolchain;
        var s = config.Settings;
        if (!context.Quiet)
        {
            context.Logger.LogInformation("");
            context.Logger.LogInformation("Linking...");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(config.PrimaryOutput)!);

        var args = new List<string> { "-o", config.PrimaryOutput };

        // Architecture flags for the linker too.
        args.AddRange(s.List("gcc.arch-flags"));

        // Objects: compiled + step-contributed (e.g. a sub-build blob).
        var objects = context.State.Objects.Concat(context.State.ExtraObjects);
        args.AddRange(objects.Order());

        // Linker script.
        var ldScript = s.Scalar("gcc.ld-script");
        if (ldScript != null)
            args.AddRange(["-T", ldScript]);

        // Explicit link search dirs (from target link-dirs), then the implicit
        // structural ones (source, target, component dirs).
        foreach (var dir in s.List("gcc.link-dirs"))
            args.AddRange(["-L", dir]);

        var libDirs = config.TargetDirs.Concat(config.ComponentDirs);
        if (Directory.Exists(config.Layout.SourceDir))
            libDirs = new[] { config.Layout.SourceDir }.Concat(libDirs);
        foreach (var dir in libDirs)
            args.AddRange(["-L", dir]);

        // Link flags from the bag (target + profile + components) and steps.
        args.AddRange(s.List("gcc.link-flags"));
        args.AddRange(context.State.ExtraLinkFlags);

        // Garbage-collect unused sections.
        args.Add("-Wl,--gc-sections");

        var result = await toolchain.RunToolAsync(toolchain.CXX, args, config.Layout.ProjectRoot, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StdErr))
            context.Logger.LogWarning("{StdErr}", result.StdErr.TrimEnd());
        if (!result.Success)
            return StepResult.Fail($"Linking failed with exit code {result.ExitCode}");

        context.State.Image = config.PrimaryOutput;
        return StepResult.Ok();
    }
}
