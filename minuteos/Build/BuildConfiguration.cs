namespace MinuteOS.Cli.Build;

/// <summary>
/// Represents the full build configuration for a minuteos project.
/// Aggregates project layout, target/component resolution, and compiler settings.
/// </summary>
public class BuildConfiguration
{
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

    public string OutputRoot => Path.Combine(Layout.ProjectRoot, "out", Target, Config);
    public string ObjectDir => Path.Combine(OutputRoot, "obj");
    public string OutputName => Layout.Name;
    public string PrimaryOutput => Path.Combine(OutputRoot, OutputName + ".elf");

    /// <summary>
    /// Gets the object file path for a given source file.
    /// </summary>
    public string GetObjectPath(SourceFile source)
    {
        var objRelative = Path.ChangeExtension(source.RelativePath, ".o");
        return Path.Combine(ObjectDir, objRelative);
    }

    /// <summary>
    /// Creates a build configuration from project settings.
    /// </summary>
    public static BuildConfiguration Create(
        string projectRoot,
        string target = "host",
        string config = "Release",
        IEnumerable<string>? components = null,
        IEnumerable<string>? additionalTargets = null)
    {
        var layout = new ProjectLayout(projectRoot);

        var targets = new List<string> { target };
        if (additionalTargets != null)
            targets.AddRange(additionalTargets);

        var targetDirs = layout.ResolveTargetDirs(targets);

        var resolver = new ComponentResolver(layout);
        var resolvedComponents = resolver.ResolveComponents(
            components ?? ["kernel"],
            targetDirs);

        var componentDirs = layout.ResolveComponentDirs(targetDirs, resolvedComponents);

        var libDirs = targetDirs.Concat(componentDirs).ToList();

        // Include dirs: project source, target dirs, target roots
        var includeDirs = new List<string>();
        if (Directory.Exists(layout.SourceDir))
            includeDirs.Add(layout.SourceDir);
        includeDirs.AddRange(targetDirs);
        includeDirs.AddRange(layout.TargetRoots);

        // Source dirs: project source, target dirs, component dirs
        var sourceDirs = new List<string>();
        if (Directory.Exists(layout.SourceDir))
            sourceDirs.Add(layout.SourceDir);
        sourceDirs.AddRange(targetDirs);
        sourceDirs.AddRange(componentDirs);
        sourceDirs = sourceDirs.Distinct().ToList();

        var collector = new SourceCollector();
        var sources = collector.CollectSources(sourceDirs, layout.ProjectRoot);

        // Generate defines: C<component> and T<target> macros
        var defines = new List<string>();
        foreach (var c in resolvedComponents)
            defines.Add("C" + c.Replace("/", "_").Replace("-", "_"));
        foreach (var t in targets.Where(t => t != "all").Append("all").Distinct())
            defines.Add("T" + t.Replace("/", "_").Replace("-", "_"));

        return new BuildConfiguration
        {
            Layout = layout,
            Target = target,
            Config = config,
            Targets = targets,
            Components = resolvedComponents,
            TargetDirs = targetDirs,
            ComponentDirs = componentDirs,
            IncludeDirs = includeDirs,
            SourceDirs = sourceDirs,
            Sources = sources,
            Defines = defines,
        };
    }
}
