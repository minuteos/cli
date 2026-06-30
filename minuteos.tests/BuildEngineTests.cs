using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Graph;

namespace MinuteOS.Cli.Tests;

/// <summary>
/// Exercises the task-graph engine in isolation: artifact/selector matching,
/// property-based wiring + topological ordering, and lazy fan-out (a consumer
/// planning over the artifacts a producer emitted).
/// </summary>
public class BuildEngineTests
{
    [Fact]
    public void Artifact_Matches_RequiresAllSelectorPairs()
    {
        var a = Artifact.File("/x/foo.cpp", ("kind", "source"), ("lang", "cpp"));

        Assert.True(a.Matches(Selector.Of(("kind", "source"))));
        Assert.True(a.Matches(Selector.Of(("kind", "source"), ("lang", "cpp"))));
        Assert.False(a.Matches(Selector.Of(("kind", "source"), ("lang", "c"))));
        Assert.False(a.Matches(Selector.Of(("kind", "object"))));
    }

    /// <summary>A → B → C declared out of order; the engine must run them A,B,C.</summary>
    [Fact]
    public async Task Engine_OrdersByProperties_AndFansOutLazily()
    {
        var order = new List<string>();

        // C: object -> image
        var c = new FakeStep("link",
            consumes: [Selector.Of(("kind", "object"))],
            produces: [[("kind", "image")]],
            plan: ctx =>
            {
                order.Add("link");
                // Lazy fan-out: sees the objects B produced.
                Assert.Equal(2, ctx.Inputs.Count);
                return [Action("img", ctx.Inputs, [Artifact.File("/out/app", ("kind", "image"))])];
            });

        // B: source -> object (one per source)
        var b = new FakeStep("compile",
            consumes: [Selector.Of(("kind", "source"))],
            produces: [[("kind", "object")]],
            plan: ctx =>
            {
                order.Add("compile");
                return ctx.Inputs.Select((s, i) =>
                    Action($"o{i}", [s], [Artifact.File($"/out/{i}.o", ("kind", "object"))])).ToList();
            });

        // A: produces two sources
        var a = new FakeStep("scan",
            consumes: [],
            produces: [[("kind", "source")]],
            plan: _ =>
            {
                order.Add("scan");
                return [Action("scan",
                    [],
                    [Artifact.File("/src/a.cpp", ("kind", "source")), Artifact.File("/src/b.cpp", ("kind", "source"))])];
            });

        var engine = new BuildEngine(new Toolchain("", NullLogger.Instance), NullLogger.Instance);
        // Pass deliberately out of dependency order.
        var ok = await engine.RunAsync([c, b, a], FakeConfig(), CancellationToken.None, quiet: true);

        Assert.True(ok);
        Assert.Equal(["scan", "compile", "link"], order);
    }

    [Fact]
    public async Task Engine_PropagatesActionFailure()
    {
        var step = new FakeStep("boom",
            consumes: [],
            produces: [[("kind", "image")]],
            plan: _ => [new BuildAction("fail", [], [], _ => Task.FromResult(ActionResult.Fail("nope")))]);

        var engine = new BuildEngine(new Toolchain("", NullLogger.Instance), NullLogger.Instance);
        Assert.False(await engine.RunAsync([step], FakeConfig(), CancellationToken.None, quiet: true));
    }

    private static BuildAction Action(string label, IReadOnlyList<Artifact> ins, IReadOnlyList<Artifact> outs) =>
        new(label, ins, outs, _ => Task.FromResult(ActionResult.Ok())) { IsUpToDate = () => true };

    private static BuildConfiguration FakeConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"minuteos-engine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "minuteos.yaml"),
            "name: d\nconfigurations:\n  c:\n    target: host\n    components: []\n");
        return BuildConfiguration.Create(ProjectConfig.Load(root), "c", root);
    }

    private sealed class FakeStep(
        string name,
        IReadOnlyList<Selector> consumes,
        IReadOnlyList<(string, string)[]> produces,
        Func<PlanContext, IEnumerable<BuildAction>> plan) : IGraphStep
    {
        public string Name => name;
        public StepSignature Signature => StepSignature.Source(consumes, produces.ToArray());
        public IEnumerable<BuildAction> Plan(PlanContext context) => plan(context);
    }
}
