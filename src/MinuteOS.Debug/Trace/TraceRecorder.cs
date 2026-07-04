using System.Diagnostics;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Collects heterogeneous profiling events - DWT PC samples, ITM log output and
/// SMU measurements - onto a single monotonic host clock and writes them as an
/// <see cref="TraceFormat"/> stream. Producers call the <c>Record*</c> methods
/// from their own threads; each is stamped with the current host time and
/// delta-coded by the (thread-safe) <see cref="TraceWriter"/>.
///
/// The host arrival clock is the only time base shared by all three sources
/// (the SWO stream carries no usable device timestamps), so events are ordered
/// by when the adapter saw them, not by an on-target clock.
/// </summary>
public sealed class TraceRecorder : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly FileStream _file;
    private readonly TraceWriter _writer;
    private readonly long _startTimestamp;
    private readonly Timer _flushTimer;
    private ISmuSampleSource? _attached;
    private long _eventCount;

    public string Path { get; }
    public long EventCount => Interlocked.Read(ref _eventCount);

    public TraceRecorder(string path, TimeSpan? flushInterval = null)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var startUnixNanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        _writer = new TraceWriter(_file, startUnixNanos);
        _startTimestamp = Stopwatch.GetTimestamp();
        Mark(MarkKind.SessionStart);

        // Push completed records to the OS on a cadence so a killed session (or a
        // reader tailing the live file) sees data before clean shutdown. The
        // writer's lock keeps every flush on a record boundary.
        var interval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = new Timer(_ => _writer.Flush(), null, interval, interval);
    }

    public void RecordPc(uint pc)
    {
        _writer.WritePc(NowNs(), pc);
        Count();
    }

    public void RecordPcSleep()
    {
        _writer.WritePcSleep(NowNs());
        Count();
    }

    public void RecordLog(int port, ReadOnlySpan<byte> data)
    {
        _writer.WriteLog(NowNs(), port, data);
        Count();
    }

    public void RecordMeasurement(int channel, long raw)
    {
        _writer.WriteMeasurement(NowNs(), channel, raw);
        Count();
    }

    public void DefineChannel(int channel, ChannelKind kind, double scale, string name, string unit)
    {
        _writer.DefineChannel(NowNs(), channel, kind, scale, name, unit);
        Count();
    }

    public void Mark(MarkKind kind, string text = "")
    {
        _writer.WriteMark(NowNs(), kind, text);
        Count();
    }

    /// <summary>
    /// Records a decoded SWO packet: DWT PC samples become PC events (requires
    /// PC sampling to be enabled on the target), ITM stimulus-port writes become
    /// log events (port 0 is the console). Other DWT sources are ignored.
    /// </summary>
    public void OnSwoPacket(Swo.SwoPacket packet)
    {
        var sample = Swo.SwoSample.Classify(packet);
        switch (sample.Kind)
        {
            case Swo.SwoSampleKind.PcSample:
                RecordPc(sample.Pc);
                break;
            case Swo.SwoSampleKind.PcSleep:
                RecordPcSleep();
                break;
            case Swo.SwoSampleKind.Log:
                RecordLog(sample.Port, sample.Data);
                break;
        }
    }

    /// <summary>Defines an SMU source's channels and streams its samples into the recording.</summary>
    public void Attach(ISmuSampleSource source)
    {
        foreach (var channel in source.Channels)
            DefineChannel(channel.Id, channel.Kind, channel.Scale, channel.Name, channel.Unit);
        source.Sample += OnSmuSample;
        _attached = source;
    }

    public async ValueTask DisposeAsync()
    {
        // Awaiting the timer's disposal drains any in-flight flush callback, so
        // nothing touches the writer or file after this point.
        await _flushTimer.DisposeAsync();
        if (_attached != null)
            _attached.Sample -= OnSmuSample;
        _writer.Flush();
        await _file.DisposeAsync();
    }

    private void OnSmuSample(SmuSample sample) => RecordMeasurement(sample.Channel, sample.Raw);

    // TimeSpan ticks (100 ns) are coarser than the Stopwatch but overflow-safe
    // over long sessions and finer than the host arrival jitter anyway.
    private long NowNs() => Stopwatch.GetElapsedTime(_startTimestamp).Ticks * 100;

    private void Count() => Interlocked.Increment(ref _eventCount);
}
