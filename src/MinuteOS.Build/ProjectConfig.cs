using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Build;

/// <summary>
/// YAML project configuration model.
/// Loaded from minuteos.yaml in the project root.
/// </summary>
[YamlSerializable]
public class ProjectConfig
{
    public const string FileName = "minuteos.yaml";

    public string? Name { get; set; }

    /// <summary>
    /// External dependencies (typically git submodules) providing lib roots such
    /// as <c>lib</c> / <c>lib-arm</c>. Restored by <c>minuteos restore</c>; a lib
    /// dir already present on disk is used whether or not it is declared here.
    /// </summary>
    public List<Dependency>? Dependencies { get; set; }

    /// <summary>
    /// Default settings inherited by all configurations.
    /// </summary>
    public ConfigurationProfile Defaults { get; set; } = new();

    /// <summary>
    /// Named build configurations. Each inherits from Defaults
    /// and can override any setting.
    /// </summary>
    public Dictionary<string, ConfigurationProfile> Configurations { get; set; } = new();

    /// <summary>
    /// Resolves a named configuration by merging it with defaults.
    /// </summary>
    public ConfigurationProfile Resolve(string configName)
    {
        if (!Configurations.TryGetValue(configName, out var profile))
            throw new InvalidOperationException(
                $"Unknown configuration '{configName}'. Available: {string.Join(", ", Configurations.Keys)}");

        return Defaults.MergeWith(profile);
    }

    /// <summary>
    /// Lists all available configuration names.
    /// </summary>
    public IEnumerable<string> ConfigurationNames => Configurations.Keys;

    public static ProjectConfig Load(string projectRoot)
    {
        var path = Path.Combine(projectRoot, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No {FileName} found in {projectRoot}. Run 'minuteos init' to create one.");

        var yaml = File.ReadAllText(path);
        var deserializer = new StaticDeserializerBuilder(new YamlContext())
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .WithTypeConverter(SettingsMapConverter.Instance)
            .Build();

        var config = deserializer.Deserialize<ProjectConfig>(yaml)
            ?? throw new InvalidOperationException($"Failed to parse {FileName}");

        config.Name ??= Path.GetFileName(Path.GetFullPath(projectRoot));
        return config;
    }

    public static string GetProjectRoot(string? explicitPath)
    {
        if (explicitPath != null)
            return Path.GetFullPath(explicitPath);

        // Walk up looking for minuteos.yaml
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, FileName)))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// One configuration in <c>minuteos.yaml</c>: structural selections (target,
/// components, dirs, steps) plus a generic <see cref="Settings"/> map for all
/// toolchain configuration (flat <c>defines</c>/<c>include-dirs</c>, namespaced
/// <c>gcc.*</c>).
/// </summary>
[YamlSerializable]
public class ConfigurationProfile
{
    public string? Target { get; set; }
    public string? Config { get; set; }
    public List<string>? Components { get; set; }

    public List<string>? Defines { get; set; }

    [YamlMember(Alias = "include-dirs")]
    public List<string>? IncludeDirs { get; set; }

    /// <summary>
    /// Overrides the project source directory (default: src/). Used by sub-build
    /// configurations like a bootloader that build from their own sources.
    /// </summary>
    [YamlMember(Alias = "source-dir")]
    public string? SourceDir { get; set; }

    /// <summary>
    /// Build steps to run. Components and targets also contribute steps.
    /// </summary>
    public List<StepReference>? Steps { get; set; }

    /// <summary>
    /// Generic, toolchain-agnostic settings merged into the build's Settings bag
    /// (each value a scalar or list).
    /// </summary>
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>
    /// Returns a new profile with values from 'other' taking precedence over this one.
    /// Lists are replaced, not merged - the override fully owns the list if specified.
    /// </summary>
    public ConfigurationProfile MergeWith(ConfigurationProfile other) => new()
    {
        Target = other.Target ?? Target,
        Config = other.Config ?? Config,
        Components = other.Components ?? Components,
        Defines = other.Defines ?? Defines,
        IncludeDirs = other.IncludeDirs ?? IncludeDirs,
        SourceDir = other.SourceDir ?? SourceDir,
        Steps = other.Steps ?? Steps,
        Settings = other.Settings ?? Settings,
    };
}
