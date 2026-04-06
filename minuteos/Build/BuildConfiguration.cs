namespace MinuteOS.Cli.Build;

/// <summary>
/// Represents the fully resolved build configuration for a minuteos project.
/// Built from a ProjectConfig + named configuration profile.
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

    public string OutputRoot => Path.Combine(Layout.ProjectRoot, "out", Name);
    public string ObjectDir => Path.Combine(OutputRoot, "obj");
    public string OutputName => Layout.Name;
    public string PrimaryOutput => Path.Combine(OutputRoot, OutputName + ".elf");

    public string GetObjectPath(SourceFile source)
    {
        var objRelative = Path.ChangeExtension(source.RelativePath, ".o");
        return Path.Combine(ObjectDir, objRelative);
    }

    /// <summary>
    /// Creates a build configuration from a project config and a named configuration.
    /// </summary>
    public static BuildConfiguration Create(ProjectConfig projectConfig, string configName, string projectRoot)
    {
        var profile = projectConfig.Resolve(configName);
        var layout = new ProjectLayout(projectRoot, projectConfig.Name);

        var target = profile.Target ?? "host";
        var config = profile.Config ?? "Release";

        // Target resolution: primary target + "all" pseudo-target
        var targets = new List<string> { target };
        var targetDirs = layout.ResolveTargetDirs(targets);

        // Component resolution with proper recursion
        var resolver = new ComponentResolver(layout);
        var requestedComponents = profile.Components ?? ["kernel"];
        var resolvedComponents = resolver.ResolveComponents(requestedComponents, targetDirs);

        var componentDirs = layout.ResolveComponentDirs(targetDirs, resolvedComponents);

        // Include dirs: extra from profile, project source, target dirs, target roots
        var includeDirs = new List<string>();
        if (profile.IncludeDirs != null)
        {
            foreach (var dir in profile.IncludeDirs)
                includeDirs.Add(Path.GetFullPath(Path.Combine(projectRoot, dir)));
        }
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
        var sources = collector.CollectSources(sourceDirs, projectRoot);

        // Defines: C<component>, T<target>, config-specific, plus extras from profile
        var defines = new List<string>();
        foreach (var c in resolvedComponents)
            defines.Add("C" + c.Replace("/", "_").Replace("-", "_"));
        foreach (var t in targets.Where(t => t != "all").Append("all").Distinct())
            defines.Add("T" + t.Replace("/", "_").Replace("-", "_"));
        if (config == "Debug")
            defines.Add("DEBUG");
        if (config == "Trace")
            defines.Add("TRACE");
        if (profile.Defines != null)
            defines.AddRange(profile.Defines);

        return new BuildConfiguration
        {
            Name = configName,
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
            Profile = profile,
        };
    }
}
