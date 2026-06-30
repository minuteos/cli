namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// A build artifact: a stable identity (a file path; logical value-artifacts use a
/// reserved <c>value:</c> prefix) plus opaque key-value <see cref="Properties"/>
/// the engine never interprets - it only matches them. See docs/design/task-graph.md.
/// </summary>
public sealed record Artifact(string Id, IReadOnlyDictionary<string, string> Properties)
{
    public string? Kind => Properties.GetValueOrDefault("kind");

    /// <summary>True when every required pair of the selector is present and equal.</summary>
    public bool Matches(Selector selector) =>
        selector.Required.All(kv => Properties.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Convenience constructor for a file-backed artifact.</summary>
    public static Artifact File(string path, params (string Key, string Value)[] properties) =>
        new(path, properties.ToDictionary(p => p.Key, p => p.Value));

    public override string ToString() =>
        $"{Id} {{{string.Join(", ", Properties.Select(kv => $"{kv.Key}={kv.Value}"))}}}";
}

public enum Cardinality { One, Many }

/// <summary>
/// A predicate over artifact properties: an artifact matches when it carries all of
/// <see cref="Required"/> (a conjunction of key=value). A step may declare several
/// selectors; their union is what it consumes. <see cref="Cardinality"/> is a
/// sanity expectation on how many artifacts should match (One vs Many).
/// </summary>
public sealed record Selector(IReadOnlyDictionary<string, string> Required, Cardinality Cardinality = Cardinality.Many)
{
    public static Selector Of(Cardinality cardinality, params (string Key, string Value)[] required) =>
        new(required.ToDictionary(p => p.Key, p => p.Value), cardinality);

    public static Selector Of(params (string Key, string Value)[] required) =>
        Of(Cardinality.Many, required);
}
