using System.Text;
using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Renders a timeline as the Chrome Trace Event / Perfetto JSON format, so the
/// same lossless <c>mtrace</c> data opens in Perfetto or chrome://tracing:
/// power channels become counter tracks, logs and marks become instant events,
/// and PC samples become sample events with a symbolicated stack-frame table.
/// Timestamps are microseconds (the format's unit).
/// </summary>
public static class PerfettoExport
{
    private const int Pid = 1;
    private const int LogTid = 2;
    private const int SampleTid = 3;

    public static JsonObject ToJson(IEnumerable<TraceEvent> events, Symbolizer? symbolizer)
    {
        var traceEvents = new JsonArray
        {
            Metadata("process_name", Pid, 0, "minuteos"),
            Metadata("thread_name", Pid, LogTid, "logs"),
            Metadata("thread_name", Pid, SampleTid, "pc samples"),
        };
        var stackFrames = new JsonObject();
        var frameIds = new Dictionary<string, int>();
        var channels = new Dictionary<int, ChannelDefEvent>();

        foreach (var e in events)
        {
            var ts = e.TimeNs / 1000.0;
            switch (e)
            {
                case ChannelDefEvent def:
                    channels[def.Channel] = def;
                    break;

                case MeasurementEvent m:
                    channels.TryGetValue(m.Channel, out var channel);
                    traceEvents.Add(new JsonObject
                    {
                        ["ph"] = "C",
                        ["name"] = channel?.Name ?? $"ch{m.Channel}",
                        ["ts"] = ts,
                        ["pid"] = Pid,
                        ["args"] = new JsonObject { [channel?.Unit ?? "value"] = m.Raw * (channel?.Scale ?? 1) },
                    });
                    break;

                case LogEvent log:
                    var text = Encoding.UTF8.GetString(log.Data);
                    traceEvents.Add(Instant(FirstLine(text), ts, LogTid, "log",
                        new JsonObject { ["text"] = text, ["port"] = log.Port }));
                    break;

                case MarkEvent mark:
                    traceEvents.Add(Instant(mark.Kind.ToString(), ts, LogTid, "mark",
                        mark.Text.Length > 0 ? new JsonObject { ["text"] = mark.Text } : null));
                    break;

                case PcSampleEvent pc:
                    var label = pc.Sleep ? "sleep" : symbolizer?.Function(pc.Pc)?.Name ?? $"0x{pc.Pc:x8}";
                    if (!frameIds.TryGetValue(label, out var frame))
                    {
                        frame = frameIds.Count + 1;
                        frameIds[label] = frame;
                        stackFrames[frame.ToString()] = new JsonObject { ["name"] = label };
                    }
                    traceEvents.Add(new JsonObject
                    {
                        ["ph"] = "P",
                        ["name"] = "pc",
                        ["ts"] = ts,
                        ["pid"] = Pid,
                        ["tid"] = SampleTid,
                        ["sf"] = frame,
                    });
                    break;
            }
        }

        return new JsonObject
        {
            ["displayTimeUnit"] = "ns",
            ["traceEvents"] = traceEvents,
            ["stackFrames"] = stackFrames,
        };
    }

    private static JsonObject Metadata(string name, int pid, int tid, string value) => new()
    {
        ["ph"] = "M",
        ["name"] = name,
        ["pid"] = pid,
        ["tid"] = tid,
        ["args"] = new JsonObject { ["name"] = value },
    };

    private static JsonObject Instant(string name, double ts, int tid, string category, JsonObject? args)
    {
        var obj = new JsonObject
        {
            ["ph"] = "i",
            ["name"] = name,
            ["ts"] = ts,
            ["pid"] = Pid,
            ["tid"] = tid,
            ["s"] = "p", // process scope
            ["cat"] = category,
        };
        if (args != null)
            obj["args"] = args;
        return obj;
    }

    private static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }
}
