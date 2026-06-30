using Microsoft.Extensions.Logging;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Build.Graph.Steps;

/// <summary>
/// Generic external-tool source generator (graph form of the <c>transform</c>
/// step): discovers its own inputs by glob across the source dirs, runs an
/// external command, reads back a manifest, and produces the generated sources +
/// header dir via <see cref="ActionResult.ProducedArtifacts"/> (dynamic outputs).
/// The language-agnostic escape hatch for non-.NET generators.
///
/// Config: <c>command</c>, <c>inputs</c> (glob), optional <c>id</c>.
/// </summary>
public sealed class TransformStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "transform";

    public StepSignature Signature => StepSignature.Source(
        consumes: null,
        [("kind", "source"), ("lang", "cpp")],
        [("kind", "header-dir")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        if (!config.TryGetValue("command", out var commandTemplate) || string.IsNullOrWhiteSpace(commandTemplate))
            yield break;
        if (!config.TryGetValue("inputs", out var inputsPattern) || string.IsNullOrWhiteSpace(inputsPattern))
            yield break;

        var id = config.GetValueOrDefault("id", "transform");
        var generatedDir = Path.Combine(ctx.Config.OutputRoot, "generated", id);
        var manifestPath = Path.Combine(generatedDir, ".manifest.json");
        var projectRoot = ctx.Config.Layout.ProjectRoot;

        var inputs = TransformSupport.DiscoverInputs(ctx.Config.SourceDirs, inputsPattern);
        if (inputs.Count == 0)
            yield break;

        var inputArtifacts = inputs.Select(p => Artifact.File(p, ("kind", "input"))).ToList();

        // Always runs: re-invokes the tool only when an input is newer than the
        // manifest, but always reads the manifest back to (re)publish outputs.
        yield return new BuildAction($"transform:{id}", inputArtifacts, [], async actx =>
        {
            Directory.CreateDirectory(generatedDir);

            var manifestTime = File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : DateTime.MinValue;
            var stale = !File.Exists(manifestPath) || inputs.Any(i => File.GetLastWriteTimeUtc(i) > manifestTime);

            if (stale)
            {
                var command = Substitute(commandTemplate, ctx.Config, inputs, generatedDir, manifestPath);
                if (!actx.Quiet)
                    actx.Logger.LogInformation("  transform[{Id}]: {N} input(s) -> {Dir}",
                        id, inputs.Count, Path.GetRelativePath(projectRoot, generatedDir));
                var r = await actx.Toolchain.RunShellAsync(command, projectRoot, actx.CancellationToken);
                if (!string.IsNullOrWhiteSpace(r.StdErr)) actx.Logger.LogWarning("{Err}", r.StdErr.TrimEnd());
                if (!r.Success)
                    return ActionResult.Fail($"transform '{id}' exited {r.ExitCode}");
                if (!File.Exists(manifestPath))
                    return ActionResult.Fail($"transform '{id}' wrote no manifest");
            }

            List<string> outputs;
            try { outputs = TransformSupport.ReadManifest(manifestPath, generatedDir); }
            catch (Exception ex) { return ActionResult.Fail($"transform '{id}' manifest: {ex.Message}"); }

            var produced = new List<Artifact>();
            var headerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
            {
                if (!File.Exists(output))
                    return ActionResult.Fail($"transform '{id}' lists missing file: {output}");
                var ext = Path.GetExtension(output);
                var lang = ext switch
                {
                    ".c" => "c",
                    ".cpp" or ".cc" or ".cxx" => "cpp",
                    ".S" or ".s" => "asm",
                    _ => null,
                };
                if (lang != null)
                    produced.Add(Artifact.File(output, ("kind", "source"), ("lang", lang)));
                else if (ext is ".h" or ".hpp" or ".hh" or ".hxx")
                    headerDirs.Add(Path.GetDirectoryName(output)!);
            }
            produced.Add(Artifact.File(generatedDir, ("kind", "header-dir")));
            foreach (var dir in headerDirs)
                if (!string.Equals(dir, generatedDir, StringComparison.OrdinalIgnoreCase))
                    produced.Add(Artifact.File(dir, ("kind", "header-dir")));

            return ActionResult.Ok(produced);
        })
        {
            AlwaysRun = true,
        };
    }

    private static string Substitute(
        string template, BuildConfiguration c, IReadOnlyList<string> inputs, string generatedDir, string manifest)
    {
        string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
        return template
            .Replace("{inputs}", string.Join(' ', inputs.Select(Quote)))
            .Replace("{generated-dir}", Quote(generatedDir))
            .Replace("{manifest}", Quote(manifest))
            .Replace("{output-dir}", c.OutputRoot)
            .Replace("{name}", c.OutputName);
    }
}
