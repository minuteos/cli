using MinuteOS.Build.Steps;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Build;

/// <summary>
/// Metadata loaded from a target directory's target.yaml file.
/// Targets can declare parent targets (inheritance), toolchain configuration,
/// architecture flags, linker settings, and build steps.
///
/// This replaces what was previously done in per-target Include.mk files, e.g.:
///   TARGETS += cortex-m        → requires: [cortex-m]
///   TOOLCHAIN_PREFIX = arm-..  → toolchain-prefix: arm-none-eabi-
///   ARCH_FLAGS = -mcpu=...     → arch-flags: [...]
///   LINK_FLAGS += -T...        → link-flags: [...]
///   PRIMARY_EXT = .axf         → primary-ext: .axf
/// </summary>
public class TargetMeta
{
    public const string FileName = "target.yaml";

    /// <summary>
    /// Parent targets this target inherits from.
    /// Replaces TARGETS += in Include.mk.
    /// </summary>
    public List<string>? Requires { get; set; }

    /// <summary>
    /// Toolchain prefix (e.g. "arm-none-eabi-").
    /// </summary>
    [YamlMember(Alias = "toolchain-prefix")]
    public string? ToolchainPrefix { get; set; }

    /// <summary>
    /// Architecture-specific compiler/linker flags.
    /// </summary>
    [YamlMember(Alias = "arch-flags")]
    public List<string>? ArchFlags { get; set; }

    /// <summary>
    /// Additional linker flags.
    /// </summary>
    [YamlMember(Alias = "link-flags")]
    public List<string>? LinkFlags { get; set; }

    /// <summary>
    /// Primary output file extension (e.g. ".axf" for ARM).
    /// </summary>
    [YamlMember(Alias = "primary-ext")]
    public string? PrimaryExt { get; set; }

    /// <summary>
    /// Linker script file name. Resolved via link search paths.
    /// </summary>
    [YamlMember(Alias = "ld-script")]
    public string? LdScript { get; set; }

    /// <summary>
    /// Additional linker search directories (relative to target directory).
    /// </summary>
    [YamlMember(Alias = "link-dirs")]
    public List<string>? LinkDirs { get; set; }

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
    /// namespaced <c>gcc.*</c>); each value is a scalar or a list. This is the
    /// forward-looking form that <c>migrate</c> emits; the typed gcc fields above
    /// are a deprecated alias kept for backward compatibility.
    /// </summary>
    public Dictionary<string, object>? Settings { get; set; }

    /// <summary>
    /// How to execute compiled test binaries on this target (e.g. an emulator).
    /// Deprecated: prefer a <c>run</c>/<c>qemu</c>/<c>renode</c> step (phase Run).
    /// </summary>
    [YamlMember(Alias = "test-runner")]
    public TestRunnerConfig? TestRunner { get; set; }

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

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
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
