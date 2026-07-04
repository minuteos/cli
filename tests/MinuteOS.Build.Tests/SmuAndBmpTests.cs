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
        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            Sent.Add(line);
            return Task.CompletedTask;
        }
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => new("ack ok");
        public void Dispose() { }
    }

    [Fact]
    public async Task Smu_SpeaksTheV3PwrProtocol()
    {
        // Matches the extension driver: power_monitor / format bin_hexa /
        // volt <out> <mV>m / pwr <out> on|off, each awaiting an ack.
        var transport = new FakeTransport();
        using var smu = new StlinkSmu(transport, NullLogger.Instance);

        await smu.ConfigureAsync("vout", 3.3);
        await smu.PowerAsync("vout", on: true);
        await smu.PowerAsync("vout", on: false);

        Assert.Equal(
            ["power_monitor", "format bin_hexa", "volt vout 3300m", "pwr vout on", "pwr vout off"],
            transport.Sent);
    }

    [Fact]
    public async Task Smu_TimesOutWithoutAck()
    {
        using var smu = new StlinkSmu(new NoAckTransport(), NullLogger.Instance);
        await Assert.ThrowsAsync<TimeoutException>(() => smu.ExecuteAsync("pwr", "vout", true));
    }

    private sealed class NoAckTransport : ISmuTransport
    {
        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default) => new((string?)null);
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
