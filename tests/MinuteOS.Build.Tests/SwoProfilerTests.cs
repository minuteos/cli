using System.Text.Json.Nodes;
using MinuteOS.Debug;
using MinuteOS.Debug.Swo;

namespace MinuteOS.Build.Tests;

public class SwoProfilerTests
{
    private static SwoPacket PcSample(uint pc)
        => new(Dwt: true, Channel: 2, Data: BitConverter.GetBytes(pc));

    private static readonly Func<uint, FunctionSymbol?> Resolver = pc => pc switch
    {
        >= 0x100 and < 0x140 => new FunctionSymbol(0x100, 0x40, "main"),
        >= 0x140 and < 0x160 => new FunctionSymbol(0x140, 0x20, "bump"),
        _ => null,
    };

    [Fact]
    public void CountsSamples_GroupedByFunction_SortedByWeight()
    {
        var profiler = new SwoProfiler { Enabled = true };
        for (var i = 0; i < 6; i++)
            profiler.OnPacket(PcSample(0x104 + (uint)(i % 3) * 4)); // main, 3 distinct PCs
        profiler.OnPacket(PcSample(0x140));                          // bump
        profiler.OnPacket(PcSample(0x141));                          // bump (thumb bit)
        profiler.OnPacket(PcSample(0xDEAD_0000));                    // unresolved

        var report = profiler.Report(Resolver);
        Assert.Equal(9, report["totalSamples"]!.GetValue<long>());
        Assert.Equal(1, report["unresolvedSamples"]!.GetValue<long>());

        var functions = (report["functions"] as JsonArray)!.OfType<JsonObject>().ToList();
        Assert.Equal(["main", "bump"], functions.Select(f => f["name"]!.GetValue<string>()));
        Assert.Equal(6, functions[0]["samples"]!.GetValue<long>());
        Assert.Equal(2, functions[1]["samples"]!.GetValue<long>());
        Assert.Equal(66.7, functions[0]["percent"]!.GetValue<double>());
        Assert.Equal("0x100", functions[0]["address"]!.GetValue<string>());
    }

    [Fact]
    public void SleepSamples_CountedSeparately()
    {
        var profiler = new SwoProfiler { Enabled = true };
        profiler.OnPacket(new SwoPacket(Dwt: true, Channel: 2, Data: [0])); // sleep form
        profiler.OnPacket(PcSample(0x100));

        var report = profiler.Report(Resolver);
        Assert.Equal(2, report["totalSamples"]!.GetValue<long>());
        Assert.Equal(1, report["sleepSamples"]!.GetValue<long>());
    }

    [Fact]
    public void IgnoresOtherPackets_AndDisabledState()
    {
        var profiler = new SwoProfiler { Enabled = true };
        profiler.OnPacket(new SwoPacket(Dwt: false, Channel: 0, Data: [(byte)'x'])); // ITM console
        profiler.OnPacket(new SwoPacket(Dwt: true, Channel: 1, Data: [1, 2]));       // exception trace
        profiler.Enabled = false;
        profiler.OnPacket(PcSample(0x100));

        Assert.Equal(0, profiler.TotalSamples);
    }

    [Fact]
    public void Reset_ClearsCounts()
    {
        var profiler = new SwoProfiler { Enabled = true };
        profiler.OnPacket(PcSample(0x100));
        profiler.Reset();
        Assert.Equal(0, profiler.TotalSamples);
    }

    [Fact]
    public void FormatReport_ProducesTopTable()
    {
        var profiler = new SwoProfiler { Enabled = true };
        for (var i = 0; i < 3; i++)
            profiler.OnPacket(PcSample(0x100));
        var text = SwoProfiler.FormatReport(profiler.Report(Resolver));
        Assert.Contains("3 samples", text);
        Assert.Contains("main", text);
        Assert.Contains("100%", text.Replace(" ", ""));
    }

    [Fact]
    public void EndToEnd_ThroughTheWireFormat()
    {
        // Feed raw DWT PC-sample packets (as MinuteItmCapture / real silicon
        // frames them) through the SWO parser into the profiler.
        var parser = new SwoParser();
        var profiler = new SwoProfiler { Enabled = true };
        parser.SourcePacket += profiler.OnPacket;

        byte[] Frame(uint pc) => [(2 << 3) | 0x04 | 0x03, .. BitConverter.GetBytes(pc)];
        parser.Feed([.. Frame(0x104), .. Frame(0x104), .. Frame(0x150)]);

        var report = profiler.Report(Resolver);
        Assert.Equal(3, report["totalSamples"]!.GetValue<long>());
        var functions = (report["functions"] as JsonArray)!.OfType<JsonObject>().ToList();
        Assert.Equal(2, functions.Single(f => f["name"]!.GetValue<string>() == "main")["samples"]!.GetValue<long>());
    }
}
