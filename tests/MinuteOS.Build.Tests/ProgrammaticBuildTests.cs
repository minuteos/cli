using Microsoft.Extensions.DependencyInjection;
using MinuteOS.Build;
using MinuteOS.Build.Graph;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Proves the build system is usable programmatically: register it via MEDI,
/// resolve <see cref="IBuildRunner"/>, and build a resolved configuration - no CLI.
/// </summary>
public class ProgrammaticBuildTests : IDisposable
{
    private readonly string _root;

    public ProgrammaticBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-prog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: prog\nconfigurations:\n  host:\n    target: host\n    components: []\n");
        File.WriteAllText(Path.Combine(_root, "src", "main.cpp"), "int main(){ return 0; }\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Build_ViaDI_ProducesTheImage()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMinuteosBuild()
            .BuildServiceProvider();

        var runner = provider.GetRequiredService<IBuildRunner>();

        var project = ProjectConfig.Load(_root);
        var config = BuildConfiguration.Create(project, "host", _root);

        var ok = await runner.BuildAsync(config, new BuildOptions { Quiet = true });

        Assert.True(ok);
        Assert.True(File.Exists(config.PrimaryOutput));
    }

    [Fact]
    public async Task CustomStep_RegisteredViaDI_Runs()
    {
        // A project that lists our custom step.
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: prog\nconfigurations:\n  host:\n    target: host\n    components: []\n" +
            "    steps:\n      - name: marker\n");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMinuteosBuild()
            .AddSingleton<IBuildStepFactory, MarkerStepFactory>()
            .BuildServiceProvider();

        var runner = provider.GetRequiredService<IBuildRunner>();
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "host", _root);

        Assert.True(await runner.BuildAsync(config, new BuildOptions { Quiet = true }));
        Assert.True(File.Exists(Path.ChangeExtension(config.PrimaryOutput, ".marker")));
    }

    private sealed class MarkerStepFactory : IBuildStepFactory
    {
        public string Name => "marker";
        public IGraphStep Create(IReadOnlyDictionary<string, string> config) => new MarkerStep();
    }

    private sealed class MarkerStep : IGraphStep
    {
        public string Name => "marker";
        public StepSignature Signature => StepSignature.Source(
            consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))]);

        public IEnumerable<BuildAction> Plan(PlanContext ctx)
        {
            var image = ctx.Inputs.FirstOrDefault();
            if (image is null) yield break;
            var outPath = Path.ChangeExtension(image.Id, ".marker");
            yield return new BuildAction("marker", [image], [Artifact.File(outPath, ("kind", "text"))], async actx =>
            {
                await File.WriteAllTextAsync(outPath, "ok", actx.CancellationToken);
                return ActionResult.Ok();
            });
        }
    }
}
