using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Tests;

public class ProjectLayoutTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectLayoutTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void CreateDir(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, relativePath));
    }

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Constructor_FindsLibRoots()
    {
        CreateDir("lib");
        CreateDir("lib-arm");
        CreateDir("not-a-lib");

        var layout = new ProjectLayout(_tempDir);

        Assert.Equal(2, layout.LibRoots.Count);
        Assert.Contains(layout.LibRoots, r => r.EndsWith("lib"));
        Assert.Contains(layout.LibRoots, r => r.EndsWith("lib-arm"));
    }

    [Fact]
    public void Constructor_FindsTargetRoots()
    {
        CreateDir("targets");
        CreateDir("lib/targets");
        CreateDir("lib-arm/targets");

        var layout = new ProjectLayout(_tempDir);

        Assert.Equal(3, layout.TargetRoots.Count);
    }

    [Fact]
    public void ResolveTargetDirs_FindsExistingDirs()
    {
        CreateDir("lib/targets/host");
        CreateDir("lib/targets/all");

        var layout = new ProjectLayout(_tempDir);
        var dirs = layout.ResolveTargetDirs(["host", "all"]);

        Assert.Equal(2, dirs.Count);
    }

    [Fact]
    public void ResolveTargetDirs_SkipsMissingDirs()
    {
        CreateDir("lib/targets/host");

        var layout = new ProjectLayout(_tempDir);
        var dirs = layout.ResolveTargetDirs(["host", "nonexistent"]);

        Assert.Single(dirs); // only host found, nonexistent skipped, all not found either
    }

    [Fact]
    public void ResolveTargetChain_FollowsInheritance()
    {
        CreateDir("lib/targets/all");
        CreateDir("lib/targets/cmsis");
        CreateDir("lib/targets/cortex-m");
        CreateDir("lib/targets/cortex-m3");

        WriteFile("lib/targets/cortex-m3/Include.mk", "TARGETS += cortex-m\n");
        WriteFile("lib/targets/cortex-m/Include.mk", "TARGETS += cmsis\n");

        var layout = new ProjectLayout(_tempDir);
        var chain = layout.ResolveTargetChain("cortex-m3", out _);

        // Should be: cmsis, cortex-m, cortex-m3, all
        Assert.Equal(4, chain.Count);
        Assert.Equal("cmsis", chain[0]);
        Assert.Equal("cortex-m", chain[1]);
        Assert.Equal("cortex-m3", chain[2]);
        Assert.Equal("all", chain[3]);
    }

    [Fact]
    public void ResolveTargetChain_IncludeMk_CapturesTargetSettings()
    {
        // Mirrors lib-arm/targets/cortex-m/Include.mk.
        CreateDir("lib/targets/all");
        CreateDir("lib/targets/cmsis");
        WriteFile("lib/targets/cortex-m/Include.mk",
            "TOOLCHAIN_PREFIX = arm-none-eabi-\n" +
            "ARCH_FLAGS ?= -mthumb\n" +
            "LD_SCRIPT ?= default.ld\n" +
            "DEFINES += LINKER_ORDERED_SECTION=\\\".text.ord\\\"\n" +
            "LINK_FLAGS += -T$(LD_SCRIPT) -nostartfiles -specs=nano.specs\n" +
            "PRIMARY_EXT = .axf\n" +
            "TARGETS += cmsis\n");

        var layout = new ProjectLayout(_tempDir);
        var chain = layout.ResolveTargetChain("cortex-m", out var meta);

        Assert.Contains("cmsis", chain);  // TARGETS += parsed
        var cm = meta["cortex-m"];

        // Toolchain-specific values land in the generic settings map (gcc.*).
        Assert.NotNull(cm.Settings);
        Assert.Equal("arm-none-eabi-", cm.Settings["gcc.toolchain-prefix"]);
        Assert.Equal(".axf", cm.Settings["gcc.primary-ext"]);
        Assert.Equal("default.ld", cm.Settings["gcc.ld-script"]);
        var arch = (List<string>)cm.Settings["gcc.arch-flags"];
        Assert.Contains("-mthumb", arch);

        // Define quotes are unescaped from Make form (defines stay typed).
        Assert.NotNull(cm.Defines);
        Assert.Contains("LINKER_ORDERED_SECTION=\".text.ord\"", cm.Defines);

        // LINK_FLAGS: static tokens kept, $(...) tokens dropped.
        var linkFlags = (List<string>)cm.Settings["gcc.link-flags"];
        Assert.Contains("-nostartfiles", linkFlags);
        Assert.Contains("-specs=nano.specs", linkFlags);
        Assert.DoesNotContain(linkFlags, f => f.Contains("$("));
    }

    [Fact]
    public void ResolveTargetChain_IncludeMk_ParsesTestRun()
    {
        // Mirrors lib-arm/targets/qemu-arm/Include.mk.
        CreateDir("lib/targets/all");
        WriteFile("lib/targets/qemu-arm/Include.mk",
            "TEST_RUN = qemu-system-arm -machine lm3s6965evb -nographic -semihosting -kernel\n" +
            "TEST_RUN_ARGS = -append \"$(TEST_FILTERS)\"\n");

        var layout = new ProjectLayout(_tempDir);
        layout.ResolveTargetChain("qemu-arm", out var meta);

        // TEST_RUN becomes a Run-phase step (the test-runner replacement).
        var steps = meta["qemu-arm"].Steps;
        Assert.NotNull(steps);
        var run = Assert.Single(steps, s => s.Name == "run");
        Assert.Equal(BuildPhase.Run, run.Phase);
        Assert.Equal("qemu-system-arm", run.Config!["command"]);
        // Remaining TEST_RUN tokens, then {image}, then TEST_RUN_ARGS with the
        // filter placeholder; placeholders quoted for the shell-style args string.
        Assert.Equal(
            "-machine lm3s6965evb -nographic -semihosting -kernel \"{image}\" -append \"{filter}\"",
            run.Config!["args"]);
    }

    [Fact]
    public void ResolveTargetChain_UsesTargetYaml()
    {
        CreateDir("lib/targets/all");
        CreateDir("lib/targets/parent");
        CreateDir("lib/targets/child");

        WriteFile("lib/targets/child/target.yaml", "requires:\n  - parent\n");

        var layout = new ProjectLayout(_tempDir);
        var chain = layout.ResolveTargetChain("child", out var meta);

        Assert.Equal(3, chain.Count);
        Assert.Equal("parent", chain[0]);
        Assert.Equal("child", chain[1]);
        Assert.Equal("all", chain[2]);
        Assert.True(meta.ContainsKey("child"));
    }

    [Fact]
    public void ResolveTargetChain_MultipleParents()
    {
        CreateDir("lib/targets/all");
        CreateDir("lib/targets/a");
        CreateDir("lib/targets/b");
        CreateDir("lib/targets/child");

        WriteFile("lib/targets/child/Include.mk", "TARGETS += a b\n");

        var layout = new ProjectLayout(_tempDir);
        var chain = layout.ResolveTargetChain("child", out _);

        Assert.Equal(4, chain.Count);
        Assert.Contains("a", chain);
        Assert.Contains("b", chain);
        Assert.Contains("child", chain);
        Assert.Equal("all", chain[^1]);
    }

    [Fact]
    public void ResolveComponentDirs_FindsAcrossTargetDirs()
    {
        CreateDir("lib/targets/host/kernel");
        CreateDir("lib/targets/all/kernel");
        CreateDir("lib/targets/all/base");

        var layout = new ProjectLayout(_tempDir);
        var targetDirs = layout.ResolveTargetDirs(["host", "all"]);

        var componentDirs = layout.ResolveComponentDirs(targetDirs, ["kernel", "base"]);

        // kernel in host + all, base in all
        Assert.Equal(3, componentDirs.Count);
    }

    [Fact]
    public void Name_DefaultsToDirectoryName()
    {
        var layout = new ProjectLayout(_tempDir);
        Assert.Equal(Path.GetFileName(_tempDir), layout.Name);
    }

    [Fact]
    public void Name_CanBeOverridden()
    {
        var layout = new ProjectLayout(_tempDir, "my-project");
        Assert.Equal("my-project", layout.Name);
    }
}
