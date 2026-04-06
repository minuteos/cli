using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class DepFileTests : IDisposable
{
    private readonly string _tempDir;

    public DepFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteDepFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Parse_SimpleRule_ReturnsDependencies()
    {
        var path = WriteDepFile("test.d",
            "obj/main.o: src/main.cpp src/header.h\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Equal(2, deps.Count);
        Assert.Equal("src/main.cpp", deps[0]);
        Assert.Equal("src/header.h", deps[1]);
    }

    [Fact]
    public void Parse_ContinuationLines_JoinsCorrectly()
    {
        var path = WriteDepFile("test.d",
            "obj/main.o: src/main.cpp \\\n src/header1.h \\\n src/header2.h\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Equal(3, deps.Count);
        Assert.Equal("src/main.cpp", deps[0]);
        Assert.Equal("src/header1.h", deps[1]);
        Assert.Equal("src/header2.h", deps[2]);
    }

    [Fact]
    public void Parse_WithPhonyTargets_IgnoresThem()
    {
        var path = WriteDepFile("test.d",
            "obj/main.o: src/main.cpp src/header.h\n" +
            "src/header.h:\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Equal(2, deps.Count);
        Assert.Equal("src/main.cpp", deps[0]);
        Assert.Equal("src/header.h", deps[1]);
    }

    [Fact]
    public void Parse_AbsolutePaths_Works()
    {
        var path = WriteDepFile("test.d",
            "/tmp/out/obj/main.o: /tmp/src/main.cpp \\\n /tmp/lib/header.h\n" +
            "/tmp/lib/header.h:\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Equal(2, deps.Count);
        Assert.Equal("/tmp/src/main.cpp", deps[0]);
        Assert.Equal("/tmp/lib/header.h", deps[1]);
    }

    [Fact]
    public void Parse_EscapedSpaces_HandledCorrectly()
    {
        var path = WriteDepFile("test.d",
            "obj/main.o: src/my\\ file.cpp\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Single(deps);
        Assert.Equal("src/my file.cpp", deps[0]);
    }

    [Fact]
    public void Parse_NonExistentFile_ReturnsNull()
    {
        var deps = DepFile.Parse("/nonexistent/path.d");
        Assert.Null(deps);
    }

    [Fact]
    public void Parse_EmptyFile_ReturnsNull()
    {
        var path = WriteDepFile("test.d", "");
        var deps = DepFile.Parse(path);
        Assert.Null(deps);
    }

    [Fact]
    public void Parse_RealGccOutput_Works()
    {
        // Realistic output from gcc -MMD -MP
        var path = WriteDepFile("main.d",
            "/tmp/out/obj/src/main.o: /tmp/src/main.cpp \\\n" +
            " /tmp/lib/targets/all/kernel/kernel.h \\\n" +
            " /tmp/lib/targets/all/base/base.h \\\n" +
            " /tmp/lib/targets/all/base/debug.h\n" +
            "/tmp/lib/targets/all/kernel/kernel.h:\n" +
            "/tmp/lib/targets/all/base/base.h:\n" +
            "/tmp/lib/targets/all/base/debug.h:\n");

        var deps = DepFile.Parse(path);

        Assert.NotNull(deps);
        Assert.Equal(4, deps.Count);
        Assert.Equal("/tmp/src/main.cpp", deps[0]);
        Assert.Equal("/tmp/lib/targets/all/kernel/kernel.h", deps[1]);
        Assert.Equal("/tmp/lib/targets/all/base/base.h", deps[2]);
        Assert.Equal("/tmp/lib/targets/all/base/debug.h", deps[3]);
    }

    [Fact]
    public void GetDepPath_ChangesExtension()
    {
        Assert.Equal("obj/main.d", DepFile.GetDepPath("obj/main.o"));
        Assert.Equal("/tmp/out/foo.d", DepFile.GetDepPath("/tmp/out/foo.o"));
    }
}
