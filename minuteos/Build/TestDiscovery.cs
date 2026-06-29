namespace MinuteOS.Cli.Build;

/// <summary>
/// A discovered test suite: a directory of test sources living under a
/// component's tests/ directory.
///
/// Layout convention (matching the Make-based system):
///   &lt;targetDir&gt;/&lt;component&gt;/tests/&lt;suite&gt;/*.cpp
/// e.g. lib/targets/all/base/tests/sanity/
/// </summary>
public record TestSuite
{
    /// <summary>The suite name (the directory under tests/).</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the suite source directory.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// The component under test, as a path-style name (e.g. "base" or
    /// "sensors/environment").
    /// </summary>
    public required string Component { get; init; }

    /// <summary>A stable identifier "component/suite" used for dedup and output paths.</summary>
    public string Id => $"{Component}/{Name}";
}

/// <summary>
/// Discovers test suites by scanning the active target directories for
/// tests/ directories.
/// </summary>
public class TestDiscovery
{
    private static readonly string[] SourceExtensions = [".c", ".cpp", ".S"];

    /// <summary>
    /// Finds all test suites under the given target directories.
    /// Suites are deduplicated by component/suite id, keeping the first found.
    /// Optionally filtered to specific components.
    /// </summary>
    public List<TestSuite> Discover(IEnumerable<string> targetDirs, IEnumerable<string>? componentFilter = null)
    {
        var filter = componentFilter?.ToHashSet();
        var suites = new List<TestSuite>();
        var seen = new HashSet<string>();

        foreach (var targetDir in targetDirs)
        {
            if (!System.IO.Directory.Exists(targetDir))
                continue;

            foreach (var testsDir in System.IO.Directory.EnumerateDirectories(targetDir, "tests", SearchOption.AllDirectories))
            {
                // Component = path from target dir to the parent of tests/
                var componentDir = System.IO.Directory.GetParent(testsDir)!.FullName;
                var component = Path.GetRelativePath(targetDir, componentDir).Replace('\\', '/');

                // Skip the synthetic case where tests/ sits directly in the target dir
                if (component == ".")
                    continue;

                if (filter != null && !filter.Contains(component))
                    continue;

                foreach (var suiteDir in System.IO.Directory.EnumerateDirectories(testsDir))
                {
                    if (!HasSources(suiteDir))
                        continue;

                    var suite = new TestSuite
                    {
                        Name = Path.GetFileName(suiteDir),
                        Directory = Path.GetFullPath(suiteDir),
                        Component = component,
                    };

                    if (seen.Add(suite.Id))
                        suites.Add(suite);
                }
            }
        }

        return suites.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    private static bool HasSources(string dir)
    {
        return System.IO.Directory.EnumerateFiles(dir)
            .Any(f => SourceExtensions.Contains(Path.GetExtension(f)));
    }
}
