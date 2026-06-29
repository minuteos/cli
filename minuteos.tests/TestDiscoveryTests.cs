using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class TestDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public TestDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void WriteSource(string relativePath)
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// test\n");
    }

    [Fact]
    public void Discover_FlatComponent_FindsSuite()
    {
        WriteSource("all/base/tests/sanity/sanity.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")]);

        Assert.Single(suites);
        Assert.Equal("base", suites[0].Component);
        Assert.Equal("sanity", suites[0].Name);
        Assert.Equal("base/sanity", suites[0].Id);
    }

    [Fact]
    public void Discover_NestedComponent_DerivesPathName()
    {
        WriteSource("all/sensors/environment/tests/basic/basic.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")]);

        Assert.Single(suites);
        Assert.Equal("sensors/environment", suites[0].Component);
        Assert.Equal("basic", suites[0].Name);
    }

    [Fact]
    public void Discover_MultipleSuites_AllFound()
    {
        WriteSource("all/base/tests/sanity/a.cpp");
        WriteSource("all/base/tests/edge/b.cpp");
        WriteSource("all/kernel/tests/sched/c.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")]);

        Assert.Equal(3, suites.Count);
        Assert.Contains(suites, s => s.Id == "base/sanity");
        Assert.Contains(suites, s => s.Id == "base/edge");
        Assert.Contains(suites, s => s.Id == "kernel/sched");
    }

    [Fact]
    public void Discover_EmptySuiteDir_Ignored()
    {
        // tests/empty/ exists but has no source files
        Directory.CreateDirectory(Path.Combine(_tempDir, "all/base/tests/empty"));
        WriteSource("all/base/tests/real/real.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")]);

        Assert.Single(suites);
        Assert.Equal("real", suites[0].Name);
    }

    [Fact]
    public void Discover_DedupesByComponentAndSuite()
    {
        // Same logical suite present under two target dirs (all + host override)
        WriteSource("all/base/tests/sanity/a.cpp");
        WriteSource("host/base/tests/sanity/a.cpp");

        var suites = new TestDiscovery().Discover([
            Path.Combine(_tempDir, "host"),
            Path.Combine(_tempDir, "all"),
        ]);

        Assert.Single(suites);
    }

    [Fact]
    public void Discover_ComponentFilter_LimitsResults()
    {
        WriteSource("all/base/tests/sanity/a.cpp");
        WriteSource("all/kernel/tests/sched/c.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")], ["base"]);

        Assert.Single(suites);
        Assert.Equal("base", suites[0].Component);
    }

    [Fact]
    public void Discover_NoTests_ReturnsEmpty()
    {
        WriteSource("all/base/base.cpp");

        var suites = new TestDiscovery().Discover([Path.Combine(_tempDir, "all")]);

        Assert.Empty(suites);
    }
}
