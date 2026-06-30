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

`Sources` is **mutable**: a step earlier in the pipeline can add to it. This is
the mechanism for source generation (next section).

## Source generation & transforms (first-class)

> **Status: implemented** (`transform` step in `minuteos/Build/Steps/TransformStep.cs`,
> `BuildState.ExtraIncludeDirs`, covered by `minuteos.tests/TransformStepTests.cs`).
> It works today on the current phase-based runner as a `PreBuild` step and
> carries forward unchanged onto the pipeline model below — a transform is just an
> early step that mutates the source set.

Generating sources is a core requirement, not an add-on — e.g. a C#→C++
transpiler. A **transform step** runs before `gcc:compile`, consumes input files
of some kind, emits generated sources, and feeds them into the rest of the
pipeline:

```yaml
pipeline:
  - cs:transpile      # *.cs  ->  generated *.cpp / *.h
  - gcc:compile       # now sees the generated *.cpp
  - gcc:link
```

A transform step:

1. **Discovers its inputs** from the build's source directories (the context
   exposes `SourceDirs`), by extension/glob it owns (e.g. `*.cs`). The core's
   discovery of `Sources` stays compiler-input-only (`.c/.cpp/.S`); other input
   kinds belong to the step that handles them.
2. **Generates** into a dedicated dir under the output tree
   (`context.GeneratedDir`), invoking its tool.
3. **Registers outputs**: adds the generated `*.cpp` to the `Sources` slot and
   the generated include dir to the `include-dirs` setting, so downstream steps
   pick them up with no special casing.
4. **Is incremental**: regenerates only when an input is newer than its output;
   the generated `.cpp` then flows through the normal `.d`-based compile caching.

### How the transpiler plugs in

The transpiler is a separate program (`minuteos/cs-transpiler`). It plugs in as a
`transform` step that invokes it as an external tool with a small I/O contract
(inputs in, output dir out, a manifest of produced files back) — the same loose
coupling as the `shell` step, just with output registration. Wiring it into a
project is one step entry today:

```yaml
# a config (or component / target) that uses the transpiler
steps:
  - name: transform
    phase: PreBuild        # becomes an early pipeline slot under the pipeline model
    config:
      id: cs               # names the generated subdir: out/<cfg>/generated/cs
      inputs: "**/*.cs"    # glob(s), matched across every source dir
      command: cs-transpiler {inputs} -o {generated-dir} --manifest {manifest}
```

**The I/O contract the tool must honor:**
- It receives the matched input files (`{inputs}`), an output directory
  (`{generated-dir}`), and a manifest path (`{manifest}`) — placeholders the step
  substitutes; arrange them however the tool's CLI wants.
- It writes its produced files into the output directory and lists them in the
  manifest: a JSON array of paths, or `{"outputs": [...]}`; paths may be absolute
  or relative to the output dir.
- The step then registers any `.c/.cpp/.cc/.cxx/.S` from the manifest as sources
  to compile and puts the output dir (plus any dir containing a generated header)
  on the include path. Generated `.cpp` then flows through the normal `.d`-based
  incremental compile cache.
- **Incrementality:** the step re-invokes the tool only when an input is newer
  than the last manifest, so a no-op build skips the tool entirely. (Whole-project
  codegen is supported — the manifest is the source of truth for outputs — but a
  predictable per-file mapping gives the tightest rebuilds.)

(An in-process plugin-step variant — loading the transpiler as an assembly — is
possible later for speed, but the external-tool contract is the primary, simplest
path and keeps the builder language-agnostic.)

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

- [x] 1. `Settings` aggregation (additive; typed fields become views over it)
- [ ] 2. `BuildContext` with `Sources`/`Objects`/`Image`/`Settings` slots
- [ ] 3. `gcc:compile` / `gcc:link` steps; built-in default pipeline
- [ ] 4. objcopy/disassembly/size as pipeline steps
- [ ] 5. `run`/`qemu`/`renode` steps; retire top-level `test-runner`
- [ ] 6. drop typed gcc fields from the schema; `migrate` emits `settings`+`pipeline`
- [x] **Source generation / transforms** — `transform` step, mutable source set,
  generated-header include dirs, manifest contract, incremental (delivered ahead
  of the pipeline refactor since the transpiler needs it now)
