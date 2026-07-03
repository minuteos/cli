using System.Text.Json.Nodes;
using MinuteOS.Debug.Trace;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The Perfetto/Chrome-trace export: power → counter tracks, logs/marks →
/// instants, PC samples → sample events with a stack-frame table.
/// </summary>
public class PerfettoExportTests
{
    [Fact]
    public async Task WriteAsync_MapsEachSourceToTheRightEventShape()
    {
        TraceEvent[] events =
        [
            new ChannelDefEvent(0, 0, ChannelKind.Current, 1e-6, "vout", "A"),
            new MeasurementEvent(1_000, 0, 12_000), // 12000 * 1e-6 = 0.012 A
            new LogEvent(2_000, 0, "hello\n"u8.ToArray()),
            new PcSampleEvent(3_000, 0x0800_1234, false),
            new PcSampleEvent(4_000, 0, true),
        ];

        using var stream = new MemoryStream();
        await PerfettoExport.WriteAsync(stream, events, symbolizer: null);
        var json = JsonNode.Parse(stream.ToArray())!;
        Assert.Equal("ns", (string?)json["displayTimeUnit"]);

        var traceEvents = Assert.IsType<JsonArray>(json["traceEvents"]);

        // Counter: value scaled to SI, timestamp in microseconds (ns / 1000).
        var counter = Single(traceEvents, e => (string?)e["ph"] == "C");
        Assert.Equal("vout", (string?)counter["name"]);
        Assert.Equal(1.0, (double)counter["ts"]!);
        Assert.Equal(0.012, (double)counter["args"]!["A"]!, 9);

        var log = Single(traceEvents, e => (string?)e["ph"] == "i" && (string?)e["cat"] == "log");
        Assert.Equal("hello", (string?)log["name"]);

        // Two PC sample events, and a stack-frame table with the address + sleep labels.
        Assert.Equal(2, traceEvents.Count(e => (string?)e!["ph"] == "P"));
        var frames = Assert.IsType<JsonObject>(json["stackFrames"]);
        var names = frames.Select(kv => (string?)kv.Value!["name"]).ToList();
        Assert.Contains("0x08001234", names);
        Assert.Contains("sleep", names);
    }

    private static JsonObject Single(JsonArray array, Func<JsonNode, bool> predicate)
        => Assert.IsType<JsonObject>(Assert.Single(array.Where(e => e != null && predicate(e))!));
}
