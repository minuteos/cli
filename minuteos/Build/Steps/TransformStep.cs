using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Generic external-tool source-generation step: discovers non-compiler input
/// files (e.g. <c>*.cs</c>), runs an external command to translate them into
/// compilable sources, and registers the generated sources + headers back into
/// the build. This is the language-agnostic escape hatch; an in-process generator
/// (see <see cref="InProcessTransformStep"/> / <see cref="TranspileStep"/>) is the
/// preferred path when the generator is a .NET component.
///
///   steps:
///     - name: transform
///       phase: PreBuild
///       config:
///         id: gen              # names the generated subdir, out/&lt;cfg&gt;/generated/gen
///         inputs: "**/*.proto" # glob(s), searched across all source dirs
///         command: protoc {inputs} --out {generated-dir} --manifest {manifest}
///
/// I/O contract with the tool: it receives the matched inputs, an output
/// directory, and a manifest path; it writes the produced files into the output
/// directory and lists them in the manifest (a JSON array of paths, or
/// <c>{"outputs": [...]}</c>; paths may be absolute or relative to the output
/// dir). The step then feeds any generated <c>.c/.cpp/.S</c> into the compile and
/// adds the output dir to the include path. Regeneration is incremental: the tool
/// only re-runs when an input is newer than the last manifest.
///
/// Command placeholders: {inputs} {generated-dir} {manifest} plus the standard
/// {output} {output-base} {output-dir} {name} {cc} {cxx} {objcopy} ...
/// </summary>
public class TransformStep : IBuildStep
{
    public string Name => "transform";
    public BuildPhase DefaultPhase => BuildPhase.PreBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var cfg = context.StepConfig;
        if (!cfg.TryGetValue("command", out var commandTemplate) || string.IsNullOrWhiteSpace(commandTemplate))
            return StepResult.Fail("transform step requires a 'command' in its config");
        if (!cfg.TryGetValue("inputs", out var inputsPattern) || string.IsNullOrWhiteSpace(inputsPattern))
            return StepResult.Fail("transform step requires an 'inputs' glob in its config");

        var build = context.Configuration;
        var id = cfg.GetValueOrDefault("id", "transform");
        var generatedDir = cfg.TryGetValue("generated-subdir", out var sub) && !string.IsNullOrWhiteSpace(sub)
            ? Path.Combine(build.OutputRoot, sub)
            : Path.Combine(build.OutputRoot, "generated", id);
        var manifestPath = Path.Combine(generatedDir, ".manifest.json");

        // Discover inputs across all source dirs by the step's own glob(s).
        var inputs = TransformSupport.DiscoverInputs(build.SourceDirs, inputsPattern);
        if (inputs.Count == 0)
        {
            // Nothing for this transform to do (e.g. a config with no inputs).
            context.Logger.LogDebug("  transform[{Id}]: no inputs matching '{Pattern}'", id, inputsPattern);
            return StepResult.Ok();
        }

        // Incremental: re-run only when an input is newer than the last manifest.
        var manifestExists = File.Exists(manifestPath);
        var manifestTime = manifestExists ? File.GetLastWriteTimeUtc(manifestPath) : DateTime.MinValue;
        var stale = !manifestExists || inputs.Any(i => File.GetLastWriteTimeUtc(i) > manifestTime);

        if (stale)
        {
            Directory.CreateDirectory(generatedDir);
            var command = Substitute(commandTemplate, context, inputs, generatedDir, manifestPath);
            context.Logger.LogInformation("  transform[{Id}]: {Count} input(s) -> {Dir}",
                id, inputs.Count, Path.GetRelativePath(build.Layout.ProjectRoot, generatedDir));
            context.Logger.LogDebug("  $ {Command}", command);

            var result = await context.Toolchain.RunShellAsync(
                command, build.Layout.ProjectRoot, cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.StdOut))
                context.Logger.LogDebug("{Out}", result.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.StdErr))
                context.Logger.LogWarning("{Err}", result.StdErr.TrimEnd());
            if (!result.Success)
                return StepResult.Fail($"transform '{id}' command exited {result.ExitCode}: {command}");

            if (!File.Exists(manifestPath))
                return StepResult.Fail($"transform '{id}' did not write a manifest at {manifestPath}");
        }
        else
        {
            context.Logger.LogDebug("  transform[{Id}]: up-to-date", id);
        }

        // Read the tool's manifest and register the outputs into the build.
        List<string> outputs;
        try
        {
            outputs = TransformSupport.ReadManifest(manifestPath, generatedDir);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"transform '{id}' manifest unreadable: {ex.Message}");
        }

        return TransformSupport.Register(context, outputs, generatedDir, id);
    }

    private static string Substitute(
        string template, StepContext ctx, IReadOnlyList<string> inputs, string generatedDir, string manifestPath)
    {
        var c = ctx.Configuration;
        var t = ctx.Toolchain;
        var outputBase = Path.Combine(c.OutputRoot, c.OutputName);
        var inputList = string.Join(' ', inputs.Select(Quote));
        return template
            .Replace("{inputs}", inputList)
            .Replace("{generated-dir}", Quote(generatedDir))
            .Replace("{manifest}", Quote(manifestPath))
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

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
