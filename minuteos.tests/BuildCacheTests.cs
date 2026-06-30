using MinuteOS.Cli.Build.Graph;

namespace MinuteOS.Cli.Tests;

/// <summary>
/// Covers the action cache: skip-when-unchanged, removed-input detection (the
/// declared-input set must match), config-change detection, and orphan tracking.
/// </summary>
public class BuildCacheTests : IDisposable
{
    private readonly string _dir;

    public BuildCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"minuteos-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static BuildAction Compile(string label, IReadOnlyList<Artifact> ins, string outPath, string config = "cc") =>
        new(label, ins, [Artifact.File(outPath, ("kind", "object"))], _ => Task.FromResult(ActionResult.Ok())) { ConfigKey = config };

    [Fact]
    public void UpToDate_AfterRecord_ThenStale_OnInputChange()
    {
        var src = Write("a.cpp", "one");
        var obj = Path.Combine(_dir, "a.o");
        File.WriteAllText(obj, "obj");
        var action = Compile("compile a", [Artifact.File(src, ("kind", "source"))], obj);

        var cache = BuildCache.Load(_dir);
        Assert.False(cache.IsUpToDate(action, "cc")); // no entry yet
        cache.Record(action, ActionResult.Ok(), "cc");
        Assert.True(cache.IsUpToDate(action, "cc"));

        // Changing the config fingerprint invalidates.
        Assert.False(cache.IsUpToDate(action, "cc -O2"));

        // Touching the input invalidates.
        File.WriteAllText(src, "two");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddSeconds(5));
        Assert.False(cache.IsUpToDate(action, "cc"));
    }

    [Fact]
    public void RemovedDeclaredInput_IsDetected()
    {
        var o1 = Write("1.o", "a");
        var o2 = Write("2.o", "b");
        var image = Path.Combine(_dir, "app.elf");
        File.WriteAllText(image, "elf");

        Artifact A(string p) => Artifact.File(p, ("kind", "object"));
        var link2 = new BuildAction("link", [A(o1), A(o2)], [Artifact.File(image, ("kind", "image"))],
            _ => Task.FromResult(ActionResult.Ok())) { ConfigKey = "ld" };

        var cache = BuildCache.Load(_dir);
        cache.Record(link2, ActionResult.Ok(), "ld");
        Assert.True(cache.IsUpToDate(link2, "ld"));

        // One object removed from the link line -> declared-input set differs -> stale.
        var link1 = link2 with { Inputs = [A(o1)] };
        Assert.False(cache.IsUpToDate(link1, "ld"));
    }

    [Fact]
    public void Orphans_AreOutputsNoLongerProduced()
    {
        var oApp = Write("app.o", "a");
        var oExtra = Write("extra.o", "b");

        var cache = BuildCache.Load(_dir);
        cache.Record(Compile("compile app", [], oApp), ActionResult.Ok(), "cc");
        cache.Record(Compile("compile extra", [], oExtra), ActionResult.Ok(), "cc");
        cache.Save();

        // Next build: only "compile app" is seen.
        var next = BuildCache.Load(_dir);
        next.Record(Compile("compile app", [], oApp), ActionResult.Ok(), "cc");
        next.RetainOnly(new HashSet<string> { "compile app" });

        Assert.Equal([oExtra], next.Orphans().ToList());
    }
}
