using MinuteOS.Debug.Svd;

namespace MinuteOS.Build.Tests;

public class SvdTests
{
    private const string SampleSvd = """
        <?xml version="1.0" encoding="utf-8"?>
        <device>
          <name>TESTCHIP</name>
          <description>A test device</description>
          <addressUnitBits>8</addressUnitBits>
          <size>32</size>
          <cpu><name>CM3</name><endian>little</endian></cpu>
          <peripherals>
            <peripheral>
              <name>UART1</name>
              <description>First UART</description>
              <groupName>UART</groupName>
              <baseAddress>0x40001000</baseAddress>
              <addressBlock><offset>0</offset><size>0x100</size><usage>registers</usage></addressBlock>
              <registers>
                <register>
                  <name>DATA</name>
                  <description>Data register</description>
                  <addressOffset>0x0</addressOffset>
                </register>
                <register>
                  <name>CTRL</name>
                  <description>Control</description>
                  <addressOffset>0x4</addressOffset>
                  <fields>
                    <field><name>EN</name><description>Enable</description><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field>
                    <field><name>MODE</name><description>Mode</description><lsb>4</lsb><msb>7</msb></field>
                    <field><name>DIV</name><description>Divider</description><bitRange>[15:8]</bitRange></field>
                  </fields>
                </register>
              </registers>
            </peripheral>
            <peripheral derivedFrom="UART1">
              <name>UART2</name>
              <baseAddress>0x40002000</baseAddress>
            </peripheral>
          </peripherals>
        </device>
        """;

    [Fact]
    public void Parse_ReadsDeviceAndRegisters()
    {
        var device = SvdParser.Parse(SampleSvd);
        Assert.Equal("TESTCHIP", device.Name);
        Assert.Equal(2, device.Peripherals.Count);

        var uart1 = device.Peripherals[0];
        Assert.Equal("UART1", uart1.Name);
        Assert.Equal(0x40001000ul, uart1.BaseAddress);
        Assert.Equal(0x100, Assert.Single(uart1.AddressBlocks).Size);
        Assert.Equal(2, uart1.Registers.Count);
        Assert.Equal(32, uart1.Registers[0].Size);
    }

    [Fact]
    public void Parse_ResolvesDerivedFrom()
    {
        var device = SvdParser.Parse(SampleSvd);
        var uart2 = device.Peripherals[1];
        Assert.Equal("UART2", uart2.Name);
        Assert.Equal(0x40002000ul, uart2.BaseAddress);
        // registers and address blocks inherited from UART1
        Assert.Equal(["DATA", "CTRL"], uart2.Registers.Select(r => r.Name));
        Assert.Single(uart2.AddressBlocks);
        Assert.Equal("UART", uart2.GroupName);
    }

    [Fact]
    public void Parse_NormalizesFieldBitRanges()
    {
        var device = SvdParser.Parse(SampleSvd);
        var fields = device.Peripherals[0].Registers[1].Fields!;
        Assert.Equal(new SvdField("EN", "Enable", 0, 1), fields[0]);
        Assert.Equal(new SvdField("MODE", "Mode", 4, 4), fields[1]);
        Assert.Equal(new SvdField("DIV", "Divider", 8, 8), fields[2]);
    }

    [Theory]
    [InlineData("STM32F407", "STM32F407", true)]
    [InlineData("stm32f407", "STM32F407", true)]         // case-insensitive
    [InlineData("STM32F4x7", "STM32F407", true)]         // x wildcard in the index name
    [InlineData("STM32F40x", "STM32F407xx", true)]       // wildcard consumes the tail
    [InlineData("STM32F407", "STM32F417", false)]
    [InlineData("STM32F407", "STM32F407VG", false)]      // no wildcard, extra chars
    [InlineData("EFM32GG12B", "EFM32GG11B", false)]
    public void MatchModel_XWildcards(string indexName, string model, bool expected)
        => Assert.Equal(expected, SvdCache.MatchModel(indexName, model));

    [Fact]
    public void WildcardMatcher_StarPatterns()
    {
        var matcher = SvdCache.WildcardMatcher(["UART*", "GPIO_A"]);
        Assert.True(matcher("UART1"));
        Assert.True(matcher("uart2"));
        Assert.True(matcher("GPIO_A"));
        Assert.False(matcher("GPIO_B"));
        Assert.False(matcher("SPI1"));
    }

    [Fact]
    public async Task Cache_ParsesLocalSvdFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}.svd");
        await File.WriteAllTextAsync(path, SampleSvd);
        try
        {
            var cache = new SvdCache(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var device = await cache.GetAsync(path);
            Assert.NotNull(device);
            Assert.Equal("TESTCHIP", device.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
