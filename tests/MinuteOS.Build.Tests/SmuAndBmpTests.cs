using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Ports of the minute-debug device support: the STLINK-V3PWR SMU line protocol
/// and the Black Magic Probe gdb-batch synthesis.
/// </summary>
public class SmuAndBmpTests : IDisposable
{
    private readonly string _root;

    public SmuAndBmpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-smu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private sealed class FakeTransport : ISmuTransport
    {
        public List<string> Sent = [];
        public void WriteLine(string line) => Sent.Add(line);
        public string? ReadLine(TimeSpan timeout) => "ack ok";
        public void Dispose() { }
    }

    [Fact]
    public void Smu_SpeaksTheV3PwrProtocol()
    {
        // Matches the extension driver: power_monitor / format bin_hexa /
        // volt <out> <mV>m / pwr <out> on|off, each awaiting an ack.
        var transport = new FakeTransport();
        using var smu = new StlinkSmu(transport, NullLogger.Instance);

        smu.Configure("vout", 3.3);
        smu.Power("vout", on: true);
        smu.Power("vout", on: false);

        Assert.Equal(
            ["power_monitor", "format bin_hexa", "volt vout 3300m", "pwr vout on", "pwr vout off"],
            transport.Sent);
    }

    [Fact]
    public void Smu_TimesOutWithoutAck()
    {
        var transport = new FakeTransport();
        using var smu = new StlinkSmu(new NoAckTransport(), NullLogger.Instance);
        Assert.Throws<TimeoutException>(() => smu.Execute("pwr", "vout", true));
    }

    private sealed class NoAckTransport : ISmuTransport
    {
        public void WriteLine(string line) { }
        public string? ReadLine(TimeSpan timeout) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Bmp_FlashAndErase_AreGdbBatchSessions()
    {
        // A board with debug.server: bmp gets flash/erase synthesized as gdb
        // batch runs (the probe IS the gdb server) - the extension's mechanism.
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  board:\n    target: host\n    components: []\n" +
            "    settings:\n      debug.server: bmp\n      bmp.port: /dev/ttyX\n      bmp.power: \"true\"\n" +
            "      gcc.toolchain-prefix: arm-none-eabi-\n");
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "board", _root);

        var flash = DeviceSpecResolver.Resolve(config, "flash", "/out/app.elf")!;
        Assert.Equal("arm-none-eabi-gdb", flash.Program);
        Assert.Equal(
            ["-nx", "--batch",
             "-ex", "target extended-remote /dev/ttyX",
             "-ex", "monitor tpwr enable",
             "-ex", "monitor swdp_scan",
             "-ex", "attach 1",
             "-ex", "load",
             "/out/app.elf"],
            flash.Args);

        var erase = DeviceSpecResolver.Resolve(config, "erase", "/out/app.elf")!;
        Assert.Contains("monitor erase_mass", erase.Args);
        Assert.DoesNotContain("/out/app.elf", erase.Args);
    }
}
