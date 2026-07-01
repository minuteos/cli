using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;
using MinuteOS.Build.Graph;

// Drives the minuteOS build system programmatically, with a custom build step
// registered through DI. Usage: dotnet run -- <project-root>
var projectRoot = Path.GetFullPath(args.FirstOrDefault() ?? "project");

var provider = new ServiceCollection()
    .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information))
    // Register the build system...
    .AddMinuteosBuild()
    // ...and our custom step. It becomes available to any project that lists it
    // under `steps:`.
    .AddSingleton<IBuildStepFactory, BuildInfoStepFactory>()
    .BuildServiceProvider();

var runner = provider.GetRequiredService<IBuildRunner>();

var project = ProjectConfig.Load(projectRoot);
var config = BuildConfiguration.Create(project, "host", projectRoot);

var ok = await runner.BuildAsync(config);
Console.WriteLine(ok ? $"OK: {config.PrimaryOutput}" : "build failed");
return ok ? 0 : 1;

// --- A custom step ---------------------------------------------------------
// Consumes the linked image and writes a build-info file next to it. It depends
// only on MinuteOS.Build.Abstractions.

sealed class BuildInfoStepFactory : IBuildStepFactory
{
    public string Name => "build-info";
    public IGraphStep Create(IReadOnlyDictionary<string, string> config) => new BuildInfoStep(config);
}

sealed class BuildInfoStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "build-info";

    // Runs after link (it consumes the ELF image); produces a text artifact.
    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var image = ctx.Inputs.FirstOrDefault();
        if (image is null)
            yield break;

        var outPath = Path.ChangeExtension(image.Id, ".build-info.txt");
        var note = config.GetValueOrDefault("note", "built by a custom step");

        yield return new BuildAction("build-info", [image], [Artifact.File(outPath, ("kind", "text"))], async actx =>
        {
            await File.WriteAllTextAsync(outPath, $"{note}\nimage: {image.Id}\n", actx.CancellationToken);
            actx.Logger.LogInformation("build-info: wrote {File}", outPath);
            return ActionResult.Ok();
        })
        {
            ConfigKey = note, // rebuild the info file if the note changes
        };
    }
}
