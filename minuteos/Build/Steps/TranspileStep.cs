using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// In-process C#-&gt;C++ transpile step (currently a <b>stub</b>). It plugs the
/// transpiler into the builder as a build step rather than an external CLI: it
/// runs inside the builder, receives the whole-project <c>*.cs</c> input set, and
/// returns the generated C++ translation units.
///
///   steps:
///     - name: transpile
///       phase: PreBuild
///       config:
///         id: cs            # generated subdir, out/&lt;cfg&gt;/generated/cs (default)
///         inputs: "**/*.cs" # overridable glob(s)
///
/// The real transpiler will replace <see cref="GenerateAsync"/>'s body. The stub
/// emits, for each input, a header + source declaring/defining a marker symbol so
/// the generated code compiles and links and the wiring is verifiable end to end.
///
/// Incrementality is the transpiler's job (it gets all files for cross-unit
/// context): the stub rewrites an output only when its content changes, leaving
/// untouched units' timestamps intact so the downstream compile is skipped.
/// </summary>
public class TranspileStep : InProcessTransformStep
{
    public override string Name => "transpile";
    protected override string DefaultId => "cs";
    protected override string DefaultInputs => "**/*.cs";

    protected override Task<IReadOnlyList<string>> GenerateAsync(
        StepContext context, IReadOnlyList<string> inputs, string generatedDir, CancellationToken cancellationToken)
    {
        var projectRoot = context.Configuration.Layout.ProjectRoot;
        var outputs = new List<string>();
        var changed = 0;

        foreach (var input in inputs)
        {
            var rel = Path.GetRelativePath(projectRoot, input);
            var stem = SafeStem(rel);

            var headerPath = Path.Combine(generatedDir, stem + ".g.h");
            var sourcePath = Path.Combine(generatedDir, stem + ".g.cpp");

            // --- STUB transpilation ------------------------------------------
            // Real C#->C++ translation goes here. For now emit a trivial unit
            // that names its origin, proving the generated code compiles/links.
            var header =
                $"#pragma once\n" +
                $"// Auto-generated from {rel} by the (stub) C#->C++ transpile step.\n" +
                $"namespace cs_stub {{ const char* {stem}_origin(); }}\n";
            var source =
                $"#include \"{Path.GetFileName(headerPath)}\"\n" +
                $"namespace cs_stub {{ const char* {stem}_origin() {{ return \"{rel}\"; }} }}\n";
            // -----------------------------------------------------------------

            if (WriteIfChanged(headerPath, header)) changed++;
            if (WriteIfChanged(sourcePath, source)) changed++;

            outputs.Add(headerPath);
            outputs.Add(sourcePath);
        }

        context.Logger.LogInformation("  transpile[cs]: {Inputs} input(s) -> {Dir} ({Changed} file(s) changed)",
            inputs.Count, Path.GetRelativePath(projectRoot, generatedDir), changed);

        return Task.FromResult<IReadOnlyList<string>>(outputs);
    }

    /// <summary>
    /// Turns an input's relative path into a unique, identifier-safe stem so two
    /// same-named inputs in different dirs don't collide and the generated symbol
    /// names are valid C++ identifiers.
    /// </summary>
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
