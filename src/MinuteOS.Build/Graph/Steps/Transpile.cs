using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph.Steps;

/// <summary>
/// Discovers C# sources from the structural source dirs and emits one
/// <c>kind=source, lang=cs</c> artifact per file. Always present in the bundle;
/// harmless (produces nothing) when a project has no <c>.cs</c> files.
/// </summary>
public sealed class CsScanStep : IGraphStep
{
    public string Name => "scan:cs";

    public StepSignature Signature => StepSignature.Source(
        consumes: null, [("kind", "source"), ("lang", "cs")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var files = ctx.Config.SourceDirs.Distinct()
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs"))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Artifact.File(p, ("kind", "source"), ("lang", "cs")))
            .ToList();

        yield return new BuildAction("scan:cs", [], files, _ => Task.FromResult(ActionResult.Ok()))
        {
            AlwaysRun = true,
        };
    }
}

/// <summary>
/// In-process C#→C++ transpile step (currently a stub), as a graph step. Consumes
/// the whole-program <c>cs-source</c> set and produces generated <c>cpp-source</c>
/// + a <c>header-dir</c> via <see cref="ActionResult.ProducedArtifacts"/> (dynamic
/// outputs - the produced set is unknown until it runs). Write-if-changed keeps
/// unchanged units' timestamps so downstream compiles skip; the action always runs
/// (until the fingerprint cache lands) since any .cs change forces the pass.
/// </summary>
public sealed class TranspileStep : IGraphStep
{
    public string Name => "transpile";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(("kind", "source"), ("lang", "cs"))],
        [("kind", "source"), ("lang", "cpp")],
        [("kind", "header-dir")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var inputs = ctx.Inputs;
        if (inputs.Count == 0)
            yield break;

        var generatedDir = Path.Combine(ctx.Config.OutputRoot, "generated", "cs");
        var projectRoot = ctx.Config.Layout.ProjectRoot;

        // Whole-program: always runs (its produced set isn't known until it runs);
        // write-if-changed keeps downstream compiles incremental, and the recorded
        // produced set drives orphan cleanup when a .cs is removed.
        yield return new BuildAction("transpile", inputs, [], async actx =>
        {
            Directory.CreateDirectory(generatedDir);
            var produced = new List<Artifact>();
            var changed = 0;

            foreach (var input in inputs)
            {
                var rel = Path.GetRelativePath(projectRoot, input.Id);
                var stem = SafeStem(rel);
                var headerPath = Path.Combine(generatedDir, stem + ".g.h");
                var sourcePath = Path.Combine(generatedDir, stem + ".g.cpp");

                // --- STUB transpilation (real translation replaces this) ---
                var header =
                    $"#pragma once\n" +
                    $"// Auto-generated from {rel} by the (stub) C#->C++ transpile step.\n" +
                    $"namespace cs_stub {{ const char* {stem}_origin(); }}\n";
                var source =
                    $"#include \"{Path.GetFileName(headerPath)}\"\n" +
                    $"namespace cs_stub {{ const char* {stem}_origin() {{ return \"{rel}\"; }} }}\n";

                if (WriteIfChanged(headerPath, header)) changed++;
                if (WriteIfChanged(sourcePath, source)) changed++;

                produced.Add(Artifact.File(sourcePath, ("kind", "source"), ("lang", "cpp")));
            }

            produced.Add(Artifact.File(generatedDir, ("kind", "header-dir")));

            if (!actx.Quiet)
                actx.Logger.LogInformation("  transpile[cs]: {N} input(s) -> {Dir} ({C} file(s) changed)",
                    inputs.Count, Path.GetRelativePath(projectRoot, generatedDir), changed);

            return ActionResult.Ok(produced);
        })
        {
            AlwaysRun = true,
        };
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        File.WriteAllText(path, content);
        return true;
    }

    private static string SafeStem(string relativePath)
    {
        var withoutExt = Path.ChangeExtension(relativePath, null) ?? relativePath;
        var chars = withoutExt.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var stem = new string(chars).Trim('_');
        if (stem.Length == 0 || char.IsDigit(stem[0]))
            stem = "_" + stem;
        return stem;
    }
}
