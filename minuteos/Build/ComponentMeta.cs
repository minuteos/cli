using MinuteOS.Cli.Build.Steps;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Metadata loaded from a component's component.yaml file.
/// This is how components declare their dependencies, build contributions,
/// and build steps - replacing the old Include.mk system.
/// </summary>
public class ComponentMeta
{
    public const string FileName = "component.yaml";

    /// <summary>
    /// Other components this component depends on.
    /// </summary>
    public List<string>? Requires { get; set; }

    /// <summary>
    /// Preprocessor defines contributed by this component.
    /// </summary>
    public List<string>? Defines { get; set; }

    /// <summary>
    /// Additional include directories (relative to the component directory).
    /// </summary>
    [YamlMember(Alias = "include-dirs")]
    public List<string>? IncludeDirs { get; set; }

    /// <summary>
    /// Extra C compiler flags.
    /// </summary>
    [YamlMember(Alias = "c-flags")]
    public List<string>? CFlags { get; set; }

    /// <summary>
    /// Extra C++ compiler flags.
    /// </summary>
    [YamlMember(Alias = "cxx-flags")]
    public List<string>? CxxFlags { get; set; }

    /// <summary>
    /// Extra linker flags.
    /// </summary>
    [YamlMember(Alias = "link-flags")]
    public List<string>? LinkFlags { get; set; }

    /// <summary>
    /// Additional source directories (relative to the lib root, supports glob patterns).
    /// Used by wrapper components like fatfs/lvgl that reference external source trees.
    /// Example: "../../../fatfs/source/" or "../../lvgl/src/draw/sw/blend/"
    /// </summary>
    [YamlMember(Alias = "source-dirs")]
    public List<string>? SourceDirs { get; set; }

    /// <summary>
    /// Build steps contributed by this component.
    /// Each entry references a step by name, optionally with configuration.
    /// </summary>
    public List<StepReference>? Steps { get; set; }

    /// <summary>
    /// Generic, toolchain-agnostic settings merged into the build's Settings bag
    /// (e.g. <c>gcc.c-flags</c>). The forward-looking form that <c>migrate</c>
    /// emits; the typed flag fields above are a deprecated alias.
    /// </summary>
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>
    /// Source file patterns to exclude from compilation.
    /// </summary>
    [YamlMember(Alias = "exclude-sources")]
    public List<string>? ExcludeSources { get; set; }

    /// <summary>
    /// The directory this metadata was loaded from.
    /// </summary>
    [YamlIgnore]
    public string ComponentDir { get; set; } = "";

    /// <summary>
    /// The component name (derived from directory name).
    /// </summary>
    [YamlIgnore]
    public string Name { get; set; } = "";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ComponentMeta? TryLoad(string componentDir, string componentName)
    {
        var path = Path.Combine(componentDir, FileName);
        if (!File.Exists(path))
            return null;

        var yaml = File.ReadAllText(path);
        var meta = Deserializer.Deserialize<ComponentMeta>(yaml) ?? new ComponentMeta();
        meta.ComponentDir = componentDir;
        meta.Name = componentName;
        return meta;
    }

    /// <summary>Serializes this metadata to YAML (component.yaml form).</summary>
    public string ToYaml() => MetaYaml.Serializer.Serialize(this);
}

/// <summary>
/// Reference to a build step from component.yaml.
/// Can be a simple name or include configuration.
/// </summary>
public class StepReference
{
    /// <summary>
    /// Step name (matches a registered IBuildStep).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// When this step runs relative to the build phases.
    /// </summary>
    public BuildPhase? Phase { get; set; }

    /// <summary>
    /// Arbitrary key-value configuration passed to the step.
    /// </summary>
    public Dictionary<string, string>? Config { get; set; }
}
