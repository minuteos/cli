using System.Text.RegularExpressions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Resolves component dependencies by parsing Include.mk files.
/// Uses recursive resolution - no artificial depth limits.
/// </summary>
public partial class ComponentResolver
{
    private readonly ProjectLayout _layout;

    public ComponentResolver(ProjectLayout layout)
    {
        _layout = layout;
    }

    /// <summary>
    /// Resolves all components including transitive dependencies via recursive DFS.
    /// Returns components in dependency order (dependencies before dependents).
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

        foreach (var dep in GetComponentDependencies(component, targetDirs))
            Resolve(dep, targetDirs, resolved, seen, visiting);

        visiting.Remove(component);
        seen.Add(component);
        resolved.Add(component);
    }

    private List<string> GetComponentDependencies(string component, IReadOnlyList<string> targetDirs)
    {
        var deps = new List<string>();

        foreach (var targetDir in targetDirs)
        {
            var includeMk = Path.Combine(targetDir, component, "Include.mk");
            if (!File.Exists(includeMk))
                continue;

            var content = File.ReadAllText(includeMk);
            foreach (var match in ComponentsRegex().Matches(content).AsEnumerable())
            {
                var values = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                deps.AddRange(values);
            }
        }

        return deps;
    }

    [GeneratedRegex(@"COMPONENTS\s*\+=\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex ComponentsRegex();
}
