using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class ProjectConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void WriteConfig(string yaml)
    {
        File.WriteAllText(Path.Combine(_tempDir, ProjectConfig.FileName), yaml);
    }

    [Fact]
    public void Load_ParsesBasicConfig()
    {
        WriteConfig("""
            name: test-project
            defaults:
              target: host
              config: Release
              components:
                - kernel
            configurations:
              release: {}
            """);

        var config = ProjectConfig.Load(_tempDir);

        Assert.Equal("test-project", config.Name);
        Assert.Single(config.ConfigurationNames);
        Assert.Contains("release", config.ConfigurationNames);
    }

    [Fact]
    public void Resolve_InheritsDefaults()
    {
        WriteConfig("""
            name: test
            defaults:
              target: host
              config: Release
              components:
                - kernel
            configurations:
              release: {}
            """);

        var config = ProjectConfig.Load(_tempDir);
        var profile = config.Resolve("release");

        Assert.Equal("host", profile.Target);
        Assert.Equal("Release", profile.Config);
        Assert.NotNull(profile.Components);
        Assert.Contains("kernel", profile.Components);
    }

    [Fact]
    public void Resolve_OverridesDefaults()
    {
        WriteConfig("""
            name: test
            defaults:
              target: host
              config: Release
              components:
                - kernel
            configurations:
              debug:
                config: Debug
            """);

        var config = ProjectConfig.Load(_tempDir);
        var profile = config.Resolve("debug");

        Assert.Equal("host", profile.Target); // inherited
        Assert.Equal("Debug", profile.Config); // overridden
    }

    [Fact]
    public void Resolve_ComponentListReplaces()
    {
        WriteConfig("""
            name: test
            defaults:
              components:
                - kernel
                - base
            configurations:
              test:
                components:
                  - testrunner
            """);

        var config = ProjectConfig.Load(_tempDir);
        var profile = config.Resolve("test");

        Assert.NotNull(profile.Components);
        Assert.Single(profile.Components);
        Assert.Equal("testrunner", profile.Components[0]);
    }

    [Fact]
    public void Resolve_UnknownConfig_Throws()
    {
        WriteConfig("""
            name: test
            configurations:
              release: {}
            """);

        var config = ProjectConfig.Load(_tempDir);

        Assert.Throws<InvalidOperationException>(() => config.Resolve("nonexistent"));
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => ProjectConfig.Load(_tempDir));
    }

    [Fact]
    public void GetProjectRoot_WalksUpToFindConfig()
    {
        WriteConfig("name: test\nconfigurations:\n  r: {}\n");
        var subDir = Path.Combine(_tempDir, "sub", "deep");
        Directory.CreateDirectory(subDir);

        // Change to subdir context
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(subDir);
            var root = ProjectConfig.GetProjectRoot(null);
            Assert.Equal(Path.GetFullPath(_tempDir), root);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Fact]
    public void Resolve_ArmProfile_AllFieldsMerged()
    {
        WriteConfig("""
            name: firmware
            defaults:
              target: host
              config: Release
              components:
                - kernel
            configurations:
              arm:
                target: cortex-m4
                toolchain-prefix: arm-none-eabi-
                arch-flags:
                  - -mcpu=cortex-m4
                  - -mthumb
                primary-ext: .axf
                ld-script: default.ld
                link-flags:
                  - -nostartfiles
                defines:
                  - NDEBUG
            """);

        var config = ProjectConfig.Load(_tempDir);
        var profile = config.Resolve("arm");

        Assert.Equal("cortex-m4", profile.Target);
        Assert.Equal("arm-none-eabi-", profile.ToolchainPrefix);
        Assert.Equal(".axf", profile.PrimaryExt);
        Assert.Equal("default.ld", profile.LdScript);
        Assert.NotNull(profile.ArchFlags);
        Assert.Equal(2, profile.ArchFlags.Count);
        Assert.NotNull(profile.LinkFlags);
        Assert.Contains("-nostartfiles", profile.LinkFlags);
        Assert.NotNull(profile.Defines);
        Assert.Contains("NDEBUG", profile.Defines);
    }
}
