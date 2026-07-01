namespace MinuteOS.Build;

/// <summary>
/// Resolves component dependencies by reading component.yaml files,
/// with fallback to Include.mk for backwards compatibility.
/// Uses recursive DFS with cycle detection.
/// </summary>
public class ComponentResolver
{
    private readonly ProjectLayout _layout;
    private readonly Dictionary<string, ComponentMeta> _metaCache = new();

    public ComponentResolver(ProjectLayout layout)
    {
        _layout = layout;
    }

    /// <summary>
    /// All component metadata collected during resolution.
    /// Available after ResolveComponents is called.
    /// </summary>
    public IReadOnlyDictionary<string, ComponentMeta> ComponentMetadata => _metaCache;

    /// <summary>
    /// Resolves all components including transitive dependencies via recursive DFS.
    /// Returns components in dependency order (dependencies first).
    /// </summary>
    public List<string> ResolveComponents(IEnumerable<string> initialComponents, IReadOnlyList<string> targetDirs)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>();
        var visiting = new HashSet<string>();

        foreach (var component in initialComponents)
            Resolve(component, targetDirs, resolved, seen, visiting);

        return resolved;
    }

    private void Resolve(
        string component,
        IReadOnlyList<string> targetDirs,
        List<string> resolved,
        HashSet<string> seen,
        HashSet<string> visiting)
    {
        if (seen.Contains(component))
            return;

        if (!visiting.Add(component))
            throw new InvalidOperationException(
                $"Circular dependency detected involving component '{component}'");

        // Load metadata and get dependencies
        var meta = LoadComponentMeta(component, targetDirs);
        var deps = meta?.Requires ?? [];

        foreach (var dep in deps)
            Resolve(dep, targetDirs, resolved, seen, visiting);

        visiting.Remove(component);
        seen.Add(component);
        resolved.Add(component);
    }

    /// <summary>
    /// Loads component.yaml from the first target directory that has one.
    /// Merges metadata from all target directories where the component exists.
    /// </summary>
    private ComponentMeta? LoadComponentMeta(string component, IReadOnlyList<string> targetDirs)
    {
        ComponentMeta? merged = null;

        foreach (var targetDir in targetDirs)
        {
            var componentDir = Path.Combine(targetDir, component);
            var meta = ComponentMeta.TryLoad(componentDir, component)
                ?? MakeImport.LoadComponent(componentDir, component);
            if (meta == null)
                continue;

            if (merged == null)
            {
                merged = meta;
            }
            else
            {
                // Merge: later (less specific) target dirs add to the metadata.
                // Dir-relative entries are made absolute against THEIR component
                // dir, since the merged meta keeps the first (most specific) dir.
                if (meta.Requires != null)
                    merged.Requires = (merged.Requires ?? []).Union(meta.Requires).ToList();
                if (meta.Defines != null)
                    (merged.Defines ??= []).AddRange(meta.Defines);
                if (meta.IncludeDirs != null)
                    (merged.IncludeDirs ??= []).AddRange(
                        meta.IncludeDirs.Select(d => Path.GetFullPath(Path.Combine(meta.ComponentDir, d))));
                if (meta.SourceDirs != null)
                    (merged.SourceDirs ??= []).AddRange(meta.SourceDirs.Select(d =>
                        d.Contains('*') || d.Contains('?') ? d : Path.GetFullPath(Path.Combine(meta.ComponentDir, d))));
                if (meta.Steps != null)
                    (merged.Steps ??= []).AddRange(meta.Steps);
                if (meta.Settings != null)
                {
                    merged.Settings ??= [];
                    foreach (var (key, value) in meta.Settings)
                        merged.Settings[key] = merged.Settings.TryGetValue(key, out var existing)
                            ? CombineSettingValues(existing, value)
                            : value;
                }
            }
        }

        if (merged != null)
            _metaCache[component] = merged;

        return merged;
    }

    /// <summary>
    /// Combines two raw YAML setting values into a list (list keys accumulate;
    /// scalar keys still resolve last-wins downstream, since the bag keeps order).
    /// </summary>
    private static object CombineSettingValues(object a, object b)
    {
        static IEnumerable<object> Items(object v) =>
            v is string ? [v] : v is System.Collections.IEnumerable e ? e.Cast<object>() : [v];
        return Items(a).Concat(Items(b)).ToList();
    }
}
