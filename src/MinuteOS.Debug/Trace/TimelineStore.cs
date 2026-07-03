namespace MinuteOS.Debug.Trace;

/// <summary>PC aggregation granularity for the histogram.</summary>
public enum Granularity
{
    Function,
    Line,
    Address,
}

public sealed record HistogramBucket(string Label, long Samples, double Percent, string Address, int Line);

public sealed record Histogram(
    long FromNs, long ToNs, long TotalSamples, long SleepSamples, long UnresolvedSamples,
    IReadOnlyList<HistogramBucket> Buckets);

public sealed record PowerPoint(long TimeNs, double Min, double Max, double Avg);

public sealed record ChannelSeries(int Channel, string Name, string Unit, IReadOnlyList<PowerPoint> Points);

public sealed record LogLine(long TimeNs, int Port, string Text);

public sealed record TimelineSeries(
    long StartNs, long EndNs, IReadOnlyList<ChannelSeries> Power, IReadOnlyList<LogLine> Logs);

/// <summary>
/// An in-memory index of timeline samples (PC, per-channel measurements, logs)
/// answering the two queries the timeline view needs: a PC <see cref="Histogram"/>
/// over a time range at a chosen <see cref="Granularity"/> (the pie), and a
/// downsampled power/log <see cref="TimelineSeries"/> (the scrolling chart). The
/// same store backs both the live session and file replay; live use bounds
/// memory with a retention window, replay loads everything.
///
/// Samples arrive time-ordered (monotonic host clock), so each list is sorted by
/// timestamp and range queries binary-search it. Thread-safe: producers append
/// while the request handler queries.
/// </summary>
public sealed class TimelineStore(long retentionNs = 300_000_000_000L)
{
    private readonly object _sync = new();
    private readonly List<(long TimeNs, uint Pc, bool Sleep)> _pc = [];
    private readonly Dictionary<int, ChannelInfo> _channels = [];
    private readonly List<(long TimeNs, int Channel, long Raw)> _measurements = [];
    private readonly List<LogLine> _logs = [];

    private sealed record ChannelInfo(string Name, string Unit, double Scale);

    /// <summary>Loads a whole .mtrace recording into a store (no retention window) for replay.</summary>
    public static TimelineStore LoadFile(string path)
    {
        var store = new TimelineStore(retentionNs: 0);
        using var file = File.OpenRead(path);
        foreach (var e in new TraceReader(file).Events())
        {
            switch (e)
            {
                case PcSampleEvent pc:
                    store.AddPc(pc.TimeNs, pc.Pc, pc.Sleep);
                    break;
                case LogEvent log:
                    store.AddLog(log.TimeNs, log.Port, System.Text.Encoding.UTF8.GetString(log.Data));
                    break;
                case ChannelDefEvent def:
                    store.DefineChannel(def.Channel, def.Scale, def.Name, def.Unit);
                    break;
                case MeasurementEvent m:
                    store.AddMeasurement(m.TimeNs, m.Channel, m.Raw);
                    break;
            }
        }
        return store;
    }

    public void DefineChannel(int channel, double scale, string name, string unit)
    {
        lock (_sync)
            _channels[channel] = new ChannelInfo(name, unit, scale);
    }

    public void AddPc(long timeNs, uint pc, bool sleep)
    {
        lock (_sync)
        {
            _pc.Add((timeNs, pc, sleep));
            Trim(_pc, timeNs, x => x.TimeNs);
        }
    }

    public void AddMeasurement(long timeNs, int channel, long raw)
    {
        lock (_sync)
        {
            _measurements.Add((timeNs, channel, raw));
            Trim(_measurements, timeNs, x => x.TimeNs);
        }
    }

    public void AddLog(long timeNs, int port, string text)
    {
        lock (_sync)
        {
            _logs.Add(new LogLine(timeNs, port, text));
            Trim(_logs, timeNs, x => x.TimeNs);
        }
    }

    /// <summary>Feeds a live SWO packet (same decode as the recorder): DWT PC samples and ITM logs.</summary>
    public void OnSwoPacket(long timeNs, Swo.SwoPacket packet)
    {
        if (packet.Dwt)
        {
            if (packet.Channel != Swo.SwoProfiler.PcSampleDiscriminator)
                return;
            if (packet.Data.Length == 1 && packet.Data[0] == 0)
                AddPc(timeNs, 0, sleep: true);
            else if (packet.Data.Length == 4)
                AddPc(timeNs, BitConverter.ToUInt32(packet.Data) & ~1u, sleep: false);
        }
        else
        {
            AddLog(timeNs, packet.Channel, System.Text.Encoding.UTF8.GetString(packet.Data));
        }
    }

    /// <summary>The span of retained data, or (0, 0) when empty.</summary>
    public (long StartNs, long EndNs) Range()
    {
        lock (_sync)
        {
            long start = long.MaxValue, end = long.MinValue;
            foreach (var t in Ends())
            {
                start = Math.Min(start, t.Start);
                end = Math.Max(end, t.End);
            }
            return start == long.MaxValue ? (0, 0) : (start, end);

            IEnumerable<(long Start, long End)> Ends()
            {
                if (_pc.Count > 0) yield return (_pc[0].TimeNs, _pc[^1].TimeNs);
                if (_measurements.Count > 0) yield return (_measurements[0].TimeNs, _measurements[^1].TimeNs);
                if (_logs.Count > 0) yield return (_logs[0].TimeNs, _logs[^1].TimeNs);
            }
        }
    }

    public Histogram Histogram(long fromNs, long toNs, Granularity granularity, Symbolizer? symbolizer, int top = 100)
    {
        var buckets = new Dictionary<string, (long Samples, string Address, int Line, string Label)>();
        long total = 0, sleep = 0, unresolved = 0;

        lock (_sync)
        {
            var lo = LowerBound(_pc, fromNs, x => x.TimeNs);
            for (var i = lo; i < _pc.Count && _pc[i].TimeNs <= toNs; i++)
            {
                var (_, pc, isSleep) = _pc[i];
                total++;
                if (isSleep)
                {
                    sleep++;
                    continue;
                }

                if (!TryKey(pc, granularity, symbolizer, out var key, out var label, out var address, out var line))
                {
                    unresolved++;
                    continue;
                }

                var prev = buckets.TryGetValue(key, out var agg) ? agg.Samples : 0;
                buckets[key] = (prev + 1, address, line, label);
            }
        }

        var ordered = buckets.Values
            .OrderByDescending(b => b.Samples)
            .Take(top)
            .Select(b => new HistogramBucket(b.Label, b.Samples,
                total > 0 ? Math.Round(b.Samples * 100.0 / total, 2) : 0, b.Address, b.Line))
            .ToList();

        return new Histogram(fromNs, toNs, total, sleep, unresolved, ordered);
    }

    public TimelineSeries Series(long fromNs, long toNs, int maxPoints = 600, int maxLogs = 500)
    {
        lock (_sync)
        {
            var span = Math.Max(1, toNs - fromNs);
            var bucketNs = Math.Max(1, span / Math.Max(1, maxPoints));

            var power = new List<ChannelSeries>();
            foreach (var (channel, info) in _channels.OrderBy(kv => kv.Key))
            {
                var points = new List<PowerPoint>();
                var lo = LowerBound(_measurements, fromNs, x => x.TimeNs);
                long bucket = -1;
                double min = 0, max = 0, sum = 0;
                var n = 0;

                void Flush()
                {
                    if (n > 0)
                        points.Add(new PowerPoint(fromNs + bucket * bucketNs, min, max, sum / n));
                }

                for (var i = lo; i < _measurements.Count && _measurements[i].TimeNs <= toNs; i++)
                {
                    var m = _measurements[i];
                    if (m.Channel != channel)
                        continue;
                    var b = (m.TimeNs - fromNs) / bucketNs;
                    var value = m.Raw * info.Scale;
                    if (b != bucket)
                    {
                        Flush();
                        bucket = b;
                        min = max = sum = value;
                        n = 1;
                    }
                    else
                    {
                        min = Math.Min(min, value);
                        max = Math.Max(max, value);
                        sum += value;
                        n++;
                    }
                }
                Flush();
                power.Add(new ChannelSeries(channel, info.Name, info.Unit, points));
            }

            var logLo = LowerBound(_logs, fromNs, x => x.TimeNs);
            var logs = new List<LogLine>();
            for (var i = logLo; i < _logs.Count && _logs[i].TimeNs <= toNs; i++)
                logs.Add(_logs[i]);
            if (logs.Count > maxLogs)
                logs = logs.GetRange(logs.Count - maxLogs, maxLogs);

            return new TimelineSeries(fromNs, toNs, power, logs);
        }
    }

    private static bool TryKey(uint pc, Granularity granularity, Symbolizer? symbolizer,
        out string key, out string label, out string address, out int line)
    {
        address = $"0x{pc:x8}";
        line = 0;
        var fn = symbolizer?.Function(pc);

        switch (granularity)
        {
            case Granularity.Address:
                key = address;
                label = fn != null ? $"{address} {fn.Name}" : address;
                return true;

            case Granularity.Line:
                var li = symbolizer?.Line(pc);
                if (fn == null && li == null)
                    break;
                line = li?.Line ?? 0;
                var where = li != null ? $"{System.IO.Path.GetFileName(li.File)}:{li.Line}" : "?";
                key = $"{fn?.Name ?? "??"}|{where}";
                label = $"{fn?.Name ?? "??"} ({where})";
                if (fn != null)
                    address = $"0x{fn.Address:x}";
                return true;

            default: // Function
                if (fn == null)
                    break;
                key = fn.Name;
                label = fn.Name;
                address = $"0x{fn.Address:x}";
                return true;
        }

        key = label = address;
        return false;
    }

    // Samples arrive in time order, so binary-search the first index at or after fromNs.
    private static int LowerBound<T>(List<T> list, long fromNs, Func<T, long> time)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (time(list[mid]) < fromNs)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // Drop the oldest samples once they fall outside the retention window. Only
    // trims when a good chunk has expired, so the front-shift stays amortized.
    private void Trim<T>(List<T> list, long nowNs, Func<T, long> time)
    {
        if (retentionNs <= 0 || list.Count < 1024)
            return;
        var cutoff = nowNs - retentionNs;
        if (time(list[0]) >= cutoff)
            return;
        var drop = LowerBound(list, cutoff, time);
        if (drop > list.Count / 4)
            list.RemoveRange(0, drop);
    }
}
