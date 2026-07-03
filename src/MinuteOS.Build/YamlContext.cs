using YamlDotNet.Serialization;

namespace MinuteOS.Build;

/// <summary>
/// Compile-time YAML type context (NativeAOT/trim compatible). The
/// Vecc.YamlDotNet.Analyzers.StaticGenerator source generator emits static
/// (de)serialization code for every <c>[YamlSerializable]</c> model and the
/// framework generics registered here, so config loading needs no reflection
/// at runtime. Consumed by the <c>StaticDeserializerBuilder</c>/
/// <c>StaticSerializerBuilder</c> in the model loaders.
/// </summary>
// The settings maps (Dictionary<string, object>) and the lock file
// (Dictionary<string, string>) are intentionally NOT registered here: the
// generator can't model `object` values, and registering a Dictionary<,> as a
// top-level [YamlSerializable] trips an IndexOutOfRange bug in the generator's
// TypeFactoryGenerator. Both are handled at runtime by SettingsMapConverter /
// StringMapConverter without reflection. Dictionary<,> *properties* on the
// models below are fine and need no registration.
[YamlStaticContext]
public partial class YamlContext : StaticContext
{
}
