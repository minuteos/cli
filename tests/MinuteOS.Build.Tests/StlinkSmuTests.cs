using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// The STLINK-V3PWR ascii_dec sample parser: a value line is
/// <c>mantissa×10^±exponent</c> amperes; metadata/ack lines are not samples.
/// (The reverse-engineered protocol payload; the transport is hardware-tested
/// elsewhere / not here.)
/// </summary>
public class StlinkSmuTests
{
    [Theory]
    [InlineData("5000-9", 5e-6)]      // 5000 x 10^-9 = 5 uA
    [InlineData("1234+3", 1_234_000)] // 1234 x 10^3
    [InlineData("12.5-6", 12.5e-6)]
    [InlineData("0-9", 0.0)]
    public void TryParseAmps_DecodesSampleLines(string line, double expected)
    {
        Assert.True(StlinkSmu.TryParseAmps(line, out var amps));
        Assert.Equal(expected, amps, 12);
    }

    [Theory]
    [InlineData("TimeStamp: 1s 234ms, buff 5%")]
    [InlineData("ack volt 3300m")]
    [InlineData("ack")]
    [InlineData("power_monitor")]
    [InlineData("")]
    public void TryParseAmps_RejectsMetadataAndAcks(string line)
    {
        Assert.False(StlinkSmu.TryParseAmps(line, out _));
    }
}
