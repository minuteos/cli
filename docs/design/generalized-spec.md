# Toward a toolchain-agnostic project specification

Status: **proposal / discussion**

## Motivation

The current schema bakes in the GCC toolchain. Targets carry first-class
properties like `toolchain-prefix`, `arch-flags`, `c-flags`, `cxx-flags`,
`link-flags`, `ld-script`, `primary-ext`; the compile/link logic is hardcoded in
`BuildRunner`/`Toolchain`; and `test-runner` is a special top-level field. That
makes alternate toolchains (clang, IAR, a future Rust path, an SDK's own
compiler) second-class, and singles out test execution as special when it is
really just "run a thing for this target."

Two principles drive the redesign:

1. **The core knows no toolchain.** GCC is a *bundle of build steps* that read
   generic settings. Swapping toolchains means swapping steps, not changing the
   schema or the core.
2. **There are no special phases.** Compiling, linking, post-processing, and
   *running tests* are all steps in an ordered pipeline. Test execution is a
   target-overridable step like any other.

## Core model

The core understands only structure, not tools:

- **Component** — a unit of sources + metadata: `requires` (deps), `settings`
  (an opaque key→value(s) bag contributed to the build), `steps` (steps it adds).
- **Target** — a build target with inheritance (`requires`), its own `settings`
  and `steps`, and a **`pipeline`**: the ordered list of steps to run. Targets
  inherit and override the pipeline (and individual steps by name).
- **Configuration** (project `minuteos.yaml`) — selects a target, names a set of
  components, and may override settings/steps.

Everything the old typed fields expressed becomes a **setting**. The core
aggregates settings with the precedence already implemented (target chain
most-specific-wins, components accumulate, the profile overrides). The core never
interprets a setting; the steps do.

```yaml
# a target, generalized
requires: [cmsis]
settings:
  toolchain-prefix: arm-none-eabi-
  arch-flags: [-mcpu=cortex-m3, -mthumb]
  link-flags: [-nostartfiles, -specs=nano.specs]
  ld-script: default.ld
  primary-ext: .axf
  defines: ['LINKER_ORDERED_SECTION=".text.ord"']
pipeline:
  - gcc:compile
  - gcc:link
  - gcc:objcopy        # bin/hex/srec, configured by settings
  - run                # default run/test step (host: exec directly)
```

## The build context (how steps compose)

Steps no longer run in fixed `PreBuild/PreLink/PostBuild` phases — their order
*is* the pipeline. They communicate through a `BuildContext` with a few universal
artifact slots plus a generic bag:

| Slot | Produced by | Consumed by |
|------|-------------|-------------|
| `Sources` | core (discovery) + steps (generated) | compile steps |
| `Objects` | compile steps | link steps |
| `Image` | link step | objcopy / run / sub-build |
| `Settings` | core (aggregated) | every step |
| `Outputs` (bag) | any step | any step / the user |

So `gcc:compile` reads `Sources` + `Settings`, writes `Objects`; `gcc:link` reads
`Objects` + `Settings`, writes `Image`; `run`/`qemu` reads `Image`. The slots
(sources → objects → image) are the universal artifacts of any compiled-language
build, independent of which tool fills them.

## GCC as steps

The current `Toolchain` class becomes a family of steps registered under a `gcc:`
prefix:

- `gcc:compile` — each source → object, reading `defines`, `include-dirs`,
  `arch-flags`, `c-flags`/`cxx-flags`, optimization. Honors the PCH.
- `gcc:link` — objects → `Image`, reading `arch-flags`, `link-flags`,
  `ld-script`, link search dirs.
- `gcc:objcopy` — `Image` → bin/hex/srec (replaces the migrated shell steps and
  the `binary-output` step).

A clang or vendor toolchain ships its own `clang:compile`/`clang:link`, and a
target just lists those in its `pipeline`. The default pipeline (so trivial host
projects need nothing) is a built-in `gcc` pipeline that any target can replace.

## Test/run as a step

`test-runner` disappears as a top-level field. Instead:

- The default `run` step executes `Image` directly (host).
- A target overrides `run` to launch an emulator:

  ```yaml
  # qemu-arm target
  pipeline-overrides:
    run:
      name: qemu
      config:
        machine: lm3s6965evb
        args: [-nographic, -semihosting, -kernel, "{image}"]
  ```

- `minuteos run` builds the pipeline through `Image`, then invokes the `run` step.
- `minuteos test` builds each suite's `Image`, then invokes the same `run` step
  and parses its output. (The qemu/renode launch + result parsing become the
  `qemu`/`renode` step's job.)

## Before / after

```yaml
# BEFORE (today)                          # AFTER (proposed)
toolchain-prefix: arm-none-eabi-          settings:
arch-flags: [-mcpu=cortex-m3, -mthumb]      toolchain-prefix: arm-none-eabi-
ld-script: default.ld                       arch-flags: [-mcpu=cortex-m3, -mthumb]
primary-ext: .axf                           ld-script: default.ld
test-runner:                                primary-ext: .axf
  command: qemu-system-arm                pipeline:
  args: [..., -kernel, "{binary}"]          - gcc:compile
                                            - gcc:link
                                            - gcc:objcopy
                                          pipeline-overrides:
                                            run: { name: qemu, config: { ... } }
```

## Incremental implementation plan

Each step keeps the tool green (host 67/67, ARM 68/68) and is independently
verifiable:

1. **Aggregate generic settings** alongside the current typed fields (populated
   from the same metadata). No behavior change.
2. **Introduce `BuildContext`** with `Sources`/`Objects`/`Image`/`Settings` slots.
3. **Convert compile + link** from hardcoded `BuildRunner` phases into
   `gcc:compile` / `gcc:link` steps reading settings. Default pipeline =
   `[gcc:compile, gcc:link, gcc:objcopy]` → identical output.
4. **Move objcopy/disassembly/size** into pipeline steps.
5. **Replace `test-runner`** with a `run`/`qemu`/`renode` step; wire `run`/`test`
   to invoke the pipeline's `run` step. Targets override it.
6. **Drop the typed gcc fields** from the schema (keep `MakeImport` emitting
   settings); update `migrate` to emit `settings:` + `pipeline`.

## Decisions

1. **Setting names — hybrid.** Near-universal C concepts are flat (`defines`,
   `include-dirs`); genuinely toolchain-specific settings are namespaced by the
   step family that reads them (`gcc.arch-flags`, `gcc.toolchain-prefix`,
   `gcc.c-flags`, `gcc.cxx-flags`, `gcc.link-flags`, `gcc.ld-script`,
   `gcc.link-dirs`, `gcc.primary-ext`).
2. **Default pipeline — built-in, fully overridable.** The tool ships a default
   `[gcc:compile, gcc:link, gcc:objcopy, run]` pipeline so trivial projects need
   no declaration; any target may replace it or override individual steps.
3. **Pipeline overrides — per-step by name *and* whole-pipeline replace.** A
   target overrides one step by name (`run` -> `qemu`) leaving the rest
   inherited, or replaces the whole `pipeline` for a radically different flow.
4. **Structure stays typed.** `requires`/`components`/`source-dir`/`steps`/
   `pipeline` stay first-class (they are structural). Only compiler/linker
   specifics move into `settings`.
5. **Step IO contract — fixed slots first.** `Sources`/`Objects`/`Image` plus a
   generic `Outputs` bag; revisit a fuller graph only if a real need appears.

## Implementation status

- [ ] 1. `Settings` aggregation (additive; typed fields become views over it)
- [ ] 2. `BuildContext` with `Sources`/`Objects`/`Image`/`Settings` slots
- [ ] 3. `gcc:compile` / `gcc:link` steps; built-in default pipeline
- [ ] 4. objcopy/disassembly/size as pipeline steps
- [ ] 5. `run`/`qemu`/`renode` steps; retire top-level `test-runner`
- [ ] 6. drop typed gcc fields from the schema; `migrate` emits `settings`+`pipeline`
