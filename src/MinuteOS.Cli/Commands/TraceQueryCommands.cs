using MinuteOS.Debug.Trace;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Shared plumbing for the replay queries (<c>trace histogram</c> /
/// <c>trace series</c>): load an .mtrace file into the same
/// <see cref="TimelineStore"/> the live session uses, so post-hoc analysis and
/// the live view answer queries identically.
/// </summary>
public abstract class TraceQueryBase : LoggingCommand
{
    [Argument(Description = "The .mtrace file to query")]
    public string File { get; set; } = "";

    [Option("--from", Description = "Range start in ns since recording start (default: earliest)")]
    public long? From { get; set; }

    [Option("--to", Description = "Range end in ns since recording start (default: latest)")]
    public long? To { get; set; }

    protected (TimelineStore Store, long From, long To)? Load()
    {
        if (!System.IO.File.Exists(File))
        {
            Logger.LogError("No such trace file: {File}", File);
            return null;
        }
        var store = TimelineStore.LoadFile(File);
        var (start, end) = store.Range();
        return (store, From ?? start, To ?? end);
    }
}

/// <summary>Prints the PC histogram (the pie's data) for a range of a saved recording.</summary>
[Command("trace", "histogram", Description = "PC-sample histogram over a range of an mtrace recording")]
public class TraceHistogramCommand : TraceQueryBase
{
    [Option("--granularity", "-g", Description = "function (default), line, or address")]
    public string Granularity { get; set; } = "function";

    [Option("--elf", Description = "Program ELF for symbolication (function/line granularity)")]
    public string? Elf { get; set; }

    [Option("--top", Description = "Keep the top N buckets")]
    public int Top { get; set; } = 100;

    public Task<int> ExecuteAsync()
    {
        if (Load() is not { } loaded)
            return Task.FromResult(1);
        var histogram = loaded.Store.Histogram(loaded.From, loaded.To,
            TimelineJson.ParseGranularity(Granularity), Symbolizer.TryLoad(Elf), Top);
        Console.WriteLine(TimelineJson.ToJson(histogram).ToJsonString());
        return Task.FromResult(0);
    }
}

/// <summary>Prints the downsampled power/log series (the timeline chart's data) for a range.</summary>
[Command("trace", "series", Description = "Downsampled power/log series over a range of an mtrace recording")]
public class TraceSeriesCommand : TraceQueryBase
{
    [Option("--max-points", Description = "Downsample power to at most N points per channel")]
    public int MaxPoints { get; set; } = 600;

    public Task<int> ExecuteAsync()
    {
        if (Load() is not { } loaded)
            return Task.FromResult(1);
        var series = loaded.Store.Series(loaded.From, loaded.To, MaxPoints);
        Console.WriteLine(TimelineJson.ToJson(series).ToJsonString());
        return Task.FromResult(0);
    }
}
