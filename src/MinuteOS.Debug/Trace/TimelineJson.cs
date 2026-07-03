using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Serializes timeline query results to JSON - shared by the live DAP custom
/// requests and the replay CLI so both speak the same shape to the webview.
/// </summary>
public static class TimelineJson
{
    public static Granularity ParseGranularity(string? value) => value?.ToLowerInvariant() switch
    {
        "line" => Granularity.Line,
        "address" => Granularity.Address,
        _ => Granularity.Function,
    };

    public static JsonObject ToJson(Histogram h) => new()
    {
        ["from"] = h.FromNs,
        ["to"] = h.ToNs,
        ["totalSamples"] = h.TotalSamples,
        ["sleepSamples"] = h.SleepSamples,
        ["unresolvedSamples"] = h.UnresolvedSamples,
        ["buckets"] = new JsonArray(h.Buckets.Select(b => (JsonNode)new JsonObject
        {
            ["label"] = b.Label,
            ["samples"] = b.Samples,
            ["percent"] = b.Percent,
            ["address"] = b.Address,
            ["line"] = b.Line,
        }).ToArray()),
    };

    public static JsonObject ToJson(TimelineSeries s) => new()
    {
        ["start"] = s.StartNs,
        ["end"] = s.EndNs,
        ["power"] = new JsonArray(s.Power.Select(c => (JsonNode)new JsonObject
        {
            ["channel"] = c.Channel,
            ["name"] = c.Name,
            ["unit"] = c.Unit,
            ["points"] = new JsonArray(c.Points.Select(p => (JsonNode)new JsonObject
            {
                ["t"] = p.TimeNs,
                ["min"] = p.Min,
                ["max"] = p.Max,
                ["avg"] = p.Avg,
            }).ToArray()),
        }).ToArray()),
        ["logs"] = new JsonArray(s.Logs.Select(l => (JsonNode)new JsonObject
        {
            ["t"] = l.TimeNs,
            ["port"] = l.Port,
            ["text"] = l.Text,
        }).ToArray()),
    };
}
