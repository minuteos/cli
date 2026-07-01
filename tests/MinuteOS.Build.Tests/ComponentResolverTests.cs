using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

public class ComponentResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ComponentResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private void CreateComponent(string targetDir, string component, string? includeMkContent = null, string? componentYaml = null)
    {
        var dir = Path.Combine(_tempDir, targetDir, component);
        Directory.CreateDirectory(dir);

        if (includeMkContent != null)
            File.WriteAllText(Path.Combine(dir, "Include.mk"), includeMkContent);

        if (componentYaml != null)
            File.WriteAllText(Path.Combine(dir, "component.yaml"), componentYaml);
    }

    [Fact]
    public void ResolveComponents_NoDeps_ReturnsSingle()
    {
        CreateComponent("targets/all", "base");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["base"], targetDirs);

        Assert.Single(result);
        Assert.Equal("base", result[0]);
    }

    [Fact]
    public void ResolveComponents_IncludeMkFallback_ResolvesDeps()
    {
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "kernel", includeMkContent: "COMPONENTS += base\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["kernel"], targetDirs);

        Assert.Equal(2, result.Count);
        Assert.Equal("base", result[0]); // dependency first
        Assert.Equal("kernel", result[1]);
    }

    [Fact]
    public void ResolveComponents_ComponentYaml_TakesPrecedence()
    {
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "kernel",
            includeMkContent: "COMPONENTS += wrong\n",
            componentYaml: "requires:\n  - base\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["kernel"], targetDirs);

        Assert.Equal(2, result.Count);
        Assert.Equal("base", result[0]);
        Assert.Equal("kernel", result[1]);
    }

    [Fact]
    public void ResolveComponents_TransitiveDeps_Resolved()
    {
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "kernel", includeMkContent: "COMPONENTS += base\n");
        CreateComponent("targets/all", "io", includeMkContent: "COMPONENTS += kernel\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["io"], targetDirs);

        Assert.Equal(3, result.Count);
        Assert.Equal("base", result[0]);
        Assert.Equal("kernel", result[1]);
        Assert.Equal("io", result[2]);
    }

    [Fact]
    public void ResolveComponents_CircularDep_Throws()
    {
        CreateComponent("targets/all", "a", componentYaml: "requires:\n  - b\n");
        CreateComponent("targets/all", "b", componentYaml: "requires:\n  - a\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        Assert.Throws<InvalidOperationException>(() =>
            resolver.ResolveComponents(["a"], targetDirs));
    }

    [Fact]
    public void ResolveComponents_DuplicateDeps_Deduplicated()
    {
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "kernel", includeMkContent: "COMPONENTS += base\n");
        CreateComponent("targets/all", "io", includeMkContent: "COMPONENTS += kernel base\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["io"], targetDirs);

        Assert.Equal(3, result.Count);
        Assert.Equal("base", result[0]);
        Assert.Equal("kernel", result[1]);
        Assert.Equal("io", result[2]);
    }

    [Fact]
    public void ResolveComponents_ComponentYaml_CollectsMetadata()
    {
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "mylib", componentYaml:
            "requires:\n  - base\ndefines:\n  - MYLIB_ENABLED\nc-flags:\n  - -Wno-unused\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        resolver.ResolveComponents(["mylib"], targetDirs);

        Assert.True(resolver.ComponentMetadata.ContainsKey("mylib"));
        var meta = resolver.ComponentMetadata["mylib"];
        Assert.NotNull(meta.Defines);
        Assert.Contains("MYLIB_ENABLED", meta.Defines);
        Assert.NotNull(meta.CFlags);
        Assert.Contains("-Wno-unused", meta.CFlags);
    }

    [Fact]
    public void ResolveComponents_IncludeMk_CapturesDefines()
    {
        // Mirrors the real lib's testrunner/Include.mk, which sets a define.
        CreateComponent("targets/all", "base");
        CreateComponent("targets/all", "testrunner", includeMkContent:
            "DEFINES += KERNEL_PLATFORM_HEADER=testrunner/kernel_platform.h\nCOMPONENTS += base\n");

        var layout = new ProjectLayout(_tempDir);
        var resolver = new ComponentResolver(layout);
        var targetDirs = layout.ResolveTargetDirs(["all"]);

        var result = resolver.ResolveComponents(["testrunner"], targetDirs);

        // COMPONENTS += base pulls base in (dependency-first order).
        Assert.Equal(["base", "testrunner"], result);

        // DEFINES += is captured into the component metadata.
        var meta = resolver.ComponentMetadata["testrunner"];
        Assert.NotNull(meta.Defines);
        Assert.Contains("KERNEL_PLATFORM_HEADER=testrunner/kernel_platform.h", meta.Defines);
    }
}
