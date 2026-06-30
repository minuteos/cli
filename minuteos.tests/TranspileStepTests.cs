using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Tests;

/// <summary>
/// Exercises the in-process (stubbed) C#-&gt;C++ transpile step: it runs inside the
/// builder (no external CLI), receives the whole-project input set, and registers
/// the generated translation units back into the build.
/// </summary>
public class TranspileStepTests : IDisposable
{
    private readonly string _root;

    public TranspileStepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-transpile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteProject() => Write("minuteos.yaml",
        "name: demo\nconfigurations:\n  default:\n    target: host\n    components: []\n");

    private StepContext MakeContext(out BuildState state)
    {
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "default", _root);
        state = new BuildState();
        return new StepContext
        {
            Configuration = config,
            Toolchain = new Toolchain("", NullLogger.Instance),
            Logger = NullLogger.Instance,
            StepConfig = new Dictionary<string, string>(),
            State = state,
        };
    }

    private string GeneratedDir => Path.Combine(_root, "out", "default", "generated", "cs");

    [Fact]
    public async Task Transpile_GeneratesUnitPerInput_AndRegisters()
    {
        WriteProject();
        Write("src/a.cs", "// one");
        Write("src/b.cs", "// two");

        var ctx = MakeContext(out var state);
        var result = await new TranspileStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success, result.Message);

        // One .g.cpp registered per input as a compilable source.
        Assert.Equal(2, state.GeneratedSources.Count);
        Assert.All(state.GeneratedSources, s => Assert.Equal(SourceLanguage.Cpp, s.Language));
        Assert.All(state.GeneratedSources, s => Assert.EndsWith(".g.cpp", s.FullPath));

        // Generated dir is on the include path so the .g.h headers are found.
        Assert.Contains(GeneratedDir, state.ExtraIncludeDirs);

        // Headers + sources exist on disk.
        Assert.True(File.Exists(Path.Combine(GeneratedDir, "src_a.g.cpp")));
        Assert.True(File.Exists(Path.Combine(GeneratedDir, "src_a.g.h")));
        Assert.True(File.Exists(Path.Combine(GeneratedDir, "src_b.g.cpp")));
    }

    [Fact]
    public async Task Transpile_NoInputs_IsNoOp()
    {
        WriteProject();
        Write("src/main.cpp", "int main(){return 0;}");

        var ctx = MakeContext(out var state);
        var result = await new TranspileStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(state.GeneratedSources);
        Assert.Empty(state.ExtraIncludeDirs);
    }

    [Fact]
    public async Task Transpile_UnchangedOutputs_KeepTimestamp_OnRerun()
    {
        WriteProject();
        Write("src/a.cs", "// one");

        var ctx1 = MakeContext(out _);
        Assert.True((await new TranspileStep().ExecuteAsync(ctx1, CancellationToken.None)).Success);

        var genCpp = Path.Combine(GeneratedDir, "src_a.g.cpp");
        var firstWrite = File.GetLastWriteTimeUtc(genCpp);

        // Rerun: write-if-changed must leave the unchanged unit's mtime intact so
        // the downstream compile is skipped.
        var ctx2 = MakeContext(out var state2);
        Assert.True((await new TranspileStep().ExecuteAsync(ctx2, CancellationToken.None)).Success);

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(genCpp));
        // The full output set is still registered every build.
        Assert.Single(state2.GeneratedSources);
    }

    [Fact]
    public async Task Transpile_NewInput_AddsUnit()
    {
        WriteProject();
        Write("src/a.cs", "// one");

        Assert.True((await new TranspileStep().ExecuteAsync(MakeContext(out _), CancellationToken.None)).Success);

        // Add a second input; the next run must register both units.
        Write("src/b.cs", "// two");
        var result = await new TranspileStep().ExecuteAsync(MakeContext(out var state2), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, state2.GeneratedSources.Count);
        Assert.True(File.Exists(Path.Combine(GeneratedDir, "src_b.g.cpp")));
    }

    [Fact]
    public async Task Transpile_RespectsCustomInputsGlob()
    {
        WriteProject();
        Write("src/keep.cs", "// yes");
        Write("src/skip.txt", "no");

        var ctx = MakeContext(out var state);
        ctx.StepConfig["inputs"] = "**/keep.cs";

        Assert.True((await new TranspileStep().ExecuteAsync(ctx, CancellationToken.None)).Success);
        Assert.Single(state.GeneratedSources);
    }
}
