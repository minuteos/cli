# minuteos CLI

A .NET command-line tool for managing minuteos embedded projects. Replaces the Make-based build system with a structured, extensible alternative.

## Install

```bash
dotnet pack -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg minuteos
```

## Quick Start

```bash
# Create a new project
minuteos new my-firmware

# Build all configurations
cd my-firmware
minuteos build

# Run the host build
./out/release/my-firmware.elf
```

## Commands

| Command | Description |
|---------|-------------|
| `minuteos new <name>` | Scaffold a new project with lib, src, and config |
| `minuteos build [-c name]` | Build one or all configurations |
| `minuteos run [-c name]` | Build a configuration and run its output (host, or via the emulator) |
| `minuteos test [-c name]` | Build and run test suites |
| `minuteos clean [-c name]` | Remove build artifacts |
| `minuteos info [-c name]` | Show resolved build configuration |
| `minuteos init` | Create a `minuteos.yaml` in an existing project |
| `minuteos migrate [-p dir]` | Convert legacy `Include.mk` files to `component.yaml`/`target.yaml` |

## Project Configuration

Projects are configured via `minuteos.yaml`:

```yaml
name: my-firmware

defaults:
  target: host
  config: Release
  components:
    - kernel
  steps:
    - name: git-version
    - name: size-report

configurations:
  release: {}

  debug:
    config: Debug

  arm:
    target: cortex-m4
    settings:
      gcc.toolchain-prefix: arm-none-eabi-
      gcc.arch-flags: [-mcpu=cortex-m4, -mthumb]
      gcc.ld-script: default.ld
      gcc.primary-ext: .axf
      gcc.link-flags: [-nostartfiles, -specs=nano.specs]
    steps:
      - name: gcc:objcopy
        config:
          formats: bin,hex
```

Each named configuration inherits from `defaults` and can override any setting. Lists replace entirely (no merging).

### Configuration Fields

Structure is typed; all toolchain configuration goes in the generic `settings`
map (read by the gcc steps under `gcc.*` keys).

| Field | Description |
|-------|-------------|
| `target` | Target platform name (resolves to `targets/<name>/` directories) |
| `config` | Build config: `Release`, `Debug`, `Trace` |
| `components` | List of components to build |
| `defines` | Extra preprocessor defines |
| `include-dirs` | Extra include directories (relative to project root) |
| `source-dir` | Overrides the source root (default `src/`) |
| `settings` | Toolchain settings, e.g. `gcc.toolchain-prefix`, `gcc.arch-flags`, `gcc.c-flags`/`gcc.cxx-flags`, `gcc.link-flags`, `gcc.link-dirs`, `gcc.ld-script`, `gcc.primary-ext` |
| `steps` | Build steps to execute |

## Project Layout

```
my-project/
├── minuteos.yaml         # Project configuration
├── src/                  # Project source files
│   └── main.cpp
├── lib/                  # Core library (git submodule)
│   └── targets/
│       ├── all/          # Sources/headers for all targets
│       │   ├── base/     # Base component
│       │   └── kernel/   # Kernel component
│       └── host/         # Host-specific overrides
├── lib-arm/              # ARM support (git submodule)
│   └── targets/
│       ├── cortex-m/
│       ├── cortex-m3/
│       └── cortex-m4/
└── out/                  # Build output
    ├── release/
    └── debug/
```

Directories named `lib*` are automatically discovered as library roots. Each can contain a `targets/` directory with target-specific and shared (`all/`) code.

## Component System

Components are directories under `targets/<target>/` that contain source files. They declare dependencies and build contributions via `component.yaml`:

```yaml
# targets/all/kernel/component.yaml
requires:
  - base

defines:
  - KERNEL_ENABLED

source-dirs:
  - ../../../external-lib/src/

steps:
  - name: git-version
```

Falls back to parsing `Include.mk` (`COMPONENTS += base`) for backwards compatibility with the Make-based system.

### Component Fields

| Field | Description |
|-------|-------------|
| `requires` | Component dependencies (resolved recursively) |
| `defines` | Preprocessor defines |
| `include-dirs` | Additional include paths (relative to component dir) |
| `source-dirs` | Additional source directories (supports glob patterns) |
| `settings` | Toolchain settings (e.g. `gcc.cxx-flags`) |
| `steps` | Build steps contributed by this component |

## Target System

Targets define platform-specific settings. They can inherit from parent targets via `target.yaml`:

```yaml
# targets/cortex-m/target.yaml
requires:
  - cmsis
settings:
  gcc.toolchain-prefix: arm-none-eabi-
  gcc.arch-flags: [-mthumb]
  gcc.primary-ext: .axf
  gcc.ld-script: default.ld
  gcc.link-flags: [-nostartfiles, -specs=nano.specs]
  gcc.link-dirs: [ld_fallbacks/]
```

Target inheritance is resolved recursively. For example, `cortex-m4f` inherits from `cortex-m4`, which inherits from `cortex-m3`, which inherits from `cortex-m`.

When no `target.yaml` is present, the tool reads a legacy `Include.mk`, importing
`TARGETS +=`, `COMPONENTS +=`, `TOOLCHAIN_PREFIX`, `ARCH_FLAGS`, `PRIMARY_EXT`,
`LD_SCRIPT`, `LINK_FLAGS` (static tokens), `LINK_DIRS`, `DEFINES +=`, and the
`TEST_RUN`/`TEST_RUN_ARGS` emulator invocation (as a `run` step). Values that
reference Make variables (`$(...)`) are skipped — the common exceptions are
`LINK_DIRS`/`$(<NAME>_DIR)` (the target's own directory) and `$(TEST_FILTERS)`
(mapped to `{filter}`). This is enough to build *and test* the real
`minuteos/lib` + `lib-arm` for host and ARM/qemu with nothing but a target name
in `minuteos.yaml`.

Directory precedence is most-specific-first, so a target-specific header (e.g.
`qemu-arm/cortex_defs.h`) overrides the generic one (`cortex-m/...`).

## Migrating off Make

`minuteos migrate` converts a project or lib's `Include.mk` files into native
`component.yaml` / `target.yaml`, so Make can be removed entirely. It writes one
YAML file per `Include.mk`, converts `objcopy`-based `binary`/`ihex`/`srec` rules
into `gcc:objcopy` steps, and flags anything genuinely
non-declarative (recursive bootloader builds, `run` targets) for manual porting.
Use `-n`/`--dry-run` to preview, and `--delete-make` to remove each `Include.mk`
that migrated with nothing left unhandled.

Running `minuteos migrate --delete-make` on the real `minuteos/lib` + `lib-arm`
auto-converts and deletes 11 of the 12 `Include.mk` files. The one it keeps is the
recursive bootloader build — its conditional, two-mode Make can't be converted
mechanically, but the capability it needs (the [`sub-build` step](#the-sub-build-step))
now exists, so it can be ported declaratively by hand. The result builds and tests
purely from YAML (plus the generated shell steps) on both host (67/67) and
ARM/qemu (68/68). The only Make-parsing code lives in one class (`MakeImport`) to
be deleted once migration is complete.

## Build Steps

The build is a task graph: steps declare what artifact kinds they consume and
produce, and order themselves accordingly (a step consuming the linked image
runs after link automatically). The `phase` field remains as YAML vocabulary —
`Run` marks out-of-graph run steps; other phases are advisory. See
[docs/build-model.md](docs/build-model.md).

### Built-in Steps

| Step | Phase | Description |
|------|-------|-------------|
| `git-version` | PreBuild | Extract version from git tags into `APP_VERSION` defines |
| `size-report` | PostBuild | Print binary size via `size` tool |
| `disassembly` | PostBuild | Generate `.S` and `.SS` disassembly files |
| `binary-output` | PostBuild | Convert ELF to bin/hex/srec via `objcopy` |
| `shell` | PostBuild | Run an arbitrary command line (see below) |
| `sub-build` | PreLink | Build another configuration and embed its output as a blob |

### The `shell` step

`shell` is the generic escape hatch: it runs a command line through the system
shell, so any Make recipe can be expressed declaratively. Placeholders are
substituted before running:

```yaml
steps:
  - name: shell
    phase: PostBuild
    config:
      command: "{objcopy} -O binary {output} {output-base}.bin"
```

`{output}` is the primary output, `{output-base}` is it without extension,
`{output-dir}` the output directory, `{name}` the output name, and `{objcopy}`
`{objdump}` `{size}` `{cc}` `{cxx}` are the (prefixed) toolchain programs. This is
what `migrate` emits for `objcopy`-based Make rules.

### The `sub-build` step

`sub-build` builds another configuration from the same `minuteos.yaml` and embeds
its output into the current image as a binary blob — the declarative replacement
for the lib-arm bootloader pattern (and the general "build A, embed it in B"
case). The embedded program is just another configuration, typically using
`source-dir:` to build from its own sources:

```yaml
configurations:
  bootloader:
    source-dir: boot          # the bootloader's own sources
    components: [base]
  app:
    components: [kernel]
    steps:
      - name: sub-build
        phase: PreLink
        config:
          configuration: bootloader
          blob-section: .binboot   # objcopy --rename-section .data=<this>
```

The blob is reachable through objcopy's `_binary_<config>_bin_start/_end/_size`
symbols and placed in `blob-section`. Verified on ARM/qemu: an app sub-builds a
loader, embeds it, and reads the embedded bytes back at runtime.

## Testing

Test suites live under a component's `tests/` directory:

```
lib/targets/all/base/tests/sanity/sanity.cpp
lib/targets/all/kernel/tests/scheduler/scheduler.cpp
```

`minuteos test` discovers every `tests/<suite>/` directory, builds each suite
as a standalone binary (linking the `testrunner` component + the component under
test + the suite's own dependencies), runs it, and aggregates the results:

```
$ minuteos test
=== Testing configuration: release ===
Discovered 2 test suite(s).

  PASS  base/sanity       (2/2, 2ms)
  FAIL  kernel/scheduler  (1 failed)
          overflow: (count) == (8) at .../scheduler.cpp:24

Total: 5  Passed: 4  Failed: 1
```

Options: `-c <config>`, `-s <substring>` (filter suites), `-f <substring>`
(filter test cases), `-j <n>` (parallel jobs).

### Test runners (host, qemu, renode)

Executing the image is a target-overridable `Run`-phase step. With no run step,
the binary runs directly — that's the host case. For emulated targets, add a
`qemu`/`renode`/`run` step; `{image}` and `{filter}` are substituted:

```yaml
configurations:
  # host: no run step needed, binaries run directly

  qemu:
    target: cortex-m3
    steps:
      - name: qemu
        phase: Run
        config:
          args: '-machine lm3s6965evb -nographic -semihosting -kernel "{image}" -append "{filter}"'
          timeout: "60"

  renode:
    target: cortex-m3
    steps:
      # Renode drives a machine from a script; a small wrapper adapts it to the
      # {image} -> stdout contract. See examples/cortex-m3-qemu/renode.
      - name: run
        phase: Run
        config:
          command: examples/cortex-m3-qemu/renode/run.sh
          args: '"{image}"'
```

A target can also supply the run step (e.g. `targets/qemu-arm/target.yaml`), so
any configuration using that target inherits it; a config-level step overrides
the target's.

A complete, runnable Cortex-M3 + QEMU target (startup, linker script, semihosting,
and the qemu `run` step) lives in [`examples/cortex-m3-qemu`](examples/cortex-m3-qemu).
Dropping it into a project and running `minuteos test -c qemu` cross-compiles each
suite with `arm-none-eabi-gcc`, runs it under `qemu-system-arm`, and reports results
over semihosting.

### Test output format

The `testrunner` component prints machine-readable lines that `minuteos test`
parses:

```
##TEST## <name> PASS
##TEST## <name> FAIL <detail>
##SUMMARY## total=<n> passed=<n> failed=<n>
```

The scaffold (`minuteos new`) includes a minimal host testrunner and a sample
suite so `minuteos test` works out of the box. A suite is considered failed if it
times out, exits non-zero, or reports any failed case.

## Precompiled headers

If a `precompiled.hpp` is found in the project source directory (or, for test
builds, in a component such as `testrunner`), it is compiled once to a `.gch` and
prepended to every C++ translation unit via `-include`, matching the Make-based
build. Sources named `*.nopch.cpp` opt out. The `.gch` is rebuilt only when the
header (or something it includes) changes, and GCC's `-Winvalid-pch` guarantees
correctness if it ever goes stale.

## Incremental Builds

The build system uses GCC's `-MMD -MP` flags to generate dependency files. On subsequent builds, it parses these `.d` files to check if any included header has changed, rebuilding only affected translation units.

## Repository layout

```
src/
  MinuteOS.Build.Abstractions   # contracts for consumers & extensions
  MinuteOS.Build                # the build system (engine, gcc bundle, steps, MEDI)
  MinuteOS.Cli                  # the minimal CLI exe
tests/
  MinuteOS.Build.Tests
```

The build system is a library, independent of the CLI.

## Using the build system programmatically

Reference `MinuteOS.Build` and drive a build through DI — no CLI required:

```csharp
using Microsoft.Extensions.DependencyInjection;
using MinuteOS.Build;

var provider = new ServiceCollection()
    .AddLogging()
    .AddMinuteosBuild()
    .BuildServiceProvider();

var runner  = provider.GetRequiredService<IBuildRunner>();
var project = ProjectConfig.Load(projectRoot);
var config  = BuildConfiguration.Create(project, "host", projectRoot);

await runner.BuildAsync(config, new BuildOptions { Parallelism = 8 });
```

To add a **custom build step**, reference only `MinuteOS.Build.Abstractions` and
implement `IGraphStep` (declare what it consumes/produces by artifact properties;
expand into `BuildAction`s). See `docs/design/task-graph.md`.

## Building from Source

```bash
dotnet build                 # builds the solution (MinuteOS.slnx)
dotnet run --project src/MinuteOS.Cli -- --help
```

## Running Tests

```bash
dotnet test
```
