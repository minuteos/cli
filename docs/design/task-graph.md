# A toolchain-agnostic task-graph core

Status: **stage 1 implemented** (supersedes the fixed-slot model in
`generalized-spec.md`).

> **Implementation progress.** Stage 1 (engine + frozen contract + native gcc
> bundle: `scan`→`compile`→`link`, sequential, per-action up-to-date check) is
> built under `minuteos/Build/Graph/` and reachable via `minuteos build --graph`.
> Validated **byte-identical** toolchain command lines vs the legacy runner on the
> host+ARM/qemu anchor; binaries run; no-op rebuilds do zero work. Remaining:
> port `objcopy`/`transpile`/`transform`/`sub-build`/`git-version`/`disassembly`/
> `size` onto the contract, the settings-ambient augmenter ordering, the
> fingerprint cache + orphan cleanup, the parallel scheduler, then retire the
> legacy `BuildRunner`.

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
  Id          : stable identity — a file path. Logical (non-file) artifacts use a
                reserved prefix to disambiguate, e.g. "value:version".
  Properties  : map<string,string>, OPAQUE to the engine
                e.g. { kind: source, lang: cpp } ; { kind: image, format: elf }
```

Artifacts carry **key-value properties**, not bare string tags — so a selector can
match a single dimension (`kind=source`, any language) or a conjunction
(`kind=source, lang=cpp`) without consumers enumerating compound names, and so
properties like `format`/`arch` ride along. (A bare "tag" is just sugar for
`kind=<tag>`.) The engine never interprets a property; properties are an **open,
step-owned namespace** used only to match producers to consumers. File artifacts
are fingerprinted by a **pluggable** strategy (default: mtime+size).

### Step

A step declares, statically, what it consumes and produces, and knows how to
expand into concrete work once its inputs exist:

```
interface IStep
  Name       : string
  Signature  : { consumes: Selector[], produces: PropSpec[] }
  Plan(PlanContext) : IEnumerable<BuildAction>

Selector   : a predicate over artifact properties.
             v1: required key=value pairs (all must match) + Cardinality (One|Many).
             Later: pluggable custom predicates.
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
   selector matches a producer's output properties.
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

The resolution: **the step graph is wired statically by property match; the
concrete action fan-out per node is computed lazily, once that node's inputs are
materialized.**

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
The fingerprint strategy is **pluggable** (an interface); v1 default is mtime+size,
swappable for content-hash later without touching steps.

### Fan-in: many files contribute to one output

An object isn't a function of its `.cpp` alone — it depends on every header that
`.cpp` includes. Two distinct sets keep this correct, and they are *not* the same:

- **Graph edges** (producer→consumer artifacts) drive *ordering/scheduling*.
- **The fingerprint input set** of an action is *every* file that contributes to
  its output — declared inputs **plus** dynamically discovered deps (the `.d`
  file) — and drives *skip decisions*. It is a superset of the edges.

So an object's included headers arrive via the depfile and land in the fingerprint
set; editing any of them rebuilds the object. The distinction that matters:

- a **checked-in** header is *only* a fingerprint input — it already exists, so no
  edge/ordering is required;
- a **generated** header (e.g. a transpiler output) is *also* a real **edge**, so
  it is produced before the compile action runs.

Declared edges guarantee first-build ordering; discovered deps guarantee
incremental correctness on every build after. The depfile feeds both: anything in
it that matches a known producer's output is (or confirms) an edge; everything
else is a leaf fingerprint input.

### Worked example: the transpiler (dynamic outputs)

The C#→C++ transpiler is the load-bearing case, and it exercises *both* dynamic
mechanisms. A `.cs` edit may update any number of `.cpp`/`.h` — unknowable until
the pass runs. The model handles it without anticipating the output set:

1. The `cs:transpile` action consumes **all** `kind=source, lang=cs` artifacts
   (whole-program), so any `.cs` change is in its fingerprint input set → the pass
   re-runs. It is one action, so "always re-runs on any `.cs` change" is cheap.
2. It reports `ProducedArtifacts` — the actual `.cpp`/`.h` it wrote, knowable only
   after running. The engine diffs this against the previous run's recorded set:
   new/changed → downstream rebuilds; identical → skipped; **vanished** (a `.cs`
   was deleted) → that artifact and its downstream object are pruned.
3. **Lazy fan-out**: `gcc:compile.Plan()` runs *after* the transpiler's outputs
   materialize, so it sees the current `.cpp` set each build — adding/removing a
   `.cs` adds/removes compile actions automatically.
4. **Minimal recompile**: the transpiler writes content-identically when nothing
   changed (write-if-changed), so unchanged `.cpp` keep their mtime → per-file
   compile fingerprints match → those objects are **skipped**. The pass always
   runs; recompilation stays proportional to what actually changed.
5. **Cross-unit headers**: a generated `.h` included by many `.cpp` lands in each
   object's `.d` (dynamic inputs), so moving it recompiles exactly its includers —
   the fan-in mechanism above, on top of the dynamic outputs.

So dynamic **outputs** (`ProducedArtifacts` + lazy fan-out) answer "which `.cpp`
exist," and dynamic **inputs** (`.d`) answer "which must recompile when a shared
generated header changes." The one extra obligation this places on the engine is
**orphan cleanup**: when the produced set shrinks, delete the now-unproduced
`.cpp`/`.o` so a stale object is never linked.

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

Properties are written `key=value`; `kind=X` alone is the common case.

| Step | consumes | produces | actions | dynamic |
|------|----------|----------|---------|---------|
| `scan` (was SourceCollector) | source dirs (from settings) | `kind=source, lang=c/cpp/asm` | one artifact per file | outputs discovered at plan |
| `cs:transpile` | `kind=source, lang=cs` | `kind=source, lang=cpp` + `kind=header-dir` | invoke transpiler | **dynamic outputs** (manifest) |
| `transform` (external) | declared input selector | declared output props | invoke tool | dynamic outputs (manifest) |
| `gcc:compile` | `kind=source, lang∈{c,cpp,asm}` | `kind=object` | one action per source (+PCH) | **dynamic inputs** (`.d`) |
| `gcc:link` | `kind=object` (Many) | `kind=image, format=elf` | one action | — |
| `gcc:objcopy` | `kind=image, format=elf` (One) | `kind=image, format=bin/hex/srec` | one per format | — |
| `sub-build` | a config name | `kind=object` (blob) | nested graph → objcopy | nested |
| `disassembly` | `kind=image, format=elf` | `kind=text` | one action | — |
| `size` | `kind=image, format=elf` | (report, no artifact) | one action | — |
| `git-version` | — | `kind=header` / defines | one action | — |
| `run`/`qemu`/`renode` | `kind=executable` (One) | (exit/result) | invoked by run/test | — |

**The bootloader case, resolved:** `sub-build` produces a `kind=object` artifact
(the blob); `gcc:link` consumes `kind=object`. The sub-build's own ELF is just an
internal artifact of the nested graph. "Image becomes source" stops being a
special case — opaque properties make it ordinary. That is the whole point.

## Property conventions (open namespace, engine-opaque)

`kind` is the primary discriminator: `source` (+ `lang`), `object`, `image`
(+ `format=elf/bin/hex/srec`), `header-dir`, `header`, `archive`, `text`,
`executable`. Bundles define their own keys/values; the engine only ever matches
them. `run`/`test` select their input like any other consumer (`kind=executable`,
Cardinality One) — there is no separate "terminal" concept.

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

## Decisions (settled)

1. **Artifact model — key-value properties**, not bare tags (see Artifact above).
2. **Identity — path.** Logical value-artifacts use a reserved prefix
   (`value:…`) to disambiguate from real paths.
3. **Fingerprint — pluggable, mtime+size default.** Replaceable with content-hash
   later behind the same interface; no step changes.
4. **Selector — property predicate.** v1 = required `key=value` conjunction +
   Cardinality (One/Many); custom predicates are a later extension. (Link =
   `kind=object` Many; objcopy/run = `kind=image|executable` One.)
5. **No terminal concept.** `build` runs all configured steps and produces
   everything; `run`/`test` are ordinary consumers selecting `kind=executable`
   (One).
6. **`scan` is language-specific.** Each bundle ships its own scan step that owns
   its extensions and tags outputs (`kind=source, lang=…`); the **source dirs**
   come from the core's structural resolution via the `Settings` bag (structural,
   not toolchain). Defines/includes likewise stay structural in the bag.
7. **No compatibility shim.** Convert all ~12 steps to the new contract in one
   move — this is a prototype; a half-migrated engine is harder to reason about.
8. **Settings is an ambient artifact; steps may augment it.** Structural
   resolution emits the base `settings`; a few steps *augment* it (e.g.
   `git-version` adds `APP_VERSION` defines), the rest *read* it. The engine
   orders **all augmenters before any reader**, so a reader's `Plan()` sees the
   fully merged bag — no per-step wiring, and it matches "git-version writes into
   the bag." Consequence (accepted): defines are global `-D`s, so a settings
   change is in every compile action's fingerprint → a version bump triggers a
   full rebuild (same as changing `CFLAGS` in make). If that ever bites, the
   refinement is to have such steps emit a generated *header* one TU includes,
   instead of a global define — not now.

## Proposed (pending sign-off)

- **Cache** — one inspectable JSON per config: `out/<config>/.cache/actions.json`,
  mapping a stable action key (`step name + primary output path`) →
  `{ inputs: {path: fingerprint}, deps: [discovered paths], outputs: [produced
  paths], config: fingerprint }`. Fingerprints are opaque strings (mtime→hash swap
  doesn't change the format); `outputs` drives orphan cleanup. Easy to delete; no
  binary format.
- **`scan` source dirs** — reuse the existing aggregation: structural resolution
  exposes the source dir list in the bag as `source-dirs` (structural, like
  `include-dirs`/`defines`). Each language `scan` step reads
  `settings.List("source-dirs")` and claims its **own** extensions within them
  (gcc: `.c/.cpp/.S`; cs: `.cs`), tagging outputs `kind=source, lang=…`.
  Extensions partition across scans, so no file is double-claimed.
