using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Shared machinery for source-generation steps (the external-tool
/// <see cref="TransformStep"/> and the in-process <see cref="InProcessTransformStep"/>
/// family): input discovery, manifest I/O, and registration of generated outputs
/// back into the build (sources to compile + header dirs onto the include path).
/// </summary>
internal static class TransformSupport
{
    /// <summary>
    /// Finds input files across the build's source directories matching the
    /// given glob(s) (comma- or whitespace-separated). Returns distinct full
    /// paths in a stable order.
    /// </summary>
    public static List<string> DiscoverInputs(IEnumerable<string> sourceDirs, string patterns)
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
    /// Parses a manifest: a JSON array of paths, or an object with an
    /// <c>outputs</c> array. Relative paths resolve against the generated dir.
    /// </summary>
    public static List<string> ReadManifest(string manifestPath, string generatedDir)
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

    /// <summary>Writes a manifest listing the produced files (a JSON array).</summary>
    public static void WriteManifest(string manifestPath, IEnumerable<string> outputs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(outputs));
    }

    /// <summary>
    /// Registers manifest outputs into the build: <c>.c/.cpp/.cc/.cxx/.S</c> become
    /// sources to compile, header dirs (and the generated root) join the include
    /// path. Returns Fail if a listed file is missing.
    /// </summary>
    public static StepResult Register(
        StepContext context, IReadOnlyList<string> outputs, string generatedDir, string id)
    {
        var build = context.Configuration;
        var generatedCount = 0;
        var headerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            if (!File.Exists(output))
                return StepResult.Fail($"transform '{id}' lists missing file: {output}");

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
}
