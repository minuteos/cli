using MinuteOS.Debug.Trace;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The timeline query core: PC histograms over a time range (aggregation,
/// sleep/total accounting, range filtering) and downsampled power/log series.
/// Symbolication is covered by <see cref="DwarfLineTests"/>; here the Address
/// granularity needs no ELF.
/// </summary>
public class TimelineStoreTests
{
    [Fact]
    public void Histogram_AggregatesByAddressWithinRange()
    {
        var store = new TimelineStore();
        store.AddPc(1_000, 0x100, false);
        store.AddPc(2_000, 0x100, false);
        store.AddPc(3_000, 0x200, false);
        store.AddPc(4_000, 0, true); // sleep

        var all = store.Histogram(0, 10_000, Granularity.Address, symbolizer: null);
        Assert.Equal(4, all.TotalSamples);
        Assert.Equal(1, all.SleepSamples);
        Assert.Equal(0, all.UnresolvedSamples);
        Assert.Collection(all.Buckets,
            b => { Assert.Equal("0x00000100", b.Label); Assert.Equal(2, b.Samples); Assert.Equal(50.0, b.Percent); },
            b => { Assert.Equal("0x00000200", b.Label); Assert.Equal(1, b.Samples); Assert.Equal(25.0, b.Percent); });

        // A sub-range excludes samples outside it.
        var mid = store.Histogram(1_500, 3_500, Granularity.Address, symbolizer: null);
        Assert.Equal(2, mid.TotalSamples);
        Assert.Equal(0, mid.SleepSamples);
    }

    [Fact]
    public void Series_DownsamplesPowerAndReturnsLogsInRange()
    {
        var store = new TimelineStore();
        store.DefineChannel(0, 1e-6, "vout", "A");
        store.AddMeasurement(1_000, 0, 100);
        store.AddMeasurement(2_000, 0, 200);
        store.AddMeasurement(3_000, 0, 300);
        store.AddLog(1_500, 0, "hi");
        store.AddLog(9_000, 0, "out-of-range");

        var series = store.Series(0, 4_000, maxPoints: 4);
        var channel = Assert.Single(series.Power);
        Assert.Equal("vout", channel.Name);
        Assert.Equal("A", channel.Unit);
        Assert.Equal(3, channel.Points.Count);
        Assert.Equal(100 * 1e-6, channel.Points[0].Avg, 12);
        Assert.Equal(300 * 1e-6, channel.Points[2].Avg, 12);

        var log = Assert.Single(series.Logs);
        Assert.Equal("hi", log.Text);
    }

    [Fact]
    public void Range_ReportsSpanAcrossSources()
    {
        var store = new TimelineStore();
        store.AddPc(500, 0x10, false);
        store.AddLog(9_000, 0, "late");
        var (start, end) = store.Range();
        Assert.Equal(500, start);
        Assert.Equal(9_000, end);
    }
}
