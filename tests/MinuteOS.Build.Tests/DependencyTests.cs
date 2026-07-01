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
    [InlineData(null, null, null, "d", DependencyKind.Path)]     // path set
    [InlineData("x", "abc", null, null, DependencyKind.Tar)]     // git + commit
    [InlineData(null, null, "t.tgz", null, DependencyKind.Tar)]  // explicit tar
    [InlineData("x", null, null, null, DependencyKind.Clone)]    // git only
    public void Kind_IsInferredFromFields(string? git, string? commit, string? tar, string? path, DependencyKind expected)
    {
        var dep = new Dependency { Name = "lib", Git = git, Commit = commit, Tar = tar, Path = path };
        Assert.Equal(expected, dep.Kind);
    }

    [Fact]
    public void ResolveDir_ByKind()
    {
        var root = "/proj";
        Assert.Equal(Path.GetFullPath("/proj/../shared/lib"),
            DependencyRestorer.ResolveDir(new Dependency { Name = "lib", Path = "../shared/lib" }, root));
        Assert.Equal(Path.Combine(root, "lib"),
            DependencyRestorer.ResolveDir(new Dependency { Name = "lib", Git = "u" }, root));

        var tar = DependencyRestorer.ResolveDir(
            new Dependency { Name = "lib", Git = "u", Commit = "abc123" }, root);
        Assert.StartsWith(DependencyRestorer.CacheRoot, tar);
        Assert.EndsWith(Path.Combine("lib", "abc123"), tar);   // cache keyed by commit
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
