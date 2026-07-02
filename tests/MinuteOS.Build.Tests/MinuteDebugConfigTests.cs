using System.Text.Json.Nodes;
using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// The debug-model single source of truth: `minuteos info --json` output must
/// match the minute-debug extension's InputLaunchConfiguration shape.
/// </summary>
public class MinuteDebugConfigTests : IDisposable
{
    private readonly string _root;

    public MinuteDebugConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-mdc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void Describe_MatchesTheLaunchConfigurationShape()
    {
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  board:\n    target: host\n    components: []\n" +
            "    settings:\n      gcc.toolchain-prefix: arm-none-eabi-\n" +
            "      debug.server: bmp\n      bmp.power: \"true\"\n" +
            "      smu.type: stlink\n      smu.voltage: \"3.3\"\n      debug.svd: EFR32MG12P\n");
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "board", _root);

        var model = MinuteDebugConfig.Describe(config);

        Assert.Equal("board", (string?)model["name"]);
        Assert.Equal("out/board/d.elf", (string?)model["program"]);
        Assert.Equal("arm-none-eabi-gdb", (string?)model["gdb"]);
        Assert.Equal("bmp", (string?)model["server"]!["type"]);
        Assert.True((bool?)model["server"]!["power"]);
        Assert.Equal(3.3, (double?)model["smu"]!["voltage"]);
        Assert.Equal("EFR32MG12P", (string?)model["svd"]);

        // Uncustomized presets collapse to the preset name.
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  q:\n    target: host\n    components: []\n" +
            "    settings:\n      debug.server: qemu\n");
        var qemu = BuildConfiguration.Create(ProjectConfig.Load(_root), "q", _root);
        Assert.Equal("qemu", (string?)(JsonNode?)MinuteDebugConfig.Describe(qemu)["server"]);
    }
}
