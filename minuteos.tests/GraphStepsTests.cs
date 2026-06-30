using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Graph;
using MinuteOS.Cli.Build.Graph.Steps;

namespace MinuteOS.Cli.Tests;

/// <summary>
/// Unit coverage for graph steps with dynamic outputs (the in-process transpiler):
/// it consumes cs-source artifacts and reports generated cpp-source + header-dir
/// via ActionResult.ProducedArtifacts.
/// </summary>
public class GraphStepsTests : IDisposable
{
    private readonly string _root;

    public GraphStepsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-graphstep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  c:\n    target: host\n    components: []\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private PlanContext Ctx(IReadOnlyList<Artifact> inputs)
    {
        var config = BuildConfiguration.Create(ProjectConfig.Load(_root), "c", _root);
        return new PlanContext
        {
            Config = config,
            Settings = config.Settings,
            Toolchain = new Toolchain("", NullLogger.Instance),
            Logger = NullLogger.Instance,
            Inputs = inputs,
        };
    }

    private ActionContext ActionCtx() => new()
    {
        Toolchain = new Toolchain("", NullLogger.Instance),
        Logger = NullLogger.Instance,
        ProjectRoot = _root,
        Quiet = true,
    };

    [Fact]
    public async Task Transpile_ProducesGeneratedCppAndHeaderDir()
    {
        var cs = Artifact.File(Path.Combine(_root, "src", "a.cs"), ("kind", "source"), ("lang", "cs"));

        var actions = new TranspileStep().Plan(Ctx([cs])).ToList();
        var action = Assert.Single(actions);
        Assert.True(action.AlwaysRun); // whole-program; dynamic outputs

        var result = await action.Run(ActionCtx());
        Assert.True(result.Success);
        Assert.NotNull(result.ProducedArtifacts);

        var produced = result.ProducedArtifacts!;
        Assert.Contains(produced, a => a.Kind == "source" && a.Properties.GetValueOrDefault("lang") == "cpp"
            && a.Id.EndsWith(".g.cpp"));
        Assert.Contains(produced, a => a.Kind == "header-dir");
    }

    [Fact]
    public void Transpile_NoInputs_PlansNothing()
    {
        Assert.Empty(new TranspileStep().Plan(Ctx([])));
    }

    [Fact]
    public void GitVersion_IsAnAugmenter()
    {
        // Produces kind=settings, so the engine runs it before readers.
        var sig = new GitVersionStep(new Dictionary<string, string>()).Signature;
        Assert.Contains(sig.Produces, p => p.GetValueOrDefault("kind") == "settings");
    }
}
