using MinuteOS.Debug.Swo;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The shared SWO packet classification (used by both the recorder and the
/// timeline store): DWT PC samples vs. sleep vs. ITM logs vs. ignore.
/// </summary>
public class SwoSampleTests
{
    [Fact]
    public void Classify_DwtPcSample_ClearsThumbBit()
    {
        var packet = new SwoPacket(Dwt: true, SwoProfiler.PcSampleDiscriminator, BitConverter.GetBytes(0x0800_1235u));
        var sample = SwoSample.Classify(packet);
        Assert.Equal(SwoSampleKind.PcSample, sample.Kind);
        Assert.Equal(0x0800_1234u, sample.Pc);
    }

    [Fact]
    public void Classify_DwtSleepForm()
    {
        var sample = SwoSample.Classify(new SwoPacket(Dwt: true, SwoProfiler.PcSampleDiscriminator, [0]));
        Assert.Equal(SwoSampleKind.PcSleep, sample.Kind);
    }

    [Fact]
    public void Classify_ItmStimulusPort_IsLog()
    {
        var data = "hi"u8.ToArray();
        var sample = SwoSample.Classify(new SwoPacket(Dwt: false, 0, data));
        Assert.Equal(SwoSampleKind.Log, sample.Kind);
        Assert.Equal(0, sample.Port);
        Assert.Equal(data, sample.Data);
    }

    [Fact]
    public void Classify_OtherDwtSource_Ignored()
    {
        var sample = SwoSample.Classify(new SwoPacket(Dwt: true, 1, [1, 2, 3, 4]));
        Assert.Equal(SwoSampleKind.Ignore, sample.Kind);
    }
}
