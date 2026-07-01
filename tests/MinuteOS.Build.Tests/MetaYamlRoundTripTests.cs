using MinuteOS.Build;
using MinuteOS.Build.Steps;

namespace MinuteOS.Build.Tests;

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
    public void MakeImport_EmitsSettings_AndRoundTrips()
    {
        // Full Include.mk -> YAML -> reload pipeline (what `migrate` does). The
        // toolchain-specific values land in the generic settings map.
        var dir = Path.Combine(_tempDir, "cortex-m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MakeImport.FileName),
            "TOOLCHAIN_PREFIX = arm-none-eabi-\n" +
            "ARCH_FLAGS ?= -mthumb\n" +
            "PRIMARY_EXT = .axf\n" +
            "TARGETS += cmsis\n");

        var imported = MakeImport.LoadTarget(dir, "cortex-m");
        Assert.NotNull(imported);
        Assert.NotNull(imported.Settings);
        Assert.Equal("arm-none-eabi-", imported.Settings["gcc.toolchain-prefix"]);
        Assert.Equal(".axf", imported.Settings["gcc.primary-ext"]);
        Assert.Equal(["cmsis"], imported.Requires);
        // The deprecated typed fields are no longer populated by the importer.
        Assert.Null(imported.ToolchainPrefix);
        Assert.Null(imported.ArchFlags);

        File.WriteAllText(Path.Combine(dir, TargetMeta.FileName), imported.ToYaml());
        var reloaded = TargetMeta.TryLoad(dir, "cortex-m");

        Assert.NotNull(reloaded);
        Assert.Equal(["cmsis"], reloaded.Requires);
        // Round-trip is stable (settings survive serialize -> reload).
        Assert.Equal(imported.ToYaml(), reloaded.ToYaml());
    }

    [Fact]
    public void MakeImport_TestRun_BecomesRunStep()
    {
        var dir = Path.Combine(_tempDir, "qemu");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MakeImport.FileName),
            "TARGETS += cortex-m\n" +
            "TEST_RUN = qemu-system-arm -machine lm3s6965evb -nographic -semihosting -kernel\n" +
            "TEST_RUN_ARGS = -append $(TEST_FILTERS)\n");

        var meta = MakeImport.LoadTarget(dir, "qemu");
        Assert.NotNull(meta);
        Assert.Null(meta.TestRunner);     // no longer emitted
        Assert.NotNull(meta.Steps);

        var run = Assert.Single(meta.Steps, s => s.Name == "run");
        Assert.Equal(BuildPhase.Run, run.Phase);
        Assert.Equal("qemu-system-arm", run.Config!["command"]);
        Assert.Equal(
            "-machine lm3s6965evb -nographic -semihosting -kernel \"{image}\" -append \"{filter}\"",
            run.Config!["args"]);
    }

    [Fact]
    public void TargetMeta_SettingsMap_RoundTrips()
    {
        var meta = new TargetMeta
        {
            Requires = ["cmsis"],
            Settings = new Dictionary<string, object>
            {
                ["gcc.toolchain-prefix"] = "arm-none-eabi-",
                ["gcc.arch-flags"] = new List<string> { "-mcpu=cortex-m3", "-mthumb" },
                ["gcc.ld-script"] = "lm3s.ld",
            },
        };

        var dir = Path.Combine(_tempDir, "t");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, TargetMeta.FileName), meta.ToYaml());

        var loaded = TargetMeta.TryLoad(dir, "t");
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Settings);
        Assert.Equal("arm-none-eabi-", loaded.Settings["gcc.toolchain-prefix"]);
        Assert.Equal("lm3s.ld", loaded.Settings["gcc.ld-script"]);
        var arch = Assert.IsAssignableFrom<System.Collections.IEnumerable>(loaded.Settings["gcc.arch-flags"]);
        Assert.Contains("-mthumb", arch.Cast<object>().Select(o => o.ToString()));
    }
}
