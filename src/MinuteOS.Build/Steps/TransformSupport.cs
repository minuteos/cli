using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace MinuteOS.Build.Steps;

/// <summary>
/// Shared machinery for source-generation steps: input discovery by glob across
/// the source dirs, and reading the tool's output manifest. Consumed by the
/// graph <c>transform</c> step.
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

}
