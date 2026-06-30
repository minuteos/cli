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

> **Status: implemented.** Two flavors share one machinery
> (`minuteos/Build/Steps/TransformSupport.cs`, `BuildState.ExtraIncludeDirs`):
> the in-process `transpile` step (`TranspileStep` / `InProcessTransformStep`,
> **the transpiler's integration point**, stubbed for now) and the generic
> external-tool `transform` step (`TransformStep`, a language-agnostic escape
> hatch). Covered by `TranspileStepTests` + `TransformStepTests`. Both run today
> on the phase-based runner as `PreBuild` steps and carry forward unchanged onto
> the pipeline model below — a transform is just an early step that mutates the
> source set.

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

Any transform step:

1. **Discovers its inputs** from the build's source directories (the context
   exposes `SourceDirs`), by extension/glob it owns (e.g. `*.cs`). The core's
   discovery of `Sources` stays compiler-input-only (`.c/.cpp/.S`); other input
   kinds belong to the step that handles them.
2. **Generates** into a dedicated dir under the output tree
   (`out/<cfg>/generated/<id>`).
3. **Registers outputs**: adds the generated `*.cpp` to the `Sources` slot and
   the generated include dir to the `include-dirs` setting, so downstream steps
   pick them up with no special casing.
4. **Is incremental**: the generated `.cpp` flows through the normal `.d`-based
   compile cache; the generator only rewrites outputs whose content changed, so a
   no-op build recompiles nothing.

### How the transpiler plugs in (in-process build step)

The transpiler runs **inside the builder as a build step**, not as an external
CLI. It is a .NET `IBuildStep` (`TranspileStep : InProcessTransformStep`); the
real translation logic will replace the stub's `GenerateAsync`. Wiring it into a
project is one step entry — no command, no manifest plumbing:

```yaml
# a config (or component / target) that uses the transpiler
steps:
  - name: transpile
    phase: PreBuild        # becomes an early pipeline slot under the pipeline model
    config:
      id: cs               # names the generated subdir: out/<cfg>/generated/cs (default)
      inputs: "**/*.cs"    # overridable; default is **/*.cs
```

**Contract between the builder and the in-process transpiler:**
- The step hands the transpiler the **complete** set of discovered inputs every
  build — a whole-program transpiler needs all files for cross-unit context.
- **Dependency analysis is the transpiler's job**, not the builder's. There is no
  input-mtime gate in the step; the transpiler decides what changed and reports
  back its **complete** current output set. To keep the downstream compile cache
  effective it should rewrite only the outputs whose content changed (the stub's
  `WriteIfChanged` helper does exactly this), leaving unchanged units' timestamps
  intact.
- The base registers any returned `.c/.cpp/.cc/.cxx/.S` as sources to compile and
  puts the generated dir (plus any dir containing a generated header) on the
  include path.

The generic external-tool **`transform`** step remains as a language-agnostic
escape hatch for non-.NET generators (it keeps the inputs/output-dir/manifest CLI
contract and an input-mtime incremental gate); the in-process `transpile` step is
the preferred, tighter integration for the C#→C++ transpiler.

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

> **Status: implemented.** Running the image is a target-overridable `Run`-phase
> step (`run`/`qemu`/`renode`, `RunStep` + `RunSpecResolver` + `RunSpec`); the
> `Run` phase is excluded from the build pipeline and resolved out-of-band by the
> run/test commands. The legacy top-level `test-runner` field is kept as a
> deprecated fallback until step 6. Covered by `RunSpecResolverTests`; validated
> on the cortex-m3/qemu example.

`test-runner` is no longer special: launching the image is an ordinary,
target-overridable step.

- With no run step the image executes directly (host).
- A target overrides it to launch an emulator. The step carries a `command`
  (the `qemu`/`renode` step names default it to `qemu-system-arm`/`renode`) and a
  shell-style `args` string whose tokens honor double quotes; `{image}`/`{binary}`
  and `{filter}` are substituted:

  ```yaml
  # cortex-m3 target.yaml
  steps:
    - name: qemu
      phase: Run
      config:
        args: '-machine lm3s6965evb -nographic -semihosting -kernel "{image}" -append "{filter}"'
        timeout: "30"
  ```

- `minuteos run` builds through `Image`, resolves the run step, and launches it
  with live stdio.
- `minuteos test` builds each suite's `Image`, resolves the same run step, and
  runs it captured with a timeout, then parses the output.

(A future enhancement: let the `qemu`/`renode` steps own execution + result
parsing directly, and give step config first-class list values so `args` need not
be a shell string. The current launch-spec resolution keeps the change small and
the validated capture/parse path intact.)

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
- [x] 2. Pipeline artifact slots — `Sources`/`Objects`/`Image` on `BuildState`,
  `Settings` on the config; `StepContext` gains `Parallelism`/`Quiet`
- [x] 3. `GccCompileStep` (incl. PCH) / `GccLinkStep` read the `Settings` bag;
  `BuildRunner` is now a pipeline executor with the built-in default pipeline
  `[PreBuild ext, gcc:compile, PreLink ext, gcc:link, PostBuild ext]`. Validated
  byte-identical to the pre-refactor toolchain command lines on host + ARM/qemu
  (the one intended change: target `link-flags`, previously silently dropped, are
  now applied). `Toolchain` is reduced to a process runner.
- [~] 4. objcopy/disassembly/size already run as pipeline steps; remaining work is
  promoting the migrated objcopy to a first-class `gcc:objcopy`
- [x] 5. `run`/`qemu`/`renode` steps (`Run` phase, resolved out-of-band by
  run/test); top-level `test-runner` demoted to a deprecated fallback. Example
  updated to the step form.
- [ ] 6. drop typed gcc fields + the legacy `test-runner` field from the schema;
  `migrate` emits `settings`+`pipeline`+run step
- [x] **Source generation / transforms** — in-process `transpile` step (the
  transpiler's integration point, stubbed) + generic external `transform` step,
  shared via `TransformSupport`; mutable source set, generated-header include
  dirs, incremental (delivered ahead of the pipeline refactor since the
  transpiler needs it now)
