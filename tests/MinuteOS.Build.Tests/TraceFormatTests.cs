using MinuteOS.Debug.Trace;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The mtrace timeline format: writing a mixed stream of PC samples, logs and
/// SMU measurements and reading it back must reproduce every event exactly, in
/// order, with the delta-coded timestamps/PCs/values reconstructed.
/// </summary>
public class TraceFormatTests
{
    [Fact]
    public void RoundTrip_PreservesHeaderEventsAndTimeline()
    {
        using var stream = new MemoryStream();
        const long start = 1_700_000_000_000_000_000L;
        var writer = new TraceWriter(stream, start);

        writer.WriteMark(0, MarkKind.SessionStart);
        writer.DefineChannel(1_000, 5, ChannelKind.Current, 1e-9, "vout", "A");
        writer.WritePc(2_000, 0x0800_1234);
        writer.WriteMeasurement(2_500, 5, 12_000);      // 12 uA
        writer.WritePcSleep(3_000);
        writer.WritePc(4_000, 0x0800_1000);             // PC goes backwards -> negative delta
        writer.WriteMeasurement(4_500, 5, 11_500);      // value goes down -> negative delta
        writer.WriteLog(5_000, 0, "hi\n"u8.ToArray());
        writer.Flush();

        stream.Position = 0;
        var reader = new TraceReader(stream);
        Assert.Equal(TraceFormat.Version, reader.Header.Version);
        Assert.Equal(start, reader.Header.StartUnixNanos);

        var events = reader.Events().ToList();

        Assert.Collection(events,
            e => Assert.Equal(new MarkEvent(0, MarkKind.SessionStart, ""), e),
            e => Assert.Equal(new ChannelDefEvent(1_000, 5, ChannelKind.Current, 1e-9, "vout", "A"), e),
            e => Assert.Equal(new PcSampleEvent(2_000, 0x0800_1234, false), e),
            e => Assert.Equal(new MeasurementEvent(2_500, 5, 12_000), e),
            e => Assert.Equal(new PcSampleEvent(3_000, 0, true), e),
            e => Assert.Equal(new PcSampleEvent(4_000, 0x0800_1000, false), e),
            e => Assert.Equal(new MeasurementEvent(4_500, 5, 11_500), e),
            e =>
            {
                var log = Assert.IsType<LogEvent>(e);
                Assert.Equal(5_000, log.TimeNs);
                Assert.Equal(0, log.Port);
                Assert.Equal("hi\n"u8.ToArray(), log.Data);
            });
    }

    [Fact]
    public void Reader_SkipsUnknownExtensionRecords()
    {
        using var stream = new MemoryStream();
        var writer = new TraceWriter(stream, 0);
        writer.WritePc(1_000, 0x20);
        writer.Flush();

        // Hand-append an extension record (tag >= 0x80, length-prefixed) and a
        // trailing core record; an older reader must skip the former.
        Varint.WriteUnsigned(stream, 500);              // deltaNs
        stream.WriteByte(TraceFormat.ExtensionTag);     // unknown extension tag
        Varint.WriteUnsigned(stream, 3);                // payload length
        stream.Write([0xDE, 0xAD, 0xBE]);

        // Append a trailing core PcSample record by hand, relative to the
        // extension record's timestamp and the previous PC.
        Varint.WriteUnsigned(stream, 100);              // deltaNs
        stream.WriteByte((byte)TraceTag.PcSample);
        Varint.WriteSigned(stream, 0x21 - 0x20);        // pc delta from previous 0x20

        stream.Position = 0;
        var events = new TraceReader(stream).Events().ToList();

        Assert.Collection(events,
            e => Assert.Equal(new PcSampleEvent(1_000, 0x20, false), e),
            e => Assert.Equal(new PcSampleEvent(1_600, 0x21, false), e));
    }

    [Fact]
    public async Task Recorder_MergesSourcesOnOneMonotonicTimeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minuteos-trace-{Guid.NewGuid():N}.mtrace");
        var smu = new FakeSmuSource(new SmuChannel(0, ChannelKind.Current, 1e-6, "vout", "A"));
        try
        {
            await using (var recorder = new TraceRecorder(path))
            {
                recorder.Attach(smu);
                recorder.RecordPc(0x0800_0100);
                smu.Emit(new SmuSample(0, 3_300));
                recorder.RecordLog(0, "tick\n"u8.ToArray());
                recorder.RecordPc(0x0800_0104);
                smu.Emit(new SmuSample(0, 3_310));
                Assert.True(recorder.EventCount >= 6);
            }

            using var file = File.OpenRead(path);
            var events = new TraceReader(file).Events().ToList();

            // Monotonic non-decreasing timeline across all sources.
            for (var i = 1; i < events.Count; i++)
                Assert.True(events[i].TimeNs >= events[i - 1].TimeNs);

            Assert.Contains(events, e => e is ChannelDefEvent { Name: "vout", Scale: 1e-6 });
            Assert.Contains(events, e => e is PcSampleEvent { Pc: 0x0800_0100 });
            Assert.Contains(events, e => e is MeasurementEvent { Channel: 0, Raw: 3_300 });
            Assert.Contains(events, e => e is MeasurementEvent { Channel: 0, Raw: 3_310 });
            Assert.Contains(events, e => e is LogEvent { Port: 0 });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeSmuSource(params SmuChannel[] channels) : ISmuSampleSource
    {
        public IReadOnlyList<SmuChannel> Channels { get; } = channels;
        public event Action<SmuSample>? Sample;
        public void Emit(SmuSample sample) => Sample?.Invoke(sample);
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
