using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Shared YAML serializer for component/target metadata. Uses the same
/// hyphenated naming and member aliases as the loaders, and omits null members
/// so generated files only contain what was actually set.
/// </summary>
internal static class MetaYaml
{
    public static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        // Quote scalars that would otherwise be read back as null/bool/number,
        // e.g. qemu's `-monitor null` arg must stay the string "null".
        .WithQuotingNecessaryStrings()
        .Build();
}
