using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace MinuteOS.Build;

/// <summary>
/// AOT-safe (de)serializer for a top-level <c>Dictionary&lt;string, string&gt;</c>
/// (the dependency lock file: name → commit). Registering a
/// <c>Dictionary&lt;,&gt;</c> as a top-level <c>[YamlSerializable]</c> trips a bug
/// in the static generator, so the lock map is read/written directly through
/// the parser/emitter instead - no reflection.
/// </summary>
public sealed class StringMapConverter : IYamlTypeConverter
{
    public static readonly StringMapConverter Instance = new();

    public bool Accepts(Type type) => type == typeof(Dictionary<string, string>);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var result = new Dictionary<string, string>();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            result[key] = parser.Consume<Scalar>().Value;
        }
        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var map = (Dictionary<string, string>)value!;
        emitter.Emit(new MappingStart());
        foreach (var (key, item) in map)
        {
            emitter.Emit(new Scalar(key));
            emitter.Emit(new Scalar(item));
        }
        emitter.Emit(new MappingEnd());
    }
}
