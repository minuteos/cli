namespace MinuteOS.Cli.Build;

/// <summary>
/// Represents the fully resolved build configuration for a minuteos project.
/// Built from a ProjectConfig + named configuration profile + component metadata.
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

    /// <summary>
    /// Build step references collected from all components and the project config.
    /// </summary>
    public required List<StepReference> StepRefs { get; init; }

    /// <summary>
    /// Component metadata collected during resolution.
    /// </summary>
    public required IReadOnlyDictionary<string, ComponentMeta> ComponentMetadata { get; init; }

    /// <summary>
    /// Extra C flags contributed by components.
    /// </summary>
    public required List<string> ComponentCFlags { get; init; }

    /// <summary>
    /// Extra C++ flags contributed by components.
    /// </summary>
    public required List<string> ComponentCxxFlags { get; init; }

    /// <summary>
    /// Extra link flags contributed by components.
    /// </summary>
    public required List<string> ComponentLinkFlags { get; init; }

    public string OutputRoot => Path.Combine(Layout.ProjectRoot, "out", Name);
    public string ObjectDir => Path.Combine(OutputRoot, "obj");
    public string OutputName => Layout.Name;
    public string PrimaryOutput => Path.Combine(OutputRoot, OutputName + ".elf");

    public string GetObjectPath(SourceFile source)
    {
        var objRelative = Path.ChangeExtension(source.RelativePath, ".o");
        return Path.Combine(ObjectDir, objRelative);
    }

    public static BuildConfiguration Create(ProjectConfig projectConfig, string configName, string projectRoot)
    {
        var profile = projectConfig.Resolve(configName);
        var layout = new ProjectLayout(projectRoot, projectConfig.Name);

        var target = profile.Target ?? "host";
        var config = profile.Config ?? "Release";

        var targets = new List<string> { target };
        var targetDirs = layout.ResolveTargetDirs(targets);

        // Component resolution - also collects component.yaml metadata
        var resolver = new ComponentResolver(layout);
        var requestedComponents = profile.Components ?? ["kernel"];
        var resolvedComponents = resolver.ResolveComponents(requestedComponents, targetDirs);
        var componentMeta = resolver.ComponentMetadata;

        var componentDirs = layout.ResolveComponentDirs(targetDirs, resolvedComponents);

        // Merge contributions from all component metadata
        var componentDefines = new List<string>();
        var componentIncludeDirs = new List<string>();
        var componentCFlags = new List<string>();
        var componentCxxFlags = new List<string>();
        var componentLinkFlags = new List<string>();
        var stepRefs = new List<StepReference>();

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

            if (meta.CFlags != null)
                componentCFlags.AddRange(meta.CFlags);
            if (meta.CxxFlags != null)
                componentCxxFlags.AddRange(meta.CxxFlags);
            if (meta.LinkFlags != null)
                componentLinkFlags.AddRange(meta.LinkFlags);
            if (meta.Steps != null)
                stepRefs.AddRange(meta.Steps);
        }

        // Also collect steps from the project-level config
        if (profile.Steps != null)
            stepRefs.AddRange(profile.Steps);

        // Include dirs: component contributions, profile overrides, project source, target dirs, target roots
        var includeDirs = new List<string>();
        includeDirs.AddRange(componentIncludeDirs);
        if (profile.IncludeDirs != null)
        {
            foreach (var dir in profile.IncludeDirs)
                includeDirs.Add(Path.GetFullPath(Path.Combine(projectRoot, dir)));
        }
        if (Directory.Exists(layout.SourceDir))
            includeDirs.Add(layout.SourceDir);
        includeDirs.AddRange(targetDirs);
        includeDirs.AddRange(layout.TargetRoots);

        // Source dirs
        var sourceDirs = new List<string>();
        if (Directory.Exists(layout.SourceDir))
            sourceDirs.Add(layout.SourceDir);
        sourceDirs.AddRange(targetDirs);
        sourceDirs.AddRange(componentDirs);
        sourceDirs = sourceDirs.Distinct().ToList();

        var collector = new SourceCollector();
        var sources = collector.CollectSources(sourceDirs, projectRoot);

        // Defines: C<component>, T<target>, config-specific, component contributions, profile extras
        var defines = new List<string>();
        foreach (var c in resolvedComponents)
            defines.Add("C" + c.Replace("/", "_").Replace("-", "_"));
        foreach (var t in targets.Where(t => t != "all").Append("all").Distinct())
            defines.Add("T" + t.Replace("/", "_").Replace("-", "_"));
        if (config == "Debug")
            defines.Add("DEBUG");
        if (config == "Trace")
            defines.Add("TRACE");
        defines.AddRange(componentDefines);
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
            StepRefs = stepRefs,
            ComponentMetadata = componentMeta,
            ComponentCFlags = componentCFlags,
            ComponentCxxFlags = componentCxxFlags,
            ComponentLinkFlags = componentLinkFlags,
        };
    }
}
