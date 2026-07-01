namespace MinuteOS.Build;

/// <summary>
/// Aggregated build settings: an immutable, opaque key -> value(s) bag that the
/// core never interprets. Steps (e.g. the gcc toolchain) read the keys they
/// understand. Immutability means it can be shared and read concurrently (the
/// parallel scheduler) with no copying, and augmenting it (<see cref="With"/>)
/// yields a new bag rather than mutating a shared one.
///
/// Build it with <see cref="Builder"/>. Naming convention (see
/// docs/design/generalized-spec.md): near-universal keys are flat (<c>defines</c>,
/// <c>include-dirs</c>); toolchain-specific keys are namespaced by the step family
/// that reads them (<c>gcc.arch-flags</c>, <c>gcc.toolchain-prefix</c>, ...).
/// </summary>
public sealed class Settings
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _values;

    private Settings(IReadOnlyDictionary<string, IReadOnlyList<string>> values) => _values = values;

    public static Settings Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>All values for a list-valued setting (empty if unset).</summary>
    public IReadOnlyList<string> List(string key) =>
        _values.TryGetValue(key, out var v) ? v : [];

    /// <summary>The (last) value of a scalar setting, or null.</summary>
    public string? Scalar(string key) =>
        _values.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;

    /// <summary>All settings, for display/inspection.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> All => _values;

    /// <summary>
    /// Returns a new bag with the given additions appended (list keys accumulate,
    /// scalar keys resolve to the last value). Used by settings augmenters.
    /// </summary>
    public Settings With(IReadOnlyDictionary<string, IReadOnlyList<string>> additions)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(_values);
        foreach (var (key, values) in additions)
        {
            if (values.Count == 0) continue;
            copy[key] = copy.TryGetValue(key, out var existing) ? [.. existing, .. values] : [.. values];
        }
        return new Settings(copy);
    }

    /// <summary>Mutable accumulator that produces an immutable <see cref="Settings"/>.</summary>
    public sealed class Builder
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

        /// <summary>The (last) value of a scalar setting under construction, or null.</summary>
        public string? Scalar(string key) =>
            _values.TryGetValue(key, out var v) && v.Count > 0 ? v[^1] : null;

        public Settings Build() => new(
            _values.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToArray()));
    }
}
