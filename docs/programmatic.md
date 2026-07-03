# Using the library programmatically

The build system is `MinuteOS.Build` (implementations) over
`MinuteOS.Build.Abstractions` (contracts). Reference it and drive builds without
the CLI. A runnable example is [`examples/custom-step`](../examples/custom-step).

## Register and build

```csharp
using Microsoft.Extensions.DependencyInjection;
using MinuteOS.Build;

var provider = new ServiceCollection()
    .AddLogging()                 // provides ILogger to the runner
    .AddMinuteosBuild()           // registers IBuildRunner
    .BuildServiceProvider();

var runner = provider.GetRequiredService<IBuildRunner>();

// Resolve a configuration from a project on disk...
var project = ProjectConfig.Load(projectRoot);
var config  = BuildConfiguration.Create(project, "host", projectRoot);

// ...and build it.
var ok = await runner.BuildAsync(config, new BuildOptions
{
    Parallelism = Environment.ProcessorCount,
    // Quiet = true,
    // Fingerprinter = new ContentHashFingerprinter(),
});
```

`BuildConfiguration.Create` does the module-dependency resolution (components,
target inheritance, settings merge). The returned object implements
`IBuildConfiguration` — the read-only surface steps see.

## The contracts

Everything a consumer or extension needs is in `MinuteOS.Build.Abstractions`:

| Type | Role |
|------|------|
| `IBuildRunner` / `BuildOptions` | drive a build |
| `IBuildConfiguration` | the resolved config a step reads |
| `IToolchain` / `CompilationResult` | program names + process execution |
| `Settings` | the opaque settings bag |
| `IGraphStep`, `StepSignature`, `BuildAction`, `ActionResult` | the step contract |
| `Artifact`, `Selector`, `Cardinality` | the artifact model |
| `IFingerprinter` | cache fingerprint strategy |
| `IBuildStepFactory` | register a custom step |

## Adding custom steps

Register `IBuildStepFactory` implementations alongside `AddMinuteosBuild()`; they
become available (and can override built-ins) to any configuration that lists the
step. See [writing a build step](writing-a-step.md).

```csharp
services.AddMinuteosBuild()
        .AddSingleton<IBuildStepFactory, MyStepFactory>();
```

## Notes

- Logging flows through `Microsoft.Extensions.Logging`; `-vv`-style debug logs
  (every toolchain command line) appear at `LogLevel.Debug`.
- The runner constructs the gcc toolchain from `gcc.toolchain-prefix` in the
  configuration's settings.
- Custom steps run for the top-level build; nested `sub-build` graphs currently
  use the built-in catalog only.

## Native AOT

The CLI publishes as a self-contained **NativeAOT** binary — a single native
executable with no .NET runtime dependency and fast cold start (useful for
`minuteos dap`, which an editor spawns per debug session):

```bash
dotnet publish src/MinuteOS.Cli -r linux-x64 -c Release -p:PublishAot=true
# -> bin/Release/net10.0/linux-x64/publish/minuteos  (~15 MB, self-contained)
```

This works because every layer is reflection-free: the command framework
(`triaxis.CommandLine`) registers commands via a compile-time source generator,
config loading uses YamlDotNet's **static** (de)serialization
(`Vecc.YamlDotNet.Analyzers.StaticGenerator` + `YamlContext`; the `object`- and
`Dictionary`-valued maps are handled by `SettingsMapConverter`/
`StringMapConverter` without reflection), and the JSON that isn't already built
on the `JsonNode` DOM goes through source-gen `JsonSerializerContext`s. AOT and
`PackAsTool` are mutually exclusive, so a native publish suppresses the tool
packaging automatically; the default `dotnet pack` still produces the global
tool.
