using System.Text;
using System.Text.Json;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Renders a timeline as the Chrome Trace Event / Perfetto JSON format, so the
/// same lossless <c>mtrace</c> data opens in Perfetto or chrome://tracing:
/// power channels become counter tracks, logs and marks become instant events,
/// and PC samples become sample events with a symbolicated stack-frame table.
/// Timestamps are microseconds (the format's unit).
///
/// The events are streamed straight to the output through a
/// <see cref="Utf8JsonWriter"/>: only the channel table and the
/// distinct-function frame table stay resident, so a multi-gigabyte recording
/// exports in bounded memory. <c>stackFrames</c> is emitted after
/// <c>traceEvents</c> - JSON object member order is irrelevant to the reader.
/// </summary>
public static class PerfettoExport
{
    private const int Pid = 1;
    private const int LogTid = 2;
    private const int SampleTid = 3;
    private const int FlushEvery = 4096;

    public static async Task WriteAsync(Stream output, IEnumerable<TraceEvent> events, Symbolizer? symbolizer,
        CancellationToken cancellationToken = default)
    {
        await using var writer = new Utf8JsonWriter(output);
        var frameIds = new Dictionary<string, int>();
        var channels = new Dictionary<int, ChannelDefEvent>();

        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ns");
        writer.WriteStartArray("traceEvents");

        Metadata(writer, "process_name", 0, "minuteos");
        Metadata(writer, "thread_name", LogTid, "logs");
        Metadata(writer, "thread_name", SampleTid, "pc samples");

        var n = 0;
        foreach (var e in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ts = e.TimeNs / 1000.0;
            switch (e)
            {
                case ChannelDefEvent def:
                    channels[def.Channel] = def;
                    break;

                case MeasurementEvent m:
                    channels.TryGetValue(m.Channel, out var channel);
                    writer.WriteStartObject();
                    writer.WriteString("ph", "C");
                    writer.WriteString("name", channel?.Name ?? $"ch{m.Channel}");
                    writer.WriteNumber("ts", ts);
                    writer.WriteNumber("pid", Pid);
                    writer.WriteStartObject("args");
                    writer.WriteNumber(channel?.Unit ?? "value", m.Raw * (channel?.Scale ?? 1));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                case LogEvent log:
                    var text = Encoding.UTF8.GetString(log.Data);
                    Instant(writer, FirstLine(text), ts, LogTid, "log", w =>
                    {
                        w.WriteString("text", text);
                        w.WriteNumber("port", log.Port);
                    });
                    break;

                case MarkEvent mark:
                    Instant(writer, mark.Kind.ToString(), ts, LogTid, "mark",
                        mark.Text.Length > 0 ? w => w.WriteString("text", mark.Text) : null);
                    break;

                case PcSampleEvent pc:
                    var label = pc.Sleep ? "sleep" : symbolizer?.Function(pc.Pc)?.Name ?? $"0x{pc.Pc:x8}";
                    if (!frameIds.TryGetValue(label, out var frame))
                        frameIds[label] = frame = frameIds.Count + 1;
                    writer.WriteStartObject();
                    writer.WriteString("ph", "P");
                    writer.WriteString("name", "pc");
                    writer.WriteNumber("ts", ts);
                    writer.WriteNumber("pid", Pid);
                    writer.WriteNumber("tid", SampleTid);
                    writer.WriteNumber("sf", frame);
                    writer.WriteEndObject();
                    break;
            }

            if (++n % FlushEvery == 0)
                await writer.FlushAsync(cancellationToken);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("stackFrames");
        foreach (var (label, id) in frameIds)
        {
            writer.WriteStartObject(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("name", label);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private static void Metadata(Utf8JsonWriter writer, string name, int tid, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "M");
        writer.WriteString("name", name);
        writer.WriteNumber("pid", Pid);
        writer.WriteNumber("tid", tid);
        writer.WriteStartObject("args");
        writer.WriteString("name", value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void Instant(Utf8JsonWriter writer, string name, double ts, int tid, string category,
        Action<Utf8JsonWriter>? args)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "i");
        writer.WriteString("name", name);
        writer.WriteNumber("ts", ts);
        writer.WriteNumber("pid", Pid);
        writer.WriteNumber("tid", tid);
        writer.WriteString("s", "p"); // process scope
        writer.WriteString("cat", category);
        if (args != null)
        {
            writer.WriteStartObject("args");
            args(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }
}
