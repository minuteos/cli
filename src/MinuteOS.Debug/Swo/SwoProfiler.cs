using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Swo;

/// <summary>
/// A PC-sampling profiler over the SWO stream: counts DWT PC-sample packets
/// (hardware source discriminator 2 - 4-byte PC, or the 1-byte zero "sleep"
/// form) per unique address, and symbolicates the aggregate on demand. Hot
/// path is a dictionary increment per sample; each unique PC is resolved to
/// a function exactly once when the report is built.
/// </summary>
public sealed class SwoProfiler
{
    /// <summary>The DWT hardware-source discriminator for PC samples.</summary>
    public const int PcSampleDiscriminator = 2;

    private readonly object _sync = new();
    private readonly Dictionary<uint, long> _hits = [];
    private long _sleepSamples;
    private long _totalSamples;

    public bool Enabled { get; set; }

    public long TotalSamples
    {
        get { lock (_sync) return _totalSamples; }
    }

    /// <summary>Feed every decoded SWO packet; non-PC-sample packets are ignored.</summary>
    public void OnPacket(SwoPacket packet)
    {
        if (!Enabled || !packet.Dwt || packet.Channel != PcSampleDiscriminator)
            return;

        lock (_sync)
        {
            if (packet.Data.Length == 1 && packet.Data[0] == 0)
            {
                // Periodic PC sample with the core sleeping.
                _sleepSamples++;
                _totalSamples++;
            }
            else if (packet.Data.Length == 4)
            {
                var pc = BitConverter.ToUInt32(packet.Data) & ~1u; // thumb bit
                System.Runtime.InteropServices.CollectionsMarshal
                    .GetValueRefOrAddDefault(_hits, pc, out _)++;
                _totalSamples++;
            }
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _hits.Clear();
            _sleepSamples = 0;
            _totalSamples = 0;
        }
    }

    /// <summary>
    /// Builds the aggregated report: samples grouped by function (unique PCs
    /// resolved once via <paramref name="resolve"/>), sorted by count.
    /// </summary>
    public JsonObject Report(Func<uint, FunctionSymbol?> resolve, int top = 50)
    {
        Dictionary<uint, long> hits;
        long sleep, total;
        lock (_sync)
        {
            hits = new Dictionary<uint, long>(_hits);
            sleep = _sleepSamples;
            total = _totalSamples;
        }

        var byFunction = new Dictionary<string, (ulong Address, long Samples)>();
        long unresolved = 0;
        foreach (var (pc, count) in hits)
        {
            if (resolve(pc) is { } function)
            {
                byFunction[function.Name] = byFunction.TryGetValue(function.Name, out var agg)
                    ? (agg.Address, agg.Samples + count)
                    : (function.Address, count);
            }
            else
            {
                unresolved += count;
            }
        }

        var functions = new JsonArray();
        foreach (var (name, agg) in byFunction.OrderByDescending(kv => kv.Value.Samples).Take(top))
        {
            functions.Add(new JsonObject
            {
                ["name"] = name,
                ["address"] = $"0x{agg.Address:x}",
                ["samples"] = agg.Samples,
                ["percent"] = total > 0 ? Math.Round(agg.Samples * 100.0 / total, 1) : 0,
            });
        }

        return new JsonObject
        {
            ["totalSamples"] = total,
            ["sleepSamples"] = sleep,
            ["unresolvedSamples"] = unresolved,
            ["functions"] = functions,
        };
    }

    /// <summary>A human-readable top-N summary of a report.</summary>
    public static string FormatReport(JsonObject report)
    {
        var lines = new List<string>
        {
            $"Profile: {report["totalSamples"]} samples " +
            $"({report["sleepSamples"]} sleep, {report["unresolvedSamples"]} unresolved)",
        };
        foreach (var f in report["functions"] as JsonArray ?? [])
        {
            if (f is JsonObject fn)
                lines.Add($"{fn["percent"],6}%  {fn["samples"],8}  {fn["name"]}");
        }
        return string.Join('\n', lines) + "\n";
    }
}
