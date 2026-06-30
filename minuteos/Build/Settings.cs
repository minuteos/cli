namespace MinuteOS.Cli.Build;

/// <summary>
/// Aggregated build settings: an opaque key -> value(s) bag that the core never
/// interprets. Steps (e.g. the gcc toolchain) read the keys they understand.
///
/// Naming convention (see docs/design/generalized-spec.md): near-universal keys
/// are flat (<c>defines</c>, <c>include-dirs</c>); toolchain-specific keys are
/// namespaced by the step family that reads them (<c>gcc.arch-flags</c>,
/// <c>gcc.toolchain-prefix</c>, ...).
/// </summary>
public class Settings
{
    private readonly Dictionary<string, List<string>> _values = new();

    /// <summary>Appends values to a list-valued setting.</summary>
    public void Add(string key, IEnumerable<string>? values)
    {
        if (values == null) return;
        var materialized = values as ICollection<string> ?? values.ToList();
        if (materialized.Count == 0) return;
        if (!_values.TryGetValue(key, out var list))
            _values[key] = list = [];
        list.AddRange(materialized);
    }

    /// <summary>Sets a scalar setting (replaces any existing value). No-op if null.</summary>
    public void Set(string key, string? value)
    {
        if (value != null)
            _values[key] = [value];
    }

    /// <summary>All values for a list-valued setting (empty if unset).</summary>
    public IReadOnlyList<string> List(string key) =>
        _values.TryGetValue(key, out var v) ? v : [];

    /// <summary>The (last) value of a scalar setting, or null.</summary>
    public string? Scalar(string key) =>
        _values.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;

    /// <summary>All settings, for display/inspection.</summary>
    public IReadOnlyDictionary<string, List<string>> All => _values;

    /// <summary>A deep copy, so a build can augment settings without mutating the shared bag.</summary>
    public Settings Clone()
    {
        var copy = new Settings();
        foreach (var (key, values) in _values)
            copy._values[key] = [.. values];
        return copy;
    }
}
