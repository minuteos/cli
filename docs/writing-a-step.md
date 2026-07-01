# Writing a build step

A build step is the unit of extension. It depends only on
**`MinuteOS.Build.Abstractions`** — not the implementations — so a step can live
in your own assembly.

A working example is [`examples/custom-step`](../examples/custom-step).

## The contract

```csharp
public interface IGraphStep
{
    string Name { get; }
    StepSignature Signature { get; }               // what it consumes / produces
    IEnumerable<BuildAction> Plan(PlanContext ctx); // expand into concrete work
}
```

- **`Signature`** declares the artifact *kinds* the step consumes (selectors) and
  produces (property templates). The engine uses this to wire edges and order the
  step — you never hard-code "run after link"; you declare "I consume the image."
- **`Plan`** runs once the step's inputs are materialized. `ctx.Inputs` is the
  set of artifacts matching your selectors. Return one `BuildAction` per unit of
  work.

```csharp
public sealed record BuildAction(
    string Label,
    IReadOnlyList<Artifact> Inputs,
    IReadOnlyList<Artifact> Outputs,
    Func<ActionContext, Task<ActionResult>> Run)
{
    public string? ConfigKey { get; init; } // config fingerprint (e.g. the command)
    public bool AlwaysRun { get; init; }     // for dynamic-output steps
}
```

The `ActionContext` gives you an `IToolchain` (program names + `RunToolAsync` /
`RunShellAsync`), an `ILogger`, the project root, and the cancellation token.

## Example: a post-link step

Consume the linked ELF (so the engine runs you after `gcc:link`) and write a
file beside it:

```csharp
using MinuteOS.Build.Graph;

sealed class BuildInfoStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "build-info";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var image = ctx.Inputs.FirstOrDefault();
        if (image is null) yield break;

        var outPath = Path.ChangeExtension(image.Id, ".build-info.txt");
        var note = config.GetValueOrDefault("note", "built by a custom step");

        yield return new BuildAction("build-info", [image], [Artifact.File(outPath, ("kind", "text"))], async actx =>
        {
            await File.WriteAllTextAsync(outPath, note, actx.CancellationToken);
            return ActionResult.Ok();
        })
        {
            ConfigKey = note, // re-run only when the note changes
        };
    }
}
```

## Incrementality — get it for free

- Set **`ConfigKey`** to a fingerprint of any non-file input (typically the exact
  command line). The engine skips the action when inputs + `ConfigKey` + outputs
  are unchanged.
- Report **discovered inputs** (e.g. a depfile) from `ActionResult` so the cache
  tracks dependencies the declaration didn't know about.
- For **dynamic outputs** (you don't know the produced set until you run — a
  transpiler), set `AlwaysRun = true` and return the produced artifacts from
  `ActionResult.Ok(produced)`. Downstream steps fan out over them lazily. Only
  rewrite outputs whose content changed, so downstream stays incremental.

## Producing sources for compilation

To generate C++ that the compiler picks up:

- produce `("kind","source"),("lang","cpp")` artifacts for the `.cpp`, and
- produce a `("kind","header-dir")` artifact for the directory holding generated
  headers — `gcc:compile` consumes `header-dir` and adds it to `-I`.

See the built-in `transpile`/`transform` steps.

## Registering a step

Implement `IBuildStepFactory` and register it in DI; the step name becomes usable
in any project's `steps:` list:

```csharp
sealed class BuildInfoStepFactory : IBuildStepFactory
{
    public string Name => "build-info";
    public IGraphStep Create(IReadOnlyDictionary<string, string> config) => new BuildInfoStep(config);
}

services.AddMinuteosBuild()
        .AddSingleton<IBuildStepFactory, BuildInfoStepFactory>();
```

```yaml
# a project that uses it
steps:
  - name: build-info
    config: { note: "release build" }
```

Consumer-registered factories override the built-ins of the same name, so you can
also *replace* a step (e.g. swap the stub `transpile` for a real one).
