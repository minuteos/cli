namespace MinuteOS.Build;

/// <summary>
/// Discovers and represents the layout of a minuteos project on disk.
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

    public ProjectLayout(string projectRoot, string? name = null)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        Name = name ?? Path.GetFileName(ProjectRoot);
        SourceDir = Path.Combine(ProjectRoot, "src");

        LibRoots = Directory.Exists(ProjectRoot)
            ? Directory.GetDirectories(ProjectRoot, "lib*")
                .Where(Directory.Exists)
                .ToList()
            : [];

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
    /// Resolves the full target chain including parent targets via recursive resolution.
    /// Returns target names in dependency order, with "all" always last.
    /// Also collects TargetMeta for each resolved target.
    /// </summary>
    public List<string> ResolveTargetChain(string primaryTarget, out Dictionary<string, TargetMeta> targetMetadata)
        => ResolveTargetChain(primaryTarget, [], out targetMetadata);

    /// <summary>
    /// Resolves the full target chain including parent targets and any extra
    /// targets (e.g. the "test" pseudo-target injected for test builds).
    /// </summary>
    public List<string> ResolveTargetChain(string primaryTarget, IEnumerable<string> extraTargets, out Dictionary<string, TargetMeta> targetMetadata)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>();
        var metadata = new Dictionary<string, TargetMeta>();

        ResolveTarget(primaryTarget, resolved, seen, metadata);

        foreach (var extra in extraTargets)
            ResolveTarget(extra, resolved, seen, metadata);

        // "all" is always included last
        if (seen.Add("all"))
            resolved.Add("all");

        targetMetadata = metadata;
        return resolved;
    }

    private void ResolveTarget(string target, List<string> resolved, HashSet<string> seen, Dictionary<string, TargetMeta> metadata)
    {
        if (!seen.Add(target))
            return;

        // Prefer target.yaml; fall back to importing a legacy Include.mk.
        TargetMeta? meta = null;
        List<string>? parentTargets = null;

        foreach (var root in TargetRoots)
        {
            var dir = Path.Combine(root, target);
            if (!Directory.Exists(dir))
                continue;

            var loaded = TargetMeta.TryLoad(dir, target) ?? MakeImport.LoadTarget(dir, target);
            if (loaded != null)
            {
                meta = loaded;
                parentTargets = loaded.Requires;
                break;
            }
        }

        if (meta != null)
            metadata[target] = meta;

        // Resolve parent targets first (dependencies before dependents)
        if (parentTargets != null)
        {
            foreach (var parent in parentTargets)
                ResolveTarget(parent, resolved, seen, metadata);
        }

        resolved.Add(target);
    }

    /// <summary>
    /// Resolves target directories from resolved target names.
    /// </summary>
    public List<string> ResolveTargetDirs(IEnumerable<string> targets)
    {
        var dirs = new List<string>();
        foreach (var target in targets)
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
    /// </summary>
    public List<string> ResolveComponentDirs(IReadOnlyList<string> targetDirs, IEnumerable<string> components)
    {
        var dirs = new List<string>();
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
