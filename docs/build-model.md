# The build model

minuteOS builds through a **task graph** over opaque **artifacts**. The core
knows no toolchain: compiling, linking, source generation, and post-processing
are all *steps* that read a generic settings bag and exchange artifacts. GCC is
just the default bundle of steps.

## Two kinds of dependency

Keeping these separate is central to the design.

### 1. Module dependencies — *what code is in the build*

Resolved once, before the graph runs, into a `BuildConfiguration`:

- **Components** declare `requires: [...]`; the resolver takes the transitive
  closure to get the full component set (e.g. `kernel → base`).
- **Targets** declare `requires:` as **inheritance** (e.g.
  `cortex-m3 → cortex-m → cmsis`, plus a shared `all`), walked into a target
  chain.
- Each contributor's source dirs, include dirs, defines, settings, and steps are
  merged with precedence: most-specific target wins for scalars; components and
  targets accumulate lists; the configuration overrides.

This decides which sources are compiled and with what flags. It is **not**
incremental tracking.

### 2. Artifact dependencies — *what must rebuild when a file changes*

This is the task graph plus a fingerprint cache:

- **Step edges** — an edge `producer → consumer` exists when a consumer's
  selector matches a producer's output properties (`scan → compile → link →
  objcopy`). Edges drive *ordering* (topological) and *parallelism* (independent
  steps run concurrently).
- **Action edges within a step** — e.g. the PCH before the compiles that
  `-include` it (a compile action lists the `.gch` as an input).
- **Fan-in via depfiles** — an object depends on its `.cpp` *and every header it
  includes*. Headers come back from gcc's `.d` file (`-MMD`) and are recorded
  into the action's fingerprint set, so editing any included header rebuilds
  exactly the objects that include it. A *generated* header (from a transform
  step) is additionally a real graph **edge**, so it is produced before compile.
- **Fingerprint cache** (`out/<cfg>/.cache/actions.json`) — each action records
  the fingerprints of its inputs (declared + depfile-discovered), its outputs,
  and a config fingerprint (the command line). An action is skipped when all of
  those match. This also detects **removed** inputs (the declared-input set must
  match, so dropping an object relinks) and **flag changes** (the command line is
  part of the fingerprint).

So `requires:`/inheritance decide *what is built*; the task graph, depfiles, and
fingerprints decide *what is re-built*.

## Artifacts, steps, actions

- An **artifact** is a file identity plus opaque key-value **properties**
  (`{kind: source, lang: cpp}`, `{kind: image, format: elf}`). The engine never
  interprets a property; it only matches them.
- A **step** declares what it *consumes* (selectors) and *produces* (property
  templates), and — once its inputs exist — expands into concrete **actions**.
- An **action** is one unit of work: declared input/output files plus a run
  function. The engine schedules, fingerprints, and parallelizes actions.

The graph is wired statically by property match; each node's concrete action
fan-out is computed lazily once its inputs are materialized. This is what lets a
transform step produce an unknown-until-run set of sources that the compile step
then fans out over.

## Incremental behavior

- **No-op rebuild** runs no toolchain commands.
- **Header edit** recompiles only the units that include it (via `.d`).
- **Flag/define change** rebuilds (the command line is fingerprinted).
- **Removed source** drops its object and relinks.
- **Orphan cleanup** — outputs no longer produced (e.g. after deleting a `.cs`)
  are deleted.
- **Fingerprint strategy** is pluggable: mtime+size by default (fast), or
  content-hash (`build --hash`) so a touch-without-change doesn't rebuild.

## Parallelism

Two levels, capped by a single job limit (`-j`, default = CPU count):
independent **steps** run concurrently (e.g. objcopy/disassembly/size after
link, or a sub-build alongside compiles), and independent **actions** within a
step run concurrently (all the compiles), while ordering constraints (PCH before
compiles) are respected.

## Built-in step catalog

| Step | Consumes | Produces | Notes |
|------|----------|----------|-------|
| `scan:gcc` / `scan:cs` | source dirs | `source` artifacts | discovers `.c/.cpp/.S` / `.cs` |
| `gcc:compile` | `source`, `header-dir` | `object` | honors the PCH |
| `gcc:link` | `object` | `image` (`format=elf`) | reads `gcc.link-*`, `gcc.ld-script` |
| `gcc:objcopy` | `image` (elf) | `image` (bin/hex/srec) | `formats:` shorthand or `format`/`ext`/`args` |
| `disassembly` | `image` (elf) | `text` | `.S` + `.SS` |
| `size` / `size-report` | `image` (elf) | — | prints the size report |
| `transpile` | `source` (`lang=cs`) | `source` (`lang=cpp`) + `header-dir` | in-process source generator (stub) |
| `transform` | (own glob) | generated `source` + `header-dir` | external tool + manifest |
| `git-version` | — | `settings` | contributes `APP_VERSION*` defines (augmenter) |
| `sub-build` | (a config name) | `object` (blob) | builds a nested config, embeds it |
| `shell` | `image` (elf) | — | arbitrary command (escape hatch) |
| `run` / `qemu` / `renode` | `image` | — | `Run` phase; invoked by run/test, not build |

### sub-build

Builds another configuration as a nested graph (its own cache) and embeds its
image as a `kind=object` blob that `gcc:link` picks up like any object — so
"an image becomes an object" is an ordinary edge. Used for a bootloader embedded
in a firmware image:

```yaml
steps:
  - name: sub-build
    config:
      configuration: bootldr     # another configuration in this project
      blob-section: .binboot
      blob-format: elf32-littlearm
      blob-arch: arm
```

## Settings as an ambient input

A few steps *augment* the settings bag rather than producing files — e.g.
`git-version` contributes `APP_VERSION*` defines. Augmenters (steps producing
`kind=settings`) run before any reader, so the merged bag is visible to every
step's planning. Because defines are global, a version change re-fingerprints
every compile (a full rebuild — the accepted cost of a global define).
