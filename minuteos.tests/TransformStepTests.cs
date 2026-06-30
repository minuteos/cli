using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Tests;

/// <summary>
/// Exercises the source-generation / transform step end to end with a stub
/// "transpiler" (a shell one-liner) so the test needs no real toolchain. Verifies
/// input discovery, output registration into the build, and incremental re-runs.
/// </summary>
public class TransformStepTests : IDisposable
{
    private readonly string _root;

    public TransformStepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-transform-{Guid.NewGuid():N}");
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

    /// <summary>
    /// A stub transpiler implemented in /bin/sh: for each input it writes a
    /// .cpp + .h into the generated dir and emits a JSON manifest. Mirrors the
    /// contract the real cs-transpiler will honor.
    /// </summary>
    private const string StubTranspiler =
        "mkdir -p {generated-dir}; out={generated-dir}/gen.cpp; hdr={generated-dir}/gen.h; " +
        "echo '#pragma once' > $hdr; echo 'int generated_value();' >> $hdr; " +
        "echo '#include \"gen.h\"' > $out; echo 'int generated_value() { return 42; }' >> $out; " +
        "printf '[\"%s\",\"%s\"]' $out $hdr > {manifest}";

    private StepContext MakeContext(out BuildState state)
    {
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "default", _root);
        state = new BuildState();
        return new StepContext
        {
            Configuration = config,
            Toolchain = new Toolchain("", NullLogger.Instance),
            Logger = NullLogger.Instance,
            StepConfig = new Dictionary<string, string>
            {
                ["id"] = "cs",
                ["inputs"] = "**/*.cs",
                ["command"] = StubTranspiler,
            },
            State = state,
        };
    }

    private void WriteMinimalProject()
    {
        Write("minuteos.yaml",
            "name: demo\n" +
            "configurations:\n" +
            "  default:\n" +
            "    target: host\n" +
            "    components: []\n");
        Write("src/main.cs", "// a C# source the transpiler will translate\n");
    }

    [Fact]
    public async Task Transform_GeneratesSourcesAndRegistersThem()
    {
        WriteMinimalProject();
        var ctx = MakeContext(out var state);

        var result = await new TransformStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success, result.Message);

        // The generated .cpp is registered as a source to compile.
        Assert.Single(state.GeneratedSources);
        Assert.EndsWith("gen.cpp", state.GeneratedSources[0].FullPath);
        Assert.Equal(SourceLanguage.Cpp, state.GeneratedSources[0].Language);

        // The generated dir is on the include path so the .h is found.
        var generatedDir = Path.Combine(ctx.Configuration.OutputRoot, "generated", "cs");
        Assert.Contains(generatedDir, state.ExtraIncludeDirs);

        // The files actually exist on disk.
        Assert.True(File.Exists(Path.Combine(generatedDir, "gen.cpp")));
        Assert.True(File.Exists(Path.Combine(generatedDir, "gen.h")));
    }

    [Fact]
    public async Task Transform_NoInputs_IsNoOp()
    {
        // Project with no .cs files at all.
        Write("minuteos.yaml",
            "name: demo\nconfigurations:\n  default:\n    target: host\n    components: []\n");
        Write("src/main.cpp", "int main() { return 0; }\n");

        var ctx = MakeContext(out var state);
        var result = await new TransformStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(state.GeneratedSources);
        Assert.Empty(state.ExtraIncludeDirs);
    }

    [Fact]
    public async Task Transform_IsIncremental_SkipsWhenUpToDate()
    {
        WriteMinimalProject();

        // First run generates and records a manifest.
        var ctx1 = MakeContext(out _);
        Assert.True((await new TransformStep().ExecuteAsync(ctx1, CancellationToken.None)).Success);

        var generatedDir = Path.Combine(ctx1.Configuration.OutputRoot, "generated", "cs");
        var genCpp = Path.Combine(generatedDir, "gen.cpp");
        var firstWrite = File.GetLastWriteTimeUtc(genCpp);

        // Second run with no input changes must NOT regenerate (file untouched),
        // yet must still register the outputs from the existing manifest.
        var ctx2 = MakeContext(out var state2);
        Assert.True((await new TransformStep().ExecuteAsync(ctx2, CancellationToken.None)).Success);

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(genCpp));
        Assert.Single(state2.GeneratedSources);
    }

    [Fact]
    public async Task Transform_Regenerates_WhenInputChanges()
    {
        WriteMinimalProject();

        var ctx1 = MakeContext(out _);
        Assert.True((await new TransformStep().ExecuteAsync(ctx1, CancellationToken.None)).Success);

        var generatedDir = Path.Combine(ctx1.Configuration.OutputRoot, "generated", "cs");
        var manifest = Path.Combine(generatedDir, ".manifest.json");
        var firstManifestTime = File.GetLastWriteTimeUtc(manifest);

        // Touch the input to a later timestamp than the manifest.
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "src/main.cs"), firstManifestTime.AddSeconds(5));

        var ctx2 = MakeContext(out _);
        Assert.True((await new TransformStep().ExecuteAsync(ctx2, CancellationToken.None)).Success);

        Assert.True(File.GetLastWriteTimeUtc(manifest) > firstManifestTime,
            "manifest should have been rewritten after the input changed");
    }

    [Fact]
    public async Task Transform_FailsWithoutCommand()
    {
        WriteMinimalProject();
        var ctx = MakeContext(out _);
        ctx.StepConfig.Remove("command");

        var result = await new TransformStep().ExecuteAsync(ctx, CancellationToken.None);
        Assert.False(result.Success);
    }
}
