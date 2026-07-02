using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Covers dependency declaration parsing and the missing-dependency check that
/// drives build-time guidance / `minuteos restore`.
/// </summary>
public class DependencyTests : IDisposable
{
    private readonly string _root;

    public DependencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-dep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private ProjectConfig Load(string yaml)
    {
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"), yaml);
        return ProjectConfig.Load(_root);
    }

    [Fact]
    public void Dependencies_Parse_FromYaml()
    {
        var project = Load(
            "name: p\n" +
            "dependencies:\n" +
            "  - name: lib\n" +
            "    git: https://example.com/lib\n" +
            "  - name: lib-arm\n" +
            "    git: https://example.com/lib-arm\n" +
            "    ref: main\n" +
            "configurations:\n  host:\n    target: host\n    components: []\n");

        Assert.NotNull(project.Dependencies);
        Assert.Equal(2, project.Dependencies!.Count);
        Assert.Equal("lib", project.Dependencies[0].Directory);
        Assert.Equal("main", project.Dependencies[1].Ref);
    }

    [Theory]
    [InlineData(null, null, "d", null, DependencyKind.Path)]      // explicit path
    [InlineData(null, null, null, null, DependencyKind.Path)]     // name only (submodule dir)
    [InlineData("x", "main", null, null, DependencyKind.Remote)]  // git + branch
    [InlineData("x", null, null, null, DependencyKind.Remote)]    // git, default HEAD
    [InlineData(null, null, null, "t.tgz", DependencyKind.Remote)] // explicit tarball
    public void Kind_IsInferredFromFields(string? git, string? @ref, string? path, string? tar, DependencyKind expected)
    {
        var dep = new Dependency { Name = "lib", Git = git, Ref = @ref, Tar = tar, Path = path };
        Assert.Equal(expected, dep.Kind);
    }

    [Theory]
    [InlineData("0a1b2c3d", true)]                             // short sha
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)] // full sha
    [InlineData("main", false)]
    [InlineData("v1.2.3", false)]
    [InlineData(null, false)]                                  // default HEAD
    public void RefIsCommit_ClassifiesRefs(string? @ref, bool isCommit)
    {
        Assert.Equal(isCommit, new Dependency { Name = "l", Git = "u", Ref = @ref }.RefIsCommit);
    }

    [Fact]
    public void ResolveDir_ByKind()
    {
        // path: explicit dir, or name as the dir (a submodule).
        Assert.Equal(Path.GetFullPath("/proj/../shared/lib"),
            DependencyRestorer.ResolveDir(new Dependency { Name = "lib", Path = "../shared/lib" }, "/proj"));
        Assert.Equal(Path.Combine(Path.GetFullPath("/proj"), "lib"),
            DependencyRestorer.ResolveDir(new Dependency { Name = "lib" }, "/proj"));

        // remote pinned to a commit: cache keyed by the commit, no lock needed.
        var pinned = DependencyRestorer.ResolveDir(
            new Dependency { Name = "lib", Git = "u", Ref = "abc1234" }, _root);
        Assert.StartsWith(DependencyRestorer.CacheRoot, pinned);
        Assert.EndsWith(Path.Combine("lib", "abc1234"), pinned);

        // remote on a branch: unresolved until restore writes the lock...
        var dep = new Dependency { Name = "lib", Git = "u", Ref = "main" };
        Assert.EndsWith("_unresolved_", DependencyRestorer.ResolveDir(dep, _root));

        // ...then resolves through the locked commit.
        File.WriteAllText(Path.Combine(_root, DependencyLock.FileName), "lib: fedcba9876543210\n");
        Assert.EndsWith(Path.Combine("lib", "fedcba9876543210"),
            DependencyRestorer.ResolveDir(dep, _root));
    }

    [Fact]
    public async Task Frozen_FailsOnUnlockedMutableRef_UsesLockOtherwise()
    {
        var cache = Path.Combine(_root, "cache");
        Environment.SetEnvironmentVariable("MINUTEOS_CACHE", cache);
        try
        {
            var project = Load(
                "name: p\n" +
                "dependencies:\n  - name: lib\n    git: https://example.invalid/lib\n    ref: main\n" +
                "configurations:\n  host:\n    target: host\n    components: []\n");
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

            // No lock entry: frozen restore must fail without touching the network.
            var results = await DependencyRestorer.RestoreAsync(project, _root, logger, CancellationToken.None, frozen: true);
            var r = Assert.Single(results);
            Assert.False(r.Ok);
            Assert.Contains("not locked", r.Status);

            // Locked + cached: frozen restore succeeds offline.
            File.WriteAllText(Path.Combine(_root, DependencyLock.FileName), "lib: abc1234def5678\n");
            var dir = Path.Combine(cache, "lib", "abc1234def5678");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "x"), "");

            results = await DependencyRestorer.RestoreAsync(project, _root, logger, CancellationToken.None, frozen: true);
            r = Assert.Single(results);
            Assert.True(r.Ok, r.Status);
            Assert.StartsWith("cached", r.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MINUTEOS_CACHE", null);
        }
    }

    [Fact]
    public void MissingDependencies_ReportsAbsentAndEmptyDirs()
    {
        var project = Load(
            "name: p\n" +
            "dependencies:\n  - name: present\n  - name: absent\n  - name: empty\n" +
            "configurations:\n  host:\n    target: host\n    components: []\n");

        // 'present' has content, 'empty' exists but is empty, 'absent' is missing.
        Directory.CreateDirectory(Path.Combine(_root, "present"));
        File.WriteAllText(Path.Combine(_root, "present", "x"), "");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var missing = DependencyRestorer.MissingDependencies(project, _root);

        Assert.Contains("absent", missing);
        Assert.Contains("empty", missing);
        Assert.DoesNotContain("present", missing);
    }
}
