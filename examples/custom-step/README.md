# custom-step

Drives the build system **as a library** and adds a **custom build step** — no
CLI. Demonstrates the extension contract (`IGraphStep` + `IBuildStepFactory`),
which lives in `MinuteOS.Build.Abstractions`.

```bash
dotnet run -- project        # builds ./project with the custom "build-info" step
```

`Program.cs`:
- registers the build system (`AddMinuteosBuild()`),
- registers a custom `IBuildStepFactory` for the step name `build-info`,
- resolves `IBuildRunner` and builds a configuration.

`project/minuteos.yaml` uses the step:

```yaml
steps:
  - name: build-info
    config: { note: "hello from a custom step" }
```

The step consumes the linked ELF (`kind=image, format=elf`) — so the graph runs
it after `gcc:link` automatically — and writes `out/host/app.build-info.txt`
beside the image. It's cache-gated on its `ConfigKey`, so it only re-runs when
the note changes.
