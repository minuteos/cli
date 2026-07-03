using MinuteOS.Debug;
using MinuteOS.Debug.Trace;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Prints a human-readable summary of an mtrace recording - duration, event
/// counts, per-channel power stats, and the hottest functions - for a quick
/// sanity check without opening a viewer.
/// </summary>
[Command("trace", "summary", Description = "Summarize an mtrace recording (duration, counts, power, hot functions)")]
public class TraceSummaryCommand : LoggingCommand
{
    [Argument(Description = "The .mtrace file to summarize")]
    public string File { get; set; } = "";

    [Option("--elf", Description = "Program ELF for function symbolication")]
    public string? Elf { get; set; }

    [Option("--top", "-n", Description = "How many hot functions to list")]
    public int Top { get; set; } = 5;

    private sealed class ChannelStat
    {
        public required string Name { get; init; }
        public required string Unit { get; init; }
        public required double Scale { get; init; }
        public double Min { get; set; } = double.PositiveInfinity;
        public double Max { get; set; } = double.NegativeInfinity;
        public double Sum { get; set; }
        public long Count { get; set; }
    }

    public Task<int> ExecuteAsync()
    {
        if (!System.IO.File.Exists(File))
        {
            Logger.LogError("No such trace file: {File}", File);
            return Task.FromResult(1);
        }

        var symbolizer = Symbolizer.TryLoad(Elf);
        var channels = new Dictionary<int, ChannelStat>();
        var functions = new Dictionary<string, long>();
        long pcRun = 0, pcSleep = 0, logs = 0, samples = 0, marks = 0, resolved = 0;
        long firstNs = long.MaxValue, lastNs = long.MinValue;

        using (var input = System.IO.File.OpenRead(File))
        {
            foreach (var e in new TraceReader(input).Events())
            {
                firstNs = Math.Min(firstNs, e.TimeNs);
                lastNs = Math.Max(lastNs, e.TimeNs);
                switch (e)
                {
                    case PcSampleEvent { Sleep: true }:
                        pcSleep++;
                        break;
                    case PcSampleEvent pc:
                        pcRun++;
                        if (symbolizer?.Function(pc.Pc) is { } fn)
                        {
                            functions[fn.Name] = functions.GetValueOrDefault(fn.Name) + 1;
                            resolved++;
                        }
                        break;
                    case LogEvent:
                        logs++;
                        break;
                    case ChannelDefEvent def:
                        channels[def.Channel] = new ChannelStat { Name = def.Name, Unit = def.Unit, Scale = def.Scale };
                        break;
                    case MeasurementEvent m:
                        samples++;
                        if (channels.TryGetValue(m.Channel, out var stat))
                        {
                            var value = m.Raw * stat.Scale;
                            stat.Min = Math.Min(stat.Min, value);
                            stat.Max = Math.Max(stat.Max, value);
                            stat.Sum += value;
                            stat.Count++;
                        }
                        break;
                    case MarkEvent:
                        marks++;
                        break;
                }
            }
        }

        var durationS = firstNs <= lastNs ? (lastNs - firstNs) / 1e9 : 0;
        Console.WriteLine($"mtrace summary: {File}");
        Console.WriteLine($"  duration:  {durationS:0.000} s");
        Console.WriteLine($"  events:    {pcRun + pcSleep + logs + samples + marks}");
        Console.WriteLine($"    pc:      {pcRun} run, {pcSleep} sleep"
            + (pcRun > 0 && symbolizer != null ? $" ({100.0 * resolved / pcRun:0.#}% resolved)" : ""));
        Console.WriteLine($"    log:     {logs}");
        Console.WriteLine($"    sample:  {samples}");
        Console.WriteLine($"    mark:    {marks}");

        if (channels.Count > 0)
        {
            Console.WriteLine("  power:");
            foreach (var s in channels.Values.Where(c => c.Count > 0))
                Console.WriteLine($"    {s.Name} ({s.Unit}):  min {Eng(s.Min, s.Unit)}  max {Eng(s.Max, s.Unit)}  "
                    + $"avg {Eng(s.Sum / s.Count, s.Unit)}  ({s.Count} samples)");
        }

        if (functions.Count > 0)
        {
            Console.WriteLine($"  hot functions (top {Top}):");
            foreach (var (name, count) in functions.OrderByDescending(kv => kv.Value).Take(Top))
                Console.WriteLine($"    {100.0 * count / pcRun,5:0.0}%  {name}");
        }

        return Task.FromResult(0);
    }

    private static string Eng(double value, string unit)
    {
        var a = Math.Abs(value);
        (double factor, string prefix) = a switch
        {
            >= 1 => (1, ""),
            >= 1e-3 => (1e-3, "m"),
            >= 1e-6 => (1e-6, "µ"),
            _ => (1e-9, "n"),
        };
        return $"{value / factor:0.##} {prefix}{unit}";
    }
}
