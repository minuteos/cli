# minuteOS build system — documentation

minuteOS builds embedded C/C++ projects (and generated sources) with a
declarative, toolchain-agnostic **task-graph** engine. `minuteos` is a thin CLI
over the `MinuteOS.Build` library, so the same engine is usable programmatically.

## Guides

- [Getting started](getting-started.md) — install, a first project, build/run/test.
- [Configuration reference](configuration.md) — `minuteos.yaml`, `component.yaml`,
  `target.yaml`, and the settings bag.
- [External dependencies](dependencies.md) — lib roots, git submodules, and
  `minuteos restore`.
- [The build model](build-model.md) — the task graph, **how dependencies work**,
  incremental builds, and the built-in step catalog.
- [Device management](device.md) — flash, erase, and debug via target-declared
  probe commands.
- [Writing a build step](writing-a-step.md) — the extension contract and how to
  register a custom step.
- [Using the library programmatically](programmatic.md) — drive builds via DI.

## Examples

Runnable projects under [`../examples`](../examples):

- [`hello-host`](../examples/hello-host) — the smallest project.
- [`transpiler`](../examples/transpiler) — source generation (`*.cs` → C++).
- [`custom-step`](../examples/custom-step) — a custom step via the library + DI.
- [`cortex-m3-qemu`](../examples/cortex-m3-qemu) — an ARM target that runs tests
  under QEMU/Renode.

## Design notes

Background and rationale (how the current model was reached):

- [`design/task-graph.md`](design/task-graph.md) — the opaque-artifact task graph.
- [`design/debugger-integration.md`](design/debugger-integration.md) — the plan
  for sharing one device/debug implementation with the minute-debug extension.
- [`design/generalized-spec.md`](design/generalized-spec.md) — the settings/steps
  groundwork it builds on.
