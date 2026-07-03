using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace MinuteOS.Build;

/// <summary>
/// AOT-safe (de)serializer for the <c>Dictionary&lt;string, object&gt;</c>
/// settings maps. The static YamlDotNet source generator can't emit code for
/// <c>object</c>-typed values, so this converter reads/writes them directly
/// through the parser/emitter - no reflection. Values are a scalar (kept as a
/// <see cref="string"/>) or a sequence of scalars (a <c>List&lt;object&gt;</c>
/// of strings), matching what the reflection deserializer produced and what
/// the settings bag downstream expects. Scalars that would round-trip as
/// null/bool/number are double-quoted on write (the old
/// <c>WithQuotingNecessaryStrings</c> behaviour), so e.g. <c>-monitor null</c>
/// stays the string "null".
/// </summary>
public sealed class SettingsMapConverter : IYamlTypeConverter
{
    public static readonly SettingsMapConverter Instance = new();

    public bool Accepts(Type type) => type == typeof(Dictionary<string, object>);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var result = new Dictionary<string, object>();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            result[key] = ReadValue(parser);
        }
        return result;
    }

    private static object ReadValue(IParser parser)
    {
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var list = new List<object>();
            while (!parser.TryConsume<SequenceEnd>(out _))
                list.Add(parser.Consume<Scalar>().Value);
            return list;
        }
        return parser.Consume<Scalar>().Value;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var map = (Dictionary<string, object>)value!;
        emitter.Emit(new MappingStart());
        foreach (var (key, item) in map)
        {
            emitter.Emit(new Scalar(key));
            if (item is IEnumerable<object> list)
            {
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
                foreach (var element in list)
                    EmitScalar(emitter, element?.ToString() ?? "");
                emitter.Emit(new SequenceEnd());
            }
            else
            {
                EmitScalar(emitter, item?.ToString() ?? "");
            }
        }
        emitter.Emit(new MappingEnd());
    }

    private static void EmitScalar(IEmitter emitter, string value)
    {
        var style = NeedsQuoting(value) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, style, isPlainImplicit: true, isQuotedImplicit: true));
    }

    /// <summary>A scalar that YAML would otherwise read back as null/bool/number.</summary>
    private static bool NeedsQuoting(string value)
        => value.Length == 0
            || value is "null" or "Null" or "NULL" or "~"
                     or "true" or "True" or "false" or "False"
            || double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _);
}
