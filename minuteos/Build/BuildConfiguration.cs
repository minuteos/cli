using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Represents the fully resolved build configuration for a minuteos project.
/// Merges contributions from: project config, target metadata, and component metadata.
/// </summary>
public class BuildConfiguration
{
    public required string Name { get; init; }
    public required ProjectLayout Layout { get; init; }
    public required string Target { get; init; }
    public required string Config { get; init; }
    public required List<string> Targets { get; init; }
    public required List<string> Components { get; init; }
    public required List<string> TargetDirs { get; init; }
    public required List<string> ComponentDirs { get; init; }
    public required List<string> IncludeDirs { get; init; }
    public required List<string> SourceDirs { get; init; }
    public required List<SourceFile> Sources { get; init; }
    public required List<string> Defines { get; init; }
    public required ConfigurationProfile Profile { get; init; }
    public required List<StepReference> StepRefs { get; init; }
    public required IReadOnlyDictionary<string, ComponentMeta> ComponentMetadata { get; init; }
    public required IReadOnlyDictionary<string, TargetMeta> TargetMetadata { get; init; }
    public required List<string> ComponentCFlags { get; init; }
    public required List<string> ComponentCxxFlags { get; init; }
    public required List<string> ComponentLinkFlags { get; init; }

    /// <summary>
    /// Resolved primary extension (.elf, .axf, etc.)
    /// Precedence: profile > target metadata > default (.elf)
    /// </summary>
    public required string PrimaryExt { get; init; }

    /// <summary>
    /// Resolved linker script path, if any.
    /// </summary>
    public string? LdScript { get; init; }

    /// <summary>
    /// Additional linker search directories from targets.
    /// </summary>
    public required List<string> LinkDirs { get; init; }

    /// <summary>
    /// Aggregated, toolchain-agnostic settings consumed by build steps.
    /// The typed fields above are gradually becoming views over this.
    /// </summary>
    public required Settings Settings { get; init; }

    /// <summary>
    /// How to run compiled test binaries for this configuration, if any.
    /// </summary>
    public TestRunnerConfig? TestRunner { get; init; }

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

    public string GetObjectPath(SourceFile source)
    {
        var objRelative = Path.ChangeExtension(source.RelativePath, ".o");
        return Path.Combine(ObjectDir, objRelative);
    }

    public static BuildConfiguration Create(
        ProjectConfig projectConfig, string configName, string projectRoot, BuildOverrides? overrides = null)
    {
        var profile = projectConfig.Resolve(configName);
        var layout = new ProjectLayout(projectRoot, projectConfig.Name);

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

        // Merge target metadata contributions.
        string? targetToolchainPrefix = null;
        List<string>? targetArchFlags = null;
        string? targetPrimaryExt = null;
        string? targetLdScript = null;
        var targetDefines = new List<string>();
        var targetLinkFlags = new List<string>();
        var targetLinkDirs = new List<string>();
        var targetComponents = new List<string>();
        var stepRefs = new List<StepReference>();
        // Explicit toolchain-agnostic settings maps, gathered in precedence order
        // (target chain parents-first, then components, then profile) and merged
        // into the Settings bag after the typed-field folding below.
        var settingsMaps = new List<Dictionary<string, object>>();

        // targetNames are ordered parents-first, so iterating and keeping the
        // last value gives the most-specific (child) target precedence for
        // scalars; lists accumulate across the whole chain.
        foreach (var targetName in targetNames)
        {
            if (!targetMetadata.TryGetValue(targetName, out var tmeta))
                continue;

            // Scalars: most-specific (later) target wins
            if (tmeta.ToolchainPrefix != null) targetToolchainPrefix = tmeta.ToolchainPrefix;
            if (tmeta.ArchFlags != null) targetArchFlags = tmeta.ArchFlags;
            if (tmeta.PrimaryExt != null) targetPrimaryExt = tmeta.PrimaryExt;
            if (tmeta.LdScript != null) targetLdScript = tmeta.LdScript;

            // Lists: accumulate
            if (tmeta.Defines != null) targetDefines.AddRange(tmeta.Defines);
            if (tmeta.LinkFlags != null) targetLinkFlags.AddRange(tmeta.LinkFlags);
            if (tmeta.Components != null) targetComponents.AddRange(tmeta.Components);
            if (tmeta.Steps != null) stepRefs.AddRange(tmeta.Steps);
            if (tmeta.Settings != null) settingsMaps.Add(tmeta.Settings);

            if (tmeta.LinkDirs != null)
            {
                foreach (var dir in tmeta.LinkDirs)
                    targetLinkDirs.Add(Path.GetFullPath(Path.Combine(tmeta.TargetDir, dir)));
            }
        }

        // The profile overrides whatever the targets resolved to.
        var resolvedToolchainPrefix = profile.ToolchainPrefix ?? targetToolchainPrefix;
        var resolvedArchFlags = profile.ArchFlags ?? targetArchFlags;
        var resolvedPrimaryExt = profile.PrimaryExt ?? targetPrimaryExt ?? ".elf";
        var resolvedLdScript = profile.LdScript ?? targetLdScript;

        // === Component resolution ===
        // Test builds supply their own component set (testrunner + component-under-test).
        var baseComponents = overrides?.Components ?? profile.Components ?? ["kernel"];
        var resolver = new ComponentResolver(layout);
        var requestedComponents = new List<string>();
        requestedComponents.AddRange(targetComponents);
        requestedComponents.AddRange(baseComponents);
        var resolvedComponents = resolver.ResolveComponents(requestedComponents, targetDirs);
        var componentMeta = resolver.ComponentMetadata;

        var componentDirs = layout.ResolveComponentDirs(targetDirs, resolvedComponents);

        // === Merge component metadata ===
        var componentDefines = new List<string>();
        var componentIncludeDirs = new List<string>();
        var componentSourceDirs = new List<string>();
        var componentCFlags = new List<string>();
        var componentCxxFlags = new List<string>();
        var componentLinkFlags = new List<string>();

        foreach (var component in resolvedComponents)
        {
            if (!componentMeta.TryGetValue(component, out var meta))
                continue;

            if (meta.Defines != null)
                componentDefines.AddRange(meta.Defines);

            if (meta.IncludeDirs != null)
            {
                foreach (var dir in meta.IncludeDirs)
                    componentIncludeDirs.Add(Path.GetFullPath(Path.Combine(meta.ComponentDir, dir)));
            }

            if (meta.SourceDirs != null)
            {
                foreach (var pattern in meta.SourceDirs)
                {
                    var resolved = ResolveSourceDirPattern(meta.ComponentDir, pattern);
                    componentSourceDirs.AddRange(resolved);
                }
            }

            if (meta.CFlags != null) componentCFlags.AddRange(meta.CFlags);
            if (meta.CxxFlags != null) componentCxxFlags.AddRange(meta.CxxFlags);
            if (meta.LinkFlags != null) componentLinkFlags.AddRange(meta.LinkFlags);
            if (meta.Steps != null) stepRefs.AddRange(meta.Steps);
            if (meta.Settings != null) settingsMaps.Add(meta.Settings);
        }

        // Steps from project-level config
        if (profile.Steps != null)
            stepRefs.AddRange(profile.Steps);

        // Profile settings have the highest precedence (merged last).
        if (profile.Settings != null)
            settingsMaps.Add(profile.Settings);

        // === Assemble include dirs ===
        var includeDirs = new List<string>();
        includeDirs.AddRange(componentIncludeDirs);
        if (profile.IncludeDirs != null)
        {
            foreach (var dir in profile.IncludeDirs)
                includeDirs.Add(Path.GetFullPath(Path.Combine(projectRoot, dir)));
        }
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

        var collector = new SourceCollector();
        var sources = collector.CollectSources(sourceDirs, projectRoot);

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
        var defines = new List<string>();
        foreach (var c in resolvedComponents)
            defines.Add("C" + c.Replace("/", "_").Replace("-", "_"));
        foreach (var t in targetNames)
            defines.Add("T" + t.Replace("/", "_").Replace("-", "_"));
        if (config == "Debug") defines.Add("DEBUG");
        if (config == "Trace") defines.Add("TRACE");
        defines.AddRange(targetDefines);
        defines.AddRange(componentDefines);
        if (profile.Defines != null)
            defines.AddRange(profile.Defines);

        // The ld-script is resolved (bare name -> path) after the settings bag is
        // assembled, so an explicit `settings: { gcc.ld-script: ... }` is honored.
        string? ldScriptPath = null;

        // Resolve test runner: profile takes precedence, else the most specific
        // target (child-first) that declares one.
        var resolvedTestRunner = profile.TestRunner;
        if (resolvedTestRunner == null)
        {
            for (int i = targetNames.Count - 1; i >= 0 && resolvedTestRunner == null; i--)
            {
                if (targetMetadata.TryGetValue(targetNames[i], out var tmeta))
                    resolvedTestRunner = tmeta.TestRunner;
            }
        }

        // Aggregate the toolchain-agnostic settings bag. The typed fields fold in
        // first (a deprecated alias), then explicit `settings:` maps are merged on
        // top - so settings is the single source of truth for the steps.
        var settings = new Settings.Builder();
        settings.Add("defines", defines);
        settings.Add("include-dirs", includeDirs);
        settings.Add("gcc.arch-flags", resolvedArchFlags);
        settings.Add("gcc.c-flags", (profile.CFlags ?? []).Concat(componentCFlags));
        settings.Add("gcc.cxx-flags", (profile.CxxFlags ?? []).Concat(componentCxxFlags));
        // Target link-flags (e.g. --specs/-nostartfiles) first, then profile +
        // components.
        settings.Add("gcc.link-flags", targetLinkFlags);
        settings.Add("gcc.link-flags", (profile.LinkFlags ?? []).Concat(componentLinkFlags));
        settings.Add("gcc.link-dirs", targetLinkDirs);
        settings.Set("gcc.toolchain-prefix", resolvedToolchainPrefix);
        settings.Set("gcc.ld-script", resolvedLdScript);
        settings.Set("gcc.primary-ext", resolvedPrimaryExt);

        // Merge the explicit settings maps (target chain, components, profile, in
        // precedence order). Scalars resolve to the last (most-specific) value.
        foreach (var map in settingsMaps)
            MergeSettings(settings, map);

        // Re-derive the resolved scalars from the now-authoritative bag so an
        // explicit settings entry takes effect for these too.
        resolvedToolchainPrefix = settings.Scalar("gcc.toolchain-prefix");
        resolvedPrimaryExt = settings.Scalar("gcc.primary-ext") ?? ".elf";

        // Resolve the ld-script name to a path via the link search dirs and store
        // the concrete path back so the link step emits a usable -T argument.
        var ldScriptName = settings.Scalar("gcc.ld-script");
        if (ldScriptName != null)
        {
            foreach (var dir in targetLinkDirs.Concat(targetDirs).Concat(componentDirs))
            {
                var candidate = Path.Combine(dir, ldScriptName);
                if (File.Exists(candidate)) { ldScriptPath = candidate; break; }
            }
            ldScriptPath ??= ldScriptName;
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
            IncludeDirs = includeDirs,
            SourceDirs = sourceDirs,
            Sources = sources,
            Defines = defines,
            Pch = pch,
            Profile = new ConfigurationProfile
            {
                Target = primaryTarget,
                Config = config,
                Components = profile.Components,
                ToolchainPrefix = resolvedToolchainPrefix,
                ArchFlags = resolvedArchFlags,
                Defines = profile.Defines,
                LinkFlags = profile.LinkFlags,
                IncludeDirs = profile.IncludeDirs,
                CFlags = profile.CFlags,
                CxxFlags = profile.CxxFlags,
                PrimaryExt = resolvedPrimaryExt,
                LdScript = resolvedLdScript,
                Steps = profile.Steps,
            },
            StepRefs = stepRefs,
            ComponentMetadata = componentMeta,
            TargetMetadata = targetMetadata,
            ComponentCFlags = componentCFlags,
            ComponentCxxFlags = componentCxxFlags,
            ComponentLinkFlags = componentLinkFlags,
            PrimaryExt = resolvedPrimaryExt,
            LdScript = ldScriptPath,
            LinkDirs = targetLinkDirs,
            Settings = settings.Build(),
            TestRunner = resolvedTestRunner,
            OutputNameOverride = overrides?.OutputName,
            OutputSubdir = overrides?.OutputSubdir,
        };
    }

    /// <summary>
    /// Merges a raw YAML settings map (each value a scalar or a sequence) into the
    /// Settings bag, appending so list keys accumulate and scalar keys resolve to
    /// the most-specific (last-merged) value.
    /// </summary>
    private static void MergeSettings(Settings.Builder settings, Dictionary<string, object> map)
    {
        foreach (var (key, value) in map)
            settings.Add(key, NormalizeSettingValue(value));
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

        // Use glob matching for wildcard patterns
        var searchRoot = baseDir;
        var globPattern = pattern;

        // Walk up to find the non-wildcard root
        var parts = pattern.Split('/', '\\');
        var fixedParts = new List<string>();
        var globParts = new List<string>();
        var foundWild = false;
        foreach (var part in parts)
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

        if (fixedParts.Count > 0)
            searchRoot = Path.GetFullPath(Path.Combine(baseDir, Path.Combine(fixedParts.ToArray())));

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
