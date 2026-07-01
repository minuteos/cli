using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

public class SourceCollectorTests : IDisposable
{
    private readonly string _tempDir;

    public SourceCollectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void WriteFile(string relativePath, string content = "")
    {
        var path = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void CollectSources_FindsCppFiles()
    {
        WriteFile("src/main.cpp");
        WriteFile("src/util.cpp");
        WriteFile("src/header.h"); // not a source

        var collector = new SourceCollector();
        var sources = collector.CollectSources([Path.Combine(_tempDir, "src")], _tempDir);

        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(SourceLanguage.Cpp, s.Language));
    }

    [Fact]
    public void CollectSources_FindsCFiles()
    {
        WriteFile("src/lib.c");

        var collector = new SourceCollector();
        var sources = collector.CollectSources([Path.Combine(_tempDir, "src")], _tempDir);

        Assert.Single(sources);
        Assert.Equal(SourceLanguage.C, sources[0].Language);
    }

    [Fact]
    public void CollectSources_FindsAssemblyFiles()
    {
        WriteFile("src/startup.S");

        var collector = new SourceCollector();
        var sources = collector.CollectSources([Path.Combine(_tempDir, "src")], _tempDir);

        Assert.Single(sources);
        Assert.Equal(SourceLanguage.Assembly, sources[0].Language);
    }

    [Fact]
    public void CollectSources_MultipleDirectories()
    {
        WriteFile("src/main.cpp");
        WriteFile("lib/targets/all/base/base.cpp");

        var collector = new SourceCollector();
        var sources = collector.CollectSources([
            Path.Combine(_tempDir, "src"),
            Path.Combine(_tempDir, "lib/targets/all/base"),
        ], _tempDir);

        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public void CollectSources_DeduplicatesFiles()
    {
        WriteFile("src/main.cpp");

        var collector = new SourceCollector();
        var srcDir = Path.Combine(_tempDir, "src");
        var sources = collector.CollectSources([srcDir, srcDir], _tempDir);

        Assert.Single(sources);
    }

    [Fact]
    public void CollectSources_SkipsMissingDirs()
    {
        var collector = new SourceCollector();
        var sources = collector.CollectSources(["/nonexistent/dir"], _tempDir);

        Assert.Empty(sources);
    }

    [Fact]
    public void CollectSources_RelativePaths()
    {
        WriteFile("src/main.cpp");

        var collector = new SourceCollector();
        var sources = collector.CollectSources([Path.Combine(_tempDir, "src")], _tempDir);

        Assert.Single(sources);
        Assert.Equal("src/main.cpp", sources[0].RelativePath);
    }
}
