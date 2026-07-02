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
    public void Resolve_RequiresCommandOrJlinkDevice()
    {
        var config = Configure(
            "    steps:\n      - name: flash\n        phase: Device\n        config: { args: '-x' }\n");
        Assert.Null(DeviceSpecResolver.Resolve(config, "flash", "/i"));
    }

    [Fact]
    public void JlinkDevice_SynthesizesAllOperations()
    {
        // A board that sets ONLY jlink.device gets flash/erase/gdb-server for free
        // (the vsix / Make-era JLINK_DEVICE workflow).
        var config = Configure("    settings:\n      jlink.device: EFR32MG12P332F1024GL125\n");
        var image = Path.Combine(config.OutputRoot, "app.elf");

        var flash = DeviceSpecResolver.Resolve(config, "flash", image)!;
        Assert.Equal("JLinkExe", flash.Program);
        Assert.Contains("EFR32MG12P332F1024GL125", flash.Args);
        Assert.DoesNotContain("-SelectEmuBySN", flash.Args);   // no -d => optional group dropped

        // The commander script is materialized next to the outputs.
        var scriptPath = flash.Args[flash.Args.IndexOf("-CommanderScript") + 1];
        Assert.Contains($"loadfile {image}", File.ReadAllText(scriptPath));

        var erase = DeviceSpecResolver.Resolve(config, "erase", image, device: "483066211")!;
        Assert.Contains("-SelectEmuBySN", erase.Args);
        Assert.Contains("483066211", erase.Args);
        var eraseScript = erase.Args[erase.Args.IndexOf("-CommanderScript") + 1];
        Assert.StartsWith("erase", File.ReadAllText(eraseScript));

        var server = DeviceSpecResolver.Resolve(config, "gdb-server", image)!;
        Assert.Equal("JLinkGDBServer", server.Program);
        Assert.Contains("3333", server.Args);   // default port
    }

    [Fact]
    public void ExplicitStep_OverridesJlinkDefaults()
    {
        var config = Configure(
            "    settings:\n      jlink.device: X\n" +
            "    steps:\n      - name: flash\n        phase: Device\n        config: { command: custom-flasher, args: '{image}' }\n");

        var spec = DeviceSpecResolver.Resolve(config, "flash", "/i.elf")!;
        Assert.Equal("custom-flasher", spec.Program);
        Assert.Equal(["/i.elf"], spec.Args);
    }
}
