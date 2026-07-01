# Configuration reference

A project is described by three kinds of YAML file:

- **`minuteos.yaml`** (project root) — the configurations you build.
- **`component.yaml`** (in a component directory) — a reusable unit of sources.
- **`target.yaml`** (in a target directory) — a platform, with inheritance.

Toolchain-specific values live in a generic **`settings`** map so the core stays
toolchain-agnostic; the gcc steps read the `gcc.*` keys.

## `minuteos.yaml`

```yaml
name: my-project

# External dependencies providing lib roots. Two kinds: an existing directory
# (typically a submodule; `path` defaults to `name`), or a remote fetched as a
# commit tarball into a shared cache (never cloned). `ref` = commit | branch |
# tag; mutable refs are locked in minuteos.lock at restore time.
# See docs/dependencies.md; restored by `minuteos restore`.
dependencies:
  - name: lib                           # submodule at ./lib
  - name: lib-arm
    git: https://github.com/minuteos/lib-arm
    ref: main                           # tarball into cache, locked on restore

# Optional defaults inherited by every configuration.
defaults:
  config: Release

configurations:
  host:
    target: host              # target to build for (default: host)
    config: Release           # Debug | Release | Trace (affects -O and DEBUG/TRACE)
    components: [kernel]       # library components to include
    defines: [FEATURE_X]       # extra preprocessor defines
    include-dirs: [vendor/inc] # extra -I dirs (relative to the project)
    source-dir: src            # source root (default: src/)
    settings:                  # generic settings merged into the bag
      gcc.cxx-flags: [-fno-exceptions]
    steps:                     # build steps (see below)
      - name: gcc:objcopy
        config: { formats: "bin,hex" }
```

A **configuration** selects a target, a set of components, and may override any
setting. `config` picks the optimization/define profile (`Debug` → `-O0` +
`DEBUG`, `Release`/`Trace` → `-O3 -Os`, `Trace` also defines `TRACE`).

## `component.yaml`

A component is a directory of sources plus metadata. Components are pulled in by
name (from a configuration's `components:` or another component's `requires:`).

```yaml
requires: [base]              # other components this one needs (transitive)
defines: [USE_KERNEL]          # defines contributed to any build using it
include-dirs: [.]              # extra -I dirs (relative to the component)
source-dirs: [../vendor/src]   # extra source dirs (globs allowed)
exclude-sources: ["*.test.cpp"]
settings:                      # e.g. gcc.c-flags / gcc.cxx-flags
  gcc.cxx-flags: [-fno-rtti]
steps: []                      # steps this component contributes
```

## `target.yaml`

A target is a platform. Targets form an inheritance chain via `requires`
(most-specific wins for scalars; lists accumulate), with a shared `all` target
always last.

```yaml
requires: [cortex-m]           # parent target(s)
settings:
  gcc.toolchain-prefix: arm-none-eabi-
  gcc.arch-flags: [-mcpu=cortex-m3, -mthumb]
  gcc.ld-script: lm3s.ld       # resolved against the target's dirs
  gcc.link-flags: [--specs=nano.specs, -nostartfiles]
  gcc.link-dirs: [ld_fallbacks/]
  gcc.primary-ext: .axf        # output extension (default .elf)
steps:
  # How to execute the image (see "Run steps" below).
  - name: qemu
    phase: Run
    config:
      args: '-machine lm3s6965evb -nographic -semihosting -kernel "{image}"'
      timeout: "30"
```

## The settings bag

`settings` values are merged (with `target → component → configuration`
precedence) into an opaque key→value(s) bag the steps read. Conventions:

| Key | Meaning |
|-----|---------|
| `defines` | preprocessor defines (also settable via the typed `defines:` field) |
| `include-dirs` | `-I` directories |
| `gcc.toolchain-prefix` | e.g. `arm-none-eabi-` |
| `gcc.arch-flags` | arch flags for compile **and** link |
| `gcc.c-flags` / `gcc.cxx-flags` | language-specific compile flags |
| `gcc.link-flags` | linker flags |
| `gcc.link-dirs` | `-L` search dirs (relative → resolved against target/component dirs) |
| `gcc.ld-script` | linker script (resolved against target/component dirs) |
| `gcc.primary-ext` | primary output extension |

`kind` for a bare tag is `<value>`; near-universal keys (`defines`,
`include-dirs`) are flat, toolchain-specific keys are namespaced (`gcc.*`).

## Steps

Each entry references a step by name with an optional `config:` block. Ordering
is by artifact dependencies (not declaration order), so a post-link step runs
after link automatically. See [the build model](build-model.md) for the catalog.

```yaml
steps:
  - name: gcc:objcopy
    config: { formats: "bin,hex,srec" }
  - name: size-report
  - name: transpile          # source generation
```

## Run steps

Executing the image is a target-overridable **`Run`-phase** step (not a special
top-level field). `minuteos run` and `minuteos test` resolve it after building.

```yaml
steps:
  - name: qemu               # or "renode", or "run" (host: exec directly)
    phase: Run
    config:
      # {image} = the linked ELF, {filter} = the active test filter
      args: '-machine lm3s6965evb -nographic -semihosting -kernel "{image}" -append "{filter}"'
      timeout: "30"
```

The `qemu`/`renode` step names default their command to `qemu-system-arm` /
`renode`; a bare `run` with no `command` executes the image directly.

## Migrating from Make

`minuteos migrate` converts legacy `Include.mk` files to `component.yaml` /
`target.yaml`, emitting `settings:` + `gcc:objcopy`/`run` steps. Non-declarative
recipes are flagged for manual attention (e.g. a recursive bootloader `$(MAKE)`
becomes a [`sub-build`](build-model.md#sub-build) step).

```bash
minuteos migrate --dry-run          # preview
minuteos migrate --delete-make      # convert and remove migrated Include.mk
```
