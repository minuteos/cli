namespace MinuteOS.Cli.Build;

/// <summary>
/// Discovers and represents the layout of a minuteos project on disk.
/// Mirrors the directory conventions from Base.mk:
/// - Project root contains lib* directories (LIB_ROOTS)
/// - Each lib root and project root has a targets/ directory (TARGET_ROOTS)
/// - Target directories contain component directories
/// - Project source is in src/
/// </summary>
public class ProjectLayout
{
    public string ProjectRoot { get; }
    public string Name { get; }
    public string SourceDir { get; }
    public IReadOnlyList<string> LibRoots { get; }
    public IReadOnlyList<string> TargetRoots { get; }

    public ProjectLayout(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        Name = Path.GetFileName(ProjectRoot);
        SourceDir = Path.Combine(ProjectRoot, "src");

        // Find lib* directories (LIB_ROOTS)
        LibRoots = Directory.Exists(ProjectRoot)
            ? Directory.GetDirectories(ProjectRoot, "lib*")
                .Where(Directory.Exists)
                .ToList()
            : [];

        // Find targets/ directories in project root and lib roots (TARGET_ROOTS)
        var targetRoots = new List<string>();
        foreach (var root in new[] { ProjectRoot }.Concat(LibRoots))
        {
            var targetsDir = Path.Combine(root, "targets");
            if (Directory.Exists(targetsDir))
                targetRoots.Add(targetsDir);
        }
        TargetRoots = targetRoots;
    }

    /// <summary>
    /// Resolves the active target directories for a given set of targets.
    /// Targets are resolved in order, with "all" always appended.
    /// Each target name maps to subdirectories under each TARGET_ROOT.
    /// </summary>
    public List<string> ResolveTargetDirs(IEnumerable<string> targets)
    {
        var allTargets = targets
            .Where(t => t != "all")
            .Append("all")
            .Distinct()
            .ToList();

        var dirs = new List<string>();
        // subdirs2 semantics: prefer order of second argument (targets), iterate target roots for each
        foreach (var target in allTargets)
        {
            foreach (var root in TargetRoots)
            {
                var dir = Path.Combine(root, target);
                if (Directory.Exists(dir))
                    dirs.Add(dir);
            }
        }
        return dirs;
    }

    /// <summary>
    /// Resolves the component directories from target directories.
    /// Components are subdirectories under target dirs.
    /// </summary>
    public List<string> ResolveComponentDirs(IReadOnlyList<string> targetDirs, IEnumerable<string> components)
    {
        var dirs = new List<string>();
        // subdirs2 semantics: prefer order of second argument (components)
        foreach (var component in components)
        {
            foreach (var targetDir in targetDirs)
            {
                var dir = Path.Combine(targetDir, component);
                if (Directory.Exists(dir))
                    dirs.Add(dir);
            }
        }
        return dirs;
    }
}
