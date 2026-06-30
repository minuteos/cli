using System.Text.RegularExpressions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Discovers and represents the layout of a minuteos project on disk.
/// - Project root contains lib* directories (LIB_ROOTS)
/// - Each lib root and project root has a targets/ directory (TARGET_ROOTS)
/// - Target directories contain component directories
/// - Project source is in src/
/// </summary>
public partial class ProjectLayout
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

        // Try to load target.yaml from any target root
        TargetMeta? meta = null;
        List<string>? parentTargets = null;

        foreach (var root in TargetRoots)
        {
            var dir = Path.Combine(root, target);
            if (!Directory.Exists(dir))
                continue;

            var loaded = TargetMeta.TryLoad(dir, target) ?? LoadTargetFromIncludeMk(dir, target);
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
    /// Builds target metadata from a legacy Include.mk when no target.yaml exists.
    /// Captures the simple, unambiguous assignments (TARGETS, COMPONENTS,
    /// TOOLCHAIN_PREFIX, ARCH_FLAGS, PRIMARY_EXT, LD_SCRIPT, DEFINES). Values that
    /// reference Make variables ($(...)) are skipped - notably LINK_FLAGS, which
    /// must be provided via target.yaml.
    /// </summary>
    private static TargetMeta? LoadTargetFromIncludeMk(string targetDir, string target)
    {
        var includeMk = Path.Combine(targetDir, "Include.mk");
        if (!File.Exists(includeMk))
            return null;

        var content = File.ReadAllText(includeMk);
        var meta = new TargetMeta { TargetDir = targetDir, Name = target };

        var requires = MkAppend(content, "TARGETS");
        if (requires.Count > 0) meta.Requires = requires;

        var components = MkAppend(content, "COMPONENTS");
        if (components.Count > 0) meta.Components = components;

        var prefix = MkAssign(content, "TOOLCHAIN_PREFIX");
        if (prefix != null && !prefix.Contains("$(")) meta.ToolchainPrefix = prefix;

        var ext = MkAssign(content, "PRIMARY_EXT");
        if (ext != null && !ext.Contains("$(")) meta.PrimaryExt = ext;

        var ld = MkAssign(content, "LD_SCRIPT");
        if (ld != null && !ld.Contains("$(")) meta.LdScript = ld;

        var arch = MkAssign(content, "ARCH_FLAGS");
        if (arch != null && !arch.Contains("$(")) meta.ArchFlags = MkTokens(arch).ToList();

        // LINK_FLAGS mixes static flags with $(...) expansions (-T$(LD_SCRIPT),
        // -Map,$(OUTPUT).map). Keep the static tokens; the ld-script and
        // --gc-sections are added by the linker step from other settings.
        var linkFlags = MkAppend(content, "LINK_FLAGS")
            .Where(f => !f.Contains("$("))
            .ToList();
        if (linkFlags.Count > 0) meta.LinkFlags = linkFlags;

        // LINK_DIRS often reference $(<NAME>_DIR), which by lib convention is
        // `$(dir $(call curmake))` - i.e. this target's own directory. Strip that
        // prefix so the path resolves relative to the target dir (as LinkDirs do).
        var linkDirs = MkAppend(content, "LINK_DIRS")
            .Select(d => Regex.Replace(d, @"\$\(\w+_DIR\)", ""))
            .Where(d => !d.Contains("$("))
            .ToList();
        if (linkDirs.Count > 0) meta.LinkDirs = linkDirs;

        var defines = MkAppend(content, "DEFINES")
            .Where(d => !d.Contains("$("))
            .Select(d => d.Replace("\\\"", "\""))   // Make-escaped quotes -> real quotes
            .ToList();
        if (defines.Count > 0) meta.Defines = defines;

        // TEST_RUN is the emulator invocation; the test binary is appended after
        // it (e.g. `qemu-system-arm ... -kernel <binary>`), followed by
        // TEST_RUN_ARGS. $(TEST_FILTERS) maps to {filter}; other $(...) are dropped.
        var testRun = MkAssign(content, "TEST_RUN");
        if (testRun != null && !testRun.Contains("$("))
        {
            var tokens = MkTokens(testRun).ToList();
            if (tokens.Count > 0)
            {
                var runnerArgs = tokens.Skip(1).ToList();
                runnerArgs.Add("{binary}");

                var testRunArgs = MkAssign(content, "TEST_RUN_ARGS");
                if (testRunArgs != null)
                {
                    foreach (var t in MkTokens(testRunArgs))
                    {
                        var tok = t.Trim('"').Replace("$(TEST_FILTERS)", "{filter}");
                        if (tok.Contains("$(")) continue;
                        runnerArgs.Add(tok);
                    }
                }

                meta.TestRunner = new TestRunnerConfig { Command = tokens[0], Args = runnerArgs };
            }
        }

        // Only treat the file as a target if it actually declared something.
        var hasContent = meta.Requires != null || meta.Components != null ||
            meta.ToolchainPrefix != null || meta.PrimaryExt != null ||
            meta.LdScript != null || meta.ArchFlags != null || meta.Defines != null ||
            meta.TestRunner != null;
        return hasContent ? meta : null;
    }

    // VAR += value (possibly across multiple lines), returns all tokens.
    private static List<string> MkAppend(string content, string variable) =>
        Regex.Matches(content, $@"^{variable}\s*\+=\s*(.+)$", RegexOptions.Multiline)
            .SelectMany(m => MkTokens(m.Groups[1].Value))
            .ToList();

    // VAR = / ?= / := value, returns the last assignment's value (trimmed).
    private static string? MkAssign(string content, string variable)
    {
        var matches = Regex.Matches(content, $@"^{variable}\s*[?:]?=\s*(.+)$", RegexOptions.Multiline);
        return matches.Count > 0 ? matches[^1].Groups[1].Value.Trim() : null;
    }

    private static IEnumerable<string> MkTokens(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
