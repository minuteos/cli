using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class MetaYamlRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public MetaYamlRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void TargetMeta_RoundTrips_ThroughYaml()
    {
        var meta = new TargetMeta
        {
            Requires = ["cmsis"],
            ToolchainPrefix = "arm-none-eabi-",
            ArchFlags = ["-mcpu=cortex-m3", "-mthumb"],
            LinkFlags = ["-nostartfiles", "-specs=nano.specs"],
            PrimaryExt = ".axf",
            LdScript = "default.ld",
            LinkDirs = ["ld_fallbacks/"],
            Defines = ["LINKER_ORDERED_SECTION=\".text.ord\""],
            TestRunner = new TestRunnerConfig
            {
                Command = "qemu-system-arm",
                // "null" must survive the round-trip as the string, not YAML null.
                Args = ["-monitor", "null", "-kernel", "{binary}", "-append", "{filter}"],
            },
        };

        var dir = Path.Combine(_tempDir, "cortex-m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, TargetMeta.FileName), meta.ToYaml());

        var loaded = TargetMeta.TryLoad(dir, "cortex-m");

        Assert.NotNull(loaded);
        Assert.Equal(["cmsis"], loaded.Requires);
        Assert.Equal("arm-none-eabi-", loaded.ToolchainPrefix);
        Assert.Equal(["-mcpu=cortex-m3", "-mthumb"], loaded.ArchFlags);
        Assert.Equal(".axf", loaded.PrimaryExt);
        Assert.Equal("default.ld", loaded.LdScript);
        Assert.Equal(["ld_fallbacks/"], loaded.LinkDirs);
        Assert.Equal(["LINKER_ORDERED_SECTION=\".text.ord\""], loaded.Defines);
        Assert.NotNull(loaded.TestRunner);
        Assert.Equal("qemu-system-arm", loaded.TestRunner.Command);
        Assert.Equal(["-monitor", "null", "-kernel", "{binary}", "-append", "{filter}"], loaded.TestRunner.Args);
    }

    [Fact]
    public void ComponentMeta_RoundTrips_ThroughYaml()
    {
        var meta = new ComponentMeta
        {
            Requires = ["base"],
            Defines = ["KERNEL_PLATFORM_HEADER=testrunner/kernel_platform.h"],
        };

        var dir = Path.Combine(_tempDir, "testrunner");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ComponentMeta.FileName), meta.ToYaml());

        var loaded = ComponentMeta.TryLoad(dir, "testrunner");

        Assert.NotNull(loaded);
        Assert.Equal(["base"], loaded.Requires);
        Assert.Equal(["KERNEL_PLATFORM_HEADER=testrunner/kernel_platform.h"], loaded.Defines);
    }

    [Fact]
    public void MakeImport_ToYaml_TryLoad_PreservesTargetSettings()
    {
        // Full Include.mk -> YAML -> reload pipeline (what `migrate` does).
        var dir = Path.Combine(_tempDir, "cortex-m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MakeImport.FileName),
            "TOOLCHAIN_PREFIX = arm-none-eabi-\n" +
            "ARCH_FLAGS ?= -mthumb\n" +
            "PRIMARY_EXT = .axf\n" +
            "TARGETS += cmsis\n");

        var imported = MakeImport.LoadTarget(dir, "cortex-m");
        Assert.NotNull(imported);

        File.WriteAllText(Path.Combine(dir, TargetMeta.FileName), imported.ToYaml());
        var reloaded = TargetMeta.TryLoad(dir, "cortex-m");

        Assert.NotNull(reloaded);
        Assert.Equal(imported.ToolchainPrefix, reloaded.ToolchainPrefix);
        Assert.Equal(imported.PrimaryExt, reloaded.PrimaryExt);
        Assert.Equal(imported.ArchFlags, reloaded.ArchFlags);
        Assert.Equal(imported.Requires, reloaded.Requires);
    }
}
