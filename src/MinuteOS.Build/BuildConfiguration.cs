using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace MinuteOS.Build;

/// <summary>
/// The fully resolved build configuration for a minuteos project: the merge of
/// project config, target metadata (with inheritance), and component metadata
/// (with transitive requires). Structure (names, dirs, steps) is typed; all
/// toolchain configuration lives in the <see cref="Settings"/> bag.
/// </summary>
public class BuildConfiguration : IBuildConfiguration
{
    public required string Name { get; init; }
    public required ProjectLayout Layout { get; init; }
    public required string Target { get; init; }
    public required string Config { get; init; }
    public required List<string> Targets { get; init; }
    public required List<string> Components { get; init; }
    public required IReadOnlyList<string> TargetDirs { get; init; }
    public required IReadOnlyList<string> ComponentDirs { get; init; }
    public required IReadOnlyList<string> SourceDirs { get; init; }
    public required List<StepReference> StepRefs { get; init; }

    /// <summary>Aggregated, toolchain-agnostic settings consumed by build steps.</summary>
    public required Settings Settings { get; init; }

    /// <summary>Project root (from the layout).</summary>
    public string ProjectRoot => Layout.ProjectRoot;

    /// <summary>Primary source dir (from the layout).</summary>
    public string SourceDir => Layout.SourceDir;

    /// <summary>Primary output extension, from the settings bag (default .elf).</summary>
    public string PrimaryExt => Settings.Scalar("gcc.primary-ext") ?? ".elf";

    /// <summary>
    /// Overrides the output binary name (defaults to the project name).
    /// Used for test-suite builds where the binary is named after the suite.
    /// </summary>
    public string? OutputNameOverride { get; init; }

    /// <summary>
    /// Subdirectory under out/&lt;config&gt;/ where this build's artifacts go.
    /// Used to isolate per-suite test builds, e.g. "tests/base/sanity".
    /// </summary>
    public string? OutputSubdir { get; init; }

    public string OutputRoot
    {
        get
        {
            var root = Path.Combine(Layout.ProjectRoot, "out", Name);
            return string.IsNullOrEmpty(OutputSubdir) ? root : Path.Combine(root, OutputSubdir);
        }
    }
    public string ObjectDir => Path.Combine(OutputRoot, "obj");
    public string OutputName => OutputNameOverride ?? Layout.Name;
    public string PrimaryOutput => Path.Combine(OutputRoot, OutputName + PrimaryExt);

    /// <summary>
    /// Resolved precompiled header (precompiled.hpp), if any.
    /// </summary>
    public string? Pch { get; init; }

    /// <summary>Where the compiled PCH is written.</summary>
    public string PchGchFile => Path.Combine(ObjectDir, "precompiled.gch");

    /// <summary>The -include argument; GCC finds precompiled.gch beside it.</summary>
    public string PchIncludeBase => Path.Combine(ObjectDir, "precompiled");

    public static BuildConfiguration Create(
        ProjectConfig projectConfig, string configName, string projectRoot, BuildOverrides? overrides = null)
    {
        var profile = projectConfig.Resolve(configName);
        var depRoots = (projectConfig.Dependencies ?? [])
            .Select(d => DependencyRestorer.ResolveDir(d, projectRoot));
        var layout = new ProjectLayout(projectRoot, projectConfig.Name, depRoots);

        var primaryTarget = profile.Target ?? "host";
        var config = profile.Config ?? "Release";

        // The primary source dir is the suite dir (test builds), an explicit
        // source-dir from the profile (e.g. a bootloader sub-build), else src/.
        var primarySourceDir = overrides?.PrimarySourceDir
            ?? (profile.SourceDir != null ? Path.GetFullPath(Path.Combine(projectRoot, profile.SourceDir)) : null)
            ?? layout.SourceDir;

        // === Target resolution with inheritance ===
        // Test builds inject extra targets (e.g. "test") that provide hardware stubs.
        var targetNames = layout.ResolveTargetChain(
            primaryTarget, overrides?.ExtraTargets ?? [], out var targetMetadata);

        // Directory precedence is most-specific-first (so a target-specific header
        // like qemu-arm/cortex_defs.h overrides cortex-m's), with the shared "all"
        // target last. targetNames is parents-first, so reverse all but "all".
        var targetDirsOrder = targetNames.Where(t => t != "all").Reverse()
            .Concat(targetNames.Where(t => t == "all")).ToList();
        var targetDirs = layout.ResolveTargetDirs(targetDirsOrder);

        // Merge target metadata contributions. targetNames are parents-first, so
        // appending settings maps in order gives the most-specific target
        // precedence for scalars; lists accumulate across the chain.
        var targetDefines = new List<string>();
        var targetComponents = new List<string>();
        var stepRefs = new List<StepReference>();
        var settingsMaps = new List<Dictionary<string, object>>();

        foreach (var targetName in targetNames)
        {
            if (!targetMetadata.TryGetValue(targetName, out var tmeta))
                continue;

            if (tmeta.Defines != null) targetDefines.AddRange(tmeta.Defines);
            if (tmeta.Components != null) targetComponents.AddRange(tmeta.Components);
            if (tmeta.Steps != null) stepRefs.AddRange(tmeta.Steps);
            if (tmeta.Settings != null) settingsMaps.Add(tmeta.Settings);
        }

        // === Component resolution ===
        // Test builds supply their own component set (testrunner + component-under-test).
        var baseComponents = overrides?.Components ?? profile.Components ?? ["kernel"];
        var resolver = new ComponentResolver(layout);
        var resolvedComponents = resolver.ResolveComponents(
            targetComponents.Concat(baseComponents), targetDirs);
        var componentMeta = resolver.ComponentMetadata;

        var componentDirs = layout.ResolveComponentDirs(targetDirs, resolvedComponents);

        // === Merge component metadata ===
        var componentDefines = new List<string>();
        var componentIncludeDirs = new List<string>();
        var componentSourceDirs = new List<string>();

        foreach (var component in resolvedComponents)
        {
            if (!componentMeta.TryGetValue(component, out var meta))
                continue;

            if (meta.Defines != null)
                componentDefines.AddRange(meta.Defines);

            foreach (var dir in meta.IncludeDirs ?? [])
                componentIncludeDirs.Add(Path.GetFullPath(Path.Combine(meta.ComponentDir, dir)));

            foreach (var pattern in meta.SourceDirs ?? [])
                componentSourceDirs.AddRange(ResolveSourceDirPattern(meta.ComponentDir, pattern));

            if (meta.Steps != null) stepRefs.AddRange(meta.Steps);
            if (meta.Settings != null) settingsMaps.Add(meta.Settings);
        }

        // Steps + settings from the project-level config (highest precedence).
        if (profile.Steps != null)
            stepRefs.AddRange(profile.Steps);
        if (profile.Settings != null)
            settingsMaps.Add(profile.Settings);

        // === Assemble include dirs ===
        var includeDirs = new List<string>();
        includeDirs.AddRange(componentIncludeDirs);
        foreach (var dir in profile.IncludeDirs ?? [])
            includeDirs.Add(Path.GetFullPath(Path.Combine(projectRoot, dir)));
        if (Directory.Exists(primarySourceDir))
            includeDirs.Add(primarySourceDir);
        includeDirs.AddRange(targetDirs);
        includeDirs.AddRange(layout.TargetRoots);

        // === Assemble source dirs ===
        var sourceDirs = new List<string>();
        if (Directory.Exists(primarySourceDir))
            sourceDirs.Add(primarySourceDir);
        sourceDirs.AddRange(targetDirs);
        sourceDirs.AddRange(componentDirs);
        sourceDirs.AddRange(componentSourceDirs);
        sourceDirs = sourceDirs.Distinct().ToList();

        // === Resolve precompiled header ===
        // A precompiled.hpp in the primary source dir (app builds) or in a
        // component dir (e.g. testrunner, for test builds) enables PCH.
        string? pch = null;
        foreach (var dir in new[] { primarySourceDir }.Concat(componentDirs))
        {
            var candidate = Path.Combine(dir, "precompiled.hpp");
            if (File.Exists(candidate)) { pch = candidate; break; }
        }

        // === Assemble defines ===
        // Every component/target contributes a C<name>/T<name> marker define.
        var defines = new List<string>();
        foreach (var c in resolvedComponents)
            defines.Add("C" + c.Replace("/", "_").Replace("-", "_"));
        foreach (var t in targetNames)
            defines.Add("T" + t.Replace("/", "_").Replace("-", "_"));
        if (config == "Debug") defines.Add("DEBUG");
        if (config == "Trace") defines.Add("TRACE");
        defines.AddRange(targetDefines);
        defines.AddRange(componentDefines);
        defines.AddRange(profile.Defines ?? []);

        // === Aggregate the settings bag ===
        // Structural keys first, then the explicit settings maps in precedence
        // order (target chain, components, profile). Scalars resolve to the last
        // (most-specific) value; lists accumulate.
        var settings = new Settings.Builder();
        settings.Add("defines", defines);
        settings.Add("include-dirs", includeDirs);
        foreach (var map in settingsMaps)
            foreach (var (key, value) in map)
                settings.Add(key, NormalizeSettingValue(value));

        // Resolve the ld-script name to a path via the link search dirs and store
        // the concrete path back so the link step emits a usable -T argument.
        if (settings.Scalar("gcc.ld-script") is { } ldScriptName)
        {
            var searchDirs = settings.List("gcc.link-dirs")
                .Select(d => Path.IsPathRooted(d) ? d : "")
                .Where(d => d.Length > 0)
                .Concat(targetDirs).Concat(componentDirs);
            var ldScriptPath = searchDirs
                .Select(dir => Path.Combine(dir, ldScriptName))
                .FirstOrDefault(File.Exists) ?? ldScriptName;
            settings.Set("gcc.ld-script", ldScriptPath);
        }

        return new BuildConfiguration
        {
            Name = configName,
            Layout = layout,
            Target = primaryTarget,
            Config = config,
            Targets = targetNames,
            Components = resolvedComponents,
            TargetDirs = targetDirs,
            ComponentDirs = componentDirs,
            SourceDirs = sourceDirs,
            Pch = pch,
            StepRefs = stepRefs,
            Settings = settings.Build(),
            OutputNameOverride = overrides?.OutputName,
            OutputSubdir = overrides?.OutputSubdir,
        };
    }

    private static IEnumerable<string> NormalizeSettingValue(object? value) => value switch
    {
        null => [],
        string s => [s],
        System.Collections.IEnumerable e => e.Cast<object?>().Select(o => o?.ToString() ?? ""),
        _ => [value.ToString() ?? ""],
    };

    /// <summary>
    /// Resolves a source dir pattern that may contain glob wildcards.
    /// Returns list of actual directories.
    /// </summary>
    private static List<string> ResolveSourceDirPattern(string baseDir, string pattern)
    {
        var fullPattern = Path.Combine(baseDir, pattern);

        // No wildcard - just return the path if it exists
        if (!fullPattern.Contains('*') && !fullPattern.Contains('?'))
        {
            var resolved = Path.GetFullPath(fullPattern);
            return Directory.Exists(resolved) ? [resolved] : [];
        }

        // Walk up to the non-wildcard root, then glob below it.
        var fixedParts = new List<string>();
        var globParts = new List<string>();
        var foundWild = false;
        foreach (var part in pattern.Split('/', '\\'))
        {
            if (foundWild || part.Contains('*') || part.Contains('?'))
            {
                foundWild = true;
                globParts.Add(part);
            }
            else
            {
                fixedParts.Add(part);
            }
        }

        var searchRoot = fixedParts.Count > 0
            ? Path.GetFullPath(Path.Combine(baseDir, Path.Combine(fixedParts.ToArray())))
            : baseDir;
        if (!Directory.Exists(searchRoot))
            return [];

        var matcher = new Matcher();
        matcher.AddInclude(Path.Combine(globParts.ToArray()));

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(searchRoot)));
        return result.Files
            .Select(f => Path.GetFullPath(Path.Combine(searchRoot, Path.GetDirectoryName(f.Path) ?? "")))
            .Distinct()
            .Where(Directory.Exists)
            .ToList();
    }
}

/// <summary>
/// Overrides applied when building a single test suite rather than the main project.
/// </summary>
public record BuildOverrides
{
    /// <summary>
    /// Replaces the project src/ directory as the primary source location
    /// (the test suite directory).
    /// </summary>
    public string? PrimarySourceDir { get; init; }

    /// <summary>
    /// The full component set to build (testrunner + component-under-test +
    /// the suite's own dependencies). Replaces the profile's component list.
    /// </summary>
    public IReadOnlyList<string>? Components { get; init; }

    /// <summary>
    /// Extra targets injected into the chain (e.g. "test" for hardware stubs).
    /// </summary>
    public IReadOnlyList<string> ExtraTargets { get; init; } = [];

    /// <summary>
    /// Overrides the output binary name (the suite name).
    /// </summary>
    public string? OutputName { get; init; }

    /// <summary>
    /// Subdirectory under out/&lt;config&gt;/ for this suite's artifacts.
    /// </summary>
    public string? OutputSubdir { get; init; }
}
