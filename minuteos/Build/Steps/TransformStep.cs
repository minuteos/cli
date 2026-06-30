using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Source-generation / transform step: discovers non-compiler input files (e.g.
/// <c>*.cs</c>), runs an external tool to translate them into compilable sources,
/// and registers the generated sources + headers back into the build. This is the
/// extensibility hook a transpiler (e.g. C#-&gt;C++) plugs into.
///
///   steps:
///     - name: transform
///       phase: PreBuild
///       config:
///         id: cs               # names the generated subdir, out/&lt;cfg&gt;/generated/cs
///         inputs: "**/*.cs"    # glob(s), searched across all source dirs
///         command: cs-transpiler {inputs} -o {generated-dir} --manifest {manifest}
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
        var inputs = DiscoverInputs(build.SourceDirs, inputsPattern);
        if (inputs.Count == 0)
        {
            // Nothing for this transform to do (e.g. a config with no .cs files).
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

        // Read the manifest and register outputs into the build.
        List<string> outputs;
        try
        {
            outputs = ReadManifest(manifestPath, generatedDir);
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"transform '{id}' manifest unreadable: {ex.Message}");
        }

        var generatedCount = 0;
        var headerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            if (!File.Exists(output))
                return StepResult.Fail($"transform '{id}' manifest lists missing file: {output}");

            var ext = Path.GetExtension(output);
            var language = ext switch
            {
                ".c" => SourceLanguage.C,
                ".cpp" or ".cc" or ".cxx" => SourceLanguage.Cpp,
                ".S" or ".s" => SourceLanguage.Assembly,
                _ => (SourceLanguage?)null,
            };

            if (language is { } lang)
            {
                var rel = Path.GetRelativePath(build.Layout.ProjectRoot, output);
                // Register into the state slot the runner merges into the compile.
                context.State.GeneratedSources.Add(new SourceFile(output, rel, lang));
                generatedCount++;
            }
            else if (ext is ".h" or ".hpp" or ".hh" or ".hxx")
            {
                headerDirs.Add(Path.GetDirectoryName(output)!);
            }
        }

        // Generated headers must be on the include path. Always expose the
        // generated root; add any nested dirs that actually contain headers.
        context.State.ExtraIncludeDirs.Add(generatedDir);
        foreach (var dir in headerDirs)
            if (!string.Equals(dir, generatedDir, StringComparison.OrdinalIgnoreCase))
                context.State.ExtraIncludeDirs.Add(dir);

        context.Logger.LogDebug("  transform[{Id}]: registered {Count} generated source(s)", id, generatedCount);
        return StepResult.Ok();
    }

    /// <summary>
    /// Finds input files across the build's source directories matching the
    /// step's glob(s) (comma- or whitespace-separated). Returns distinct full
    /// paths in a stable order.
    /// </summary>
    private static List<string> DiscoverInputs(IEnumerable<string> sourceDirs, string patterns)
    {
        var includes = patterns
            .Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (includes.Length == 0)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in sourceDirs.Distinct())
        {
            if (!Directory.Exists(dir))
                continue;

            var matcher = new Matcher();
            foreach (var inc in includes)
                matcher.AddInclude(inc);

            var match = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(dir)));
            foreach (var file in match.Files)
            {
                var full = Path.GetFullPath(Path.Combine(dir, file.Path));
                if (seen.Add(full))
                    result.Add(full);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Parses the tool's manifest: a JSON array of paths, or an object with an
    /// <c>outputs</c> array. Relative paths resolve against the generated dir.
    /// </summary>
    private static List<string> ReadManifest(string manifestPath, string generatedDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("outputs", out var outputs))
            array = outputs;
        else
            throw new FormatException("expected a JSON array or an object with an 'outputs' array");

        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var path = item.GetString();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            result.Add(Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(generatedDir, path)));
        }
        return result;
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
