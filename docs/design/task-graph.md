# A toolchain-agnostic task-graph core

Status: **proposal / discussion** (supersedes the fixed-slot model in
`generalized-spec.md`). Paper first — no code until the contract is settled.

## Why

The current build engine is, honestly, a C++ build engine wearing a generic
coat. The C++ assumption is threaded through the *core*, not just the steps:

- `SourceLanguage { C, Cpp, Assembly }`, `SourceFile`, `SourceCollector` — a
  closed language enum and extension→language mapping baked into the core.
- The fixed artifact slots `Sources` / `Objects` / `Image` — three names for what
  is really one thing (an artifact), and the names don't hold up:
  - a transpiler turns *source into source* (`*.cs` → `*.cpp`);
  - the bootloader turns an *image into an object* (a sub-build's ELF becomes a
    blob linked into the parent) — "Image" and "Object" are the same file seen by
    two different steps.
- PCH as a first-class concept on `BuildConfiguration`.
- The privileged linear shape *source → object → image* itself.
- `gcc.*` as the conventional settings namespace.

Design principle #1 from `generalized-spec.md` — "the core knows no toolchain" —
is therefore still aspirational. This document makes it real: the core becomes a
**pure task graph over opaque artifacts**, and *everything C++* (source
discovery, the language enum, PCH, compile/link/objcopy, the linear pipeline)
moves out into a **bundle** of steps that is an architectural peer of the C#→C++
transpiler — not part of the engine.

The test of the abstraction: **delete every C++ step and the engine still builds
things.** Today, no. Under this design, yes.

## Scope

- **In-tree bundles** for now: a new toolchain/language is added as a peer set of
  steps *in this source tree*, recompiled with the tool. A clean **internal**
  interface is the goal.
- **Out of scope (future, additive):** dynamically loaded out-of-tree plugins
  (a stable binary/serialization ABI + a loader). The contract below is kept
  clean enough that a plugin layer is additive — it does not reshape the model.

## The core model

The core understands exactly three things and **no toolchain semantics**.

### Artifact

```
Artifact
  Id     : stable identity (a file path for v1; logical value-artifacts are a
           later extension)
  Tags   : set<string>, OPAQUE to the engine ("cpp-source", "object", "elf", …)
```

The engine never interprets a tag. Tags are an **open, step-owned namespace**;
they exist only so the engine can match producers to consumers. File-backed
artifacts are fingerprinted by content (hash) or mtime+size for incrementality.

### Step

A step declares, statically, what tags it consumes and produces, and knows how to
expand into concrete work once its inputs exist:

```
interface IStep
  Name       : string
  Signature  : { consumes: Selector[], produces: TagSpec[] }
  Plan(PlanContext) : IEnumerable<BuildAction>

Selector   : { Tag: string, Glob?: string, Cardinality: One | Many }
BuildAction: { Inputs: Artifact[], Outputs: Artifact[],
               Run: async ActionContext -> ActionResult }
ActionResult: { DiscoveredInputs?: path[],     // depfile-style dynamic deps
                ProducedArtifacts?: Artifact[] } // when outputs aren't static
```

`PlanContext` exposes the step's **materialized inputs** (the concrete artifacts
its upstreams produced) and the resolved `Settings` bag. That's the whole step
surface — toolchain-agnostic and freezable.

### Engine

Four domain-free responsibilities, no knowledge of "source", "compile", "link",
or "image":

1. **Wire** a step graph: an edge `producer → consumer` exists when a consumer's
   selector tag matches a producer's output tag.
2. **Schedule** in topological order; run independent nodes/actions in parallel.
3. **Fingerprint** each action and skip it when unchanged.
4. **Run** actions and thread produced artifacts downstream.

## Static graph, lazy fan-out (how dynamic I/O fits)

The hard part — and the reason a naive "declare everything up front" graph fails —
is that real builds discover work as they run:

- **per-file fan-out**: compile is N actions, one per source, not one node;
- **discovered outputs**: a transpiler's output set is `f(inputs)`, known only
  after it runs;
- **discovered inputs**: compile's true header deps come from the `.d` file
  *after* compiling.

The resolution: **the step graph is wired statically by tags; the concrete action
fan-out per node is computed lazily, once that node's inputs are materialized.**

- Plan phase builds the small **step graph** (nodes = steps, edges = tag matches).
  `transpile(produces cpp-source) → compile(consumes cpp-source)` is an edge even
  though *which* `.cpp` files exist is still unknown.
- Execute phase walks it in topo order. Reaching a step, the engine materializes
  its inputs (now concrete), calls `Plan(inputs)` to get actions, runs them,
  collects the produced artifacts (a possibly-dynamic concrete set), and passes
  them downstream. Compile's `Plan` therefore sees the transpiler's actual output
  files; it returns one action per source.

This cleanly absorbs all three cases above.

## Incrementality

Per **action**: `fingerprint = hash(input contents + command/config + tool
identity)`. Skip when a recorded fingerprint matches and declared outputs exist.
Dynamic `DiscoveredInputs` (a depfile) are folded into the next build's input set
for that action — generalizing today's `.d` handling, and giving
skip-when-unchanged uniformly to link/objcopy/everything (today only compile and
the transforms are incremental; link/objcopy/run always re-run).

Cache lives under `out/<config>/.cache` (fingerprints + recorded dynamic deps).
Open decision: content-hash (robust) vs mtime+size (cheap) vs hybrid.

## Parallelism

One scheduler: a ready-queue over the action DAG + a worker pool. This replaces
both the current sequential `foreach` over steps **and** the intra-compile
`SemaphoreSlim` — independent steps (e.g. `disassembly` and `gcc:objcopy`, both
reading the ELF) finally run concurrently.

## The core ⟷ bundle boundary

**Stays in the core (engine + structure):**

- the graph engine: artifacts, the step contract, the scheduler, the fingerprint
  cache;
- project/target/component **structure resolution** — inheritance, the component
  graph, directory layout — which produces (a) the opaque `Settings` bag and
  (b) the ordered **step list**. It knows nothing about toolchains;
- the `Settings` bag (opaque);
- `StepRegistry` / wiring; `run`/`test` orchestration (drives the graph to a
  terminal artifact, then launches it).

**Moves out into the `gcc` (native) bundle:**

- `SourceLanguage`, `SourceFile`, `SourceCollector` → a **`scan` step** that tags
  files by extension;
- PCH (becomes internal to `gcc:compile`);
- `gcc:compile` / `gcc:link` / `gcc:objcopy` + `GccFlags`;
- the `gcc.*` settings conventions;
- the default linear pipeline wiring;
- the `Sources` / `Objects` / `Image` / primary-output notions on
  `BuildConfiguration`.

The native build becomes "the default bundle shipped in-tree," a peer of the
transpiler bundle — not a privilege of the engine.

## Mapping today's steps onto the contract

| Step | consumes | produces | actions | dynamic |
|------|----------|----------|---------|---------|
| `scan` (was SourceCollector) | source dirs (from settings) | `cpp/c/asm-source` | one artifact per file | outputs discovered at plan |
| `cs:transpile` | `cs-source` | `cpp-source`, `header-dir` | invoke transpiler | **dynamic outputs** (manifest) |
| `transform` (external) | declared input glob | declared output tags | invoke tool | dynamic outputs (manifest) |
| `gcc:compile` | `cpp/c/asm-source` | `object` | one action per source (+PCH) | **dynamic inputs** (`.d`) |
| `gcc:link` | `object` (many) | `elf` | one action | — |
| `gcc:objcopy` | `elf` | `bin`/`hex`/`srec` | one per format | — |
| `sub-build` | a config name | `object` (blob) | nested graph → objcopy | nested |
| `disassembly` | `elf` | `text` | one action | — |
| `size` | `elf` | (report, no artifact) | one action | — |
| `git-version` | — | `header` / defines | one action | — |
| `run`/`qemu`/`renode` | `elf` | (exit/result) | terminal; invoked out-of-band by run/test | — |

**The bootloader case, resolved:** `sub-build` produces an `object`-tagged
artifact (the blob); `gcc:link` consumes `object`. The sub-build's own `elf` is
just an internal artifact of the nested graph. "Image becomes source" stops being
a special case — opaque tags make it ordinary. That is the whole point.

## Tag conventions (open namespace, engine-opaque)

`<lang>-source` (`cpp-source`, `c-source`, `asm-source`, `cs-source`), `object`,
`elf`, `bin`/`hex`/`srec`, `header-dir`, `archive`, `text`. Bundles define their
own; the engine only ever string-matches them.

## Staging (one frozen contract throughout)

Each stage keeps the step contract fixed, so steps never change between stages and
the tool stays green:

1. **Engine + frozen contract; port the gcc bundle.** Run **sequentially**, no
   skip. Validate byte-identical toolchain command lines vs today (host +
   ARM/qemu anchor).
2. **Fingerprint skip.** Uniform incrementality; verify no-op rebuilds do nothing.
3. **Parallel scheduler.** Replace the semaphore; verify output parity.

Plugin loading (out-of-tree bundles) is a later, additive layer — explicitly not
in these stages.

## Open decisions (to settle before coding)

1. **Artifact identity** — path-only for v1; defer logical value-artifacts?
2. **Fingerprint** — content-hash vs mtime+size vs hybrid; cache format.
3. **Selector matching** — tag-equality only, or tag + path glob + cardinality
   (One/Many)? (Link needs "all `object`"; objcopy needs "the one `elf`".)
4. **Terminal artifacts** — how the build's goal artifact(s) are designated (an
   explicit goal tag, or "whatever the configured pipeline ends at").
5. **Settings → scan** — structure resolution still supplies which dirs `scan`
   reads and the defines/includes; confirm that stays in core (it's structural,
   not toolchain).
6. **Migration shim** — do we keep the current slot-based `IBuildStep` working
   during stage 1, or convert all ~12 steps in one move? (Lean: convert together;
   the set is small and a half-migrated engine is harder to reason about.)
