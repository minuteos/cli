using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Covers device-operation resolution (flash/erase/gdb-server) from a
/// configuration's Device-phase steps: substitution, optional [..] groups,
/// most-specific override, and the missing-operation case.
/// </summary>
public class DeviceSpecResolverTests : IDisposable
{
    private readonly string _root;

    public DeviceSpecResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private BuildConfiguration Configure(string stepsYaml)
    {
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  board:\n    target: host\n    components: []\n" + stepsYaml);
        return BuildConfiguration.Create(ProjectConfig.Load(_root), "board", _root);
    }

    [Fact]
    public void Resolve_SubstitutesImageAndPort()
    {
        var config = Configure(
            "    steps:\n" +
            "      - name: flash\n        phase: Device\n        config:\n" +
            "          command: openocd\n" +
            "          args: '-c \"program {image} verify\" --base {image-base}.bin --port {port}'\n" +
            "          gdb-port: \"4242\"\n");

        var spec = DeviceSpecResolver.Resolve(config, "flash", "/out/app.elf");

        Assert.NotNull(spec);
        Assert.Equal("openocd", spec.Program);
        Assert.Equal(["-c", "program /out/app.elf verify", "--base", "/out/app.bin", "--port", "4242"], spec.Args);
    }

    [Fact]
    public void Resolve_OptionalGroup_DroppedWhenDeviceUnset_KeptWhenSet()
    {
        var config = Configure(
            "    steps:\n" +
            "      - name: erase\n        phase: Device\n        config:\n" +
            "          command: probe\n" +
            "          args: '[--serial {device}] --chip'\n");

        var without = DeviceSpecResolver.Resolve(config, "erase", "/out/app.elf")!;
        Assert.Equal(["--chip"], without.Args);

        var with = DeviceSpecResolver.Resolve(config, "erase", "/out/app.elf", device: "ABC123")!;
        Assert.Equal(["--serial", "ABC123", "--chip"], with.Args);
    }

    [Fact]
    public void Resolve_MostSpecificStepWins_AndMissingOpIsNull()
    {
        // Two flash steps: config-level (later in StepRefs) overrides the target's.
        var config = Configure(
            "    steps:\n" +
            "      - name: flash\n        phase: Device\n        config: { command: generic }\n" +
            "      - name: flash\n        phase: Device\n        config: { command: specific }\n");

        Assert.Equal("specific", DeviceSpecResolver.Resolve(config, "flash", "/i")!.Program);
        Assert.Null(DeviceSpecResolver.Resolve(config, "gdb-server", "/i"));
    }

    [Fact]
    public void Resolve_RequiresCommand()
    {
        var config = Configure(
            "    steps:\n      - name: flash\n        phase: Device\n        config: { args: '-x' }\n");
        Assert.Null(DeviceSpecResolver.Resolve(config, "flash", "/i"));
    }
}
