using MinuteOS.Build.Steps;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Build;

/// <summary>
/// Metadata loaded from a target directory's target.yaml file. Targets declare
/// parent targets (inheritance), extra components, build steps, and a generic
/// <see cref="Settings"/> map for all toolchain configuration.
///
/// Replaces per-target Include.mk, e.g.:
///   TARGETS += cortex-m        → requires: [cortex-m]
///   TOOLCHAIN_PREFIX = arm-..  → settings: { gcc.toolchain-prefix: arm-none-eabi- }
///   ARCH_FLAGS = -mcpu=...     → settings: { gcc.arch-flags: [...] }
/// </summary>
[YamlSerializable]
public class TargetMeta
{
    public const string FileName = "target.yaml";

    /// <summary>
    /// Parent targets this target inherits from.
    /// Replaces TARGETS += in Include.mk.
    /// </summary>
    public List<string>? Requires { get; set; }

    /// <summary>
    /// Preprocessor defines contributed by this target.
    /// </summary>
    public List<string>? Defines { get; set; }

    /// <summary>
    /// Additional components required by this target.
    /// </summary>
    public List<string>? Components { get; set; }

    /// <summary>
    /// Build steps contributed by this target.
    /// </summary>
    public List<StepReference>? Steps { get; set; }

    /// <summary>
    /// Generic, toolchain-agnostic settings merged into the build's Settings bag.
    /// Keys are canonical bag keys (flat <c>defines</c>/<c>include-dirs</c>,
    /// namespaced <c>gcc.*</c>); each value is a scalar or a list.
    /// </summary>
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>
    /// The directory this metadata was loaded from.
    /// </summary>
    [YamlIgnore]
    public string TargetDir { get; set; } = "";

    /// <summary>
    /// The target name.
    /// </summary>
    [YamlIgnore]
    public string Name { get; set; } = "";

    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new YamlContext())
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithTypeConverter(SettingsMapConverter.Instance)
        .Build();

    public static TargetMeta? TryLoad(string targetDir, string targetName)
    {
        var path = Path.Combine(targetDir, FileName);
        if (!File.Exists(path))
            return null;

        var yaml = File.ReadAllText(path);
        var meta = Deserializer.Deserialize<TargetMeta>(yaml) ?? new TargetMeta();
        meta.TargetDir = targetDir;
        meta.Name = targetName;
        return meta;
    }

    /// <summary>Serializes this metadata to YAML (target.yaml form).</summary>
    public string ToYaml() => MetaYaml.Serializer.Serialize(this);
}
