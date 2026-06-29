using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// YAML project configuration model.
/// Loaded from minuteos.yaml in the project root.
/// </summary>
public class ProjectConfig
{
    public const string FileName = "minuteos.yaml";

    public string? Name { get; set; }

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
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
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

public class ConfigurationProfile
{
    public string? Target { get; set; }
    public string? Config { get; set; }
    public List<string>? Components { get; set; }

    [YamlMember(Alias = "toolchain-prefix")]
    public string? ToolchainPrefix { get; set; }

    [YamlMember(Alias = "arch-flags")]
    public List<string>? ArchFlags { get; set; }

    public List<string>? Defines { get; set; }

    [YamlMember(Alias = "link-flags")]
    public List<string>? LinkFlags { get; set; }

    [YamlMember(Alias = "include-dirs")]
    public List<string>? IncludeDirs { get; set; }

    [YamlMember(Alias = "c-flags")]
    public List<string>? CFlags { get; set; }

    [YamlMember(Alias = "cxx-flags")]
    public List<string>? CxxFlags { get; set; }

    /// <summary>
    /// Primary output extension (e.g. ".elf", ".axf").
    /// </summary>
    [YamlMember(Alias = "primary-ext")]
    public string? PrimaryExt { get; set; }

    /// <summary>
    /// Linker script file name.
    /// </summary>
    [YamlMember(Alias = "ld-script")]
    public string? LdScript { get; set; }

    /// <summary>
    /// Build steps to run. Components and targets also contribute steps.
    /// </summary>
    public List<StepReference>? Steps { get; set; }

    /// <summary>
    /// How to execute compiled test binaries for this configuration.
    /// Targets can also provide one (e.g. an emulator); the profile takes precedence.
    /// </summary>
    [YamlMember(Alias = "test-runner")]
    public TestRunnerConfig? TestRunner { get; set; }

    /// <summary>
    /// Returns a new profile with values from 'other' taking precedence over this one.
    /// Lists are replaced, not merged - the override fully owns the list if specified.
    /// </summary>
    public ConfigurationProfile MergeWith(ConfigurationProfile other) => new()
    {
        Target = other.Target ?? Target,
        Config = other.Config ?? Config,
        Components = other.Components ?? Components,
        ToolchainPrefix = other.ToolchainPrefix ?? ToolchainPrefix,
        ArchFlags = other.ArchFlags ?? ArchFlags,
        Defines = other.Defines ?? Defines,
        LinkFlags = other.LinkFlags ?? LinkFlags,
        IncludeDirs = other.IncludeDirs ?? IncludeDirs,
        CFlags = other.CFlags ?? CFlags,
        CxxFlags = other.CxxFlags ?? CxxFlags,
        PrimaryExt = other.PrimaryExt ?? PrimaryExt,
        LdScript = other.LdScript ?? LdScript,
        Steps = other.Steps ?? Steps,
        TestRunner = other.TestRunner ?? TestRunner,
    };
}
