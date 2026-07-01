using System.Text.Json;

namespace MinuteOS.Build.Graph;

/// <summary>
/// Per-config action cache (docs/design/task-graph.md): an inspectable JSON file
/// mapping an action label to the fingerprints of its inputs (declared + depfile-
/// discovered), its outputs, and a config fingerprint. Drives uniform skip-when-
/// unchanged, removed-input detection (the declared-input set must match), and
/// orphan cleanup (outputs no longer produced are deleted).
///
/// Fingerprints are opaque strings (default: mtime+size), so swapping to content
/// hashing later doesn't change the format.
/// </summary>
public sealed class BuildCache
{
    public sealed record Entry(
        List<string> DeclaredInputs,
        Dictionary<string, string> Inputs,
        List<string> Outputs,
        string? ConfigKey);

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;
    private readonly List<string> _previousOutputs;
    private readonly IFingerprinter _fingerprinter;

    private BuildCache(string path, Dictionary<string, Entry> entries, IFingerprinter fingerprinter)
    {
        _path = path;
        _entries = entries;
        _fingerprinter = fingerprinter;
        _previousOutputs = entries.Values.SelectMany(e => e.Outputs).Distinct().ToList();
    }

    public static BuildCache Load(string cacheDir, IFingerprinter? fingerprinter = null)
    {
        var path = Path.Combine(cacheDir, "actions.json");
        Dictionary<string, Entry>? entries = null;
        if (File.Exists(path))
        {
            try { entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)); }
            catch { /* corrupt cache - rebuild from scratch */ }
        }
        return new BuildCache(path, entries ?? [], fingerprinter ?? new MtimeSizeFingerprinter());
    }

    /// <summary>True when the action can be skipped: same config, all outputs present,
    /// the declared-input set unchanged, and every recorded input still fingerprints equal.</summary>
    public bool IsUpToDate(BuildAction action, string? configKey)
    {
        if (!_entries.TryGetValue(action.Label, out var e))
            return false;
        if (e.ConfigKey != configKey)
            return false;
        if (e.Outputs.Any(o => IsFile(o) && !File.Exists(o)))
            return false;

        var declared = action.Inputs.Select(i => i.Id).Where(IsFile).ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(e.DeclaredInputs))
            return false;

        foreach (var (path, fingerprint) in e.Inputs)
            if (!File.Exists(path) || _fingerprinter.Compute(path) != fingerprint)
                return false;

        return true;
    }

    public void Record(BuildAction action, ActionResult result, string? configKey)
    {
        var declared = action.Inputs.Select(i => i.Id).Where(IsFile).Distinct().ToList();
        var deps = result.DiscoveredInputs ?? [];
        var inputs = declared.Concat(deps)
            .Where(p => IsFile(p) && File.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, _fingerprinter.Compute);
        var outputs = (result.ProducedArtifacts ?? action.Outputs)
            .Select(a => a.Id).Where(IsFile).Distinct().ToList();

        _entries[action.Label] = new Entry(declared, inputs, outputs, configKey);
    }

    /// <summary>Drops entries for actions not seen this build (their outputs become orphans).</summary>
    public void RetainOnly(IReadOnlySet<string> seenLabels)
    {
        foreach (var label in _entries.Keys.Where(k => !seenLabels.Contains(k)).ToList())
            _entries.Remove(label);
    }

    /// <summary>Output files produced before but no longer produced - safe to delete.</summary>
    public IEnumerable<string> Orphans()
    {
        var current = _entries.Values.SelectMany(e => e.Outputs).ToHashSet(StringComparer.Ordinal);
        return _previousOutputs.Where(o => IsFile(o) && !current.Contains(o) && File.Exists(o));
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    // A real filesystem path (not a "value:..." logical artifact).
    private static bool IsFile(string id) => !id.Contains(':') || (id.Length > 1 && id[1] == ':');
}
