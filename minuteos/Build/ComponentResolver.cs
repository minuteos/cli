using System.Text.RegularExpressions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Resolves component dependencies by parsing Include.mk files.
/// Each component's Include.mk can add more components via "COMPONENTS += ..."
/// Dependencies are resolved iteratively until stable (max 8 levels, matching Base.mk).
/// </summary>
public partial class ComponentResolver
{
    private readonly ProjectLayout _layout;

    public ComponentResolver(ProjectLayout layout)
    {
        _layout = layout;
    }

    /// <summary>
    /// Resolves all components including transitive dependencies.
    /// Returns the full ordered list of unique components.
    /// </summary>
    public List<string> ResolveComponents(IEnumerable<string> initialComponents, IReadOnlyList<string> targetDirs)
    {
        var components = new List<string>(initialComponents);
        var seen = new HashSet<string>(components);

        const int maxIterations = 8;
        for (int i = 0; i < maxIterations; i++)
        {
            var newComponents = new List<string>();

            foreach (var component in components)
            {
                var deps = GetComponentDependencies(component, targetDirs);
                foreach (var dep in deps)
                {
                    if (seen.Add(dep))
                        newComponents.Add(dep);
                }
            }

            if (newComponents.Count == 0)
                break;

            components.AddRange(newComponents);

            if (i == maxIterations - 1 && newComponents.Count > 0)
                throw new InvalidOperationException(
                    "Too many dependency levels. Consider simplifying the dependency tree.");
        }

        return components;
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
