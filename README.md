# minuteos CLI

A .NET command-line tool for managing minuteos embedded projects. Replaces the Make-based build system with a structured, extensible alternative.

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
| `minuteos test [-c name]` | Build and run test suites |
| `minuteos clean [-c name]` | Remove build artifacts |
| `minuteos info [-c name]` | Show resolved build configuration |
| `minuteos init` | Create a `minuteos.yaml` in an existing project |

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
    toolchain-prefix: arm-none-eabi-
    arch-flags:
      - -mcpu=cortex-m4
      - -mthumb
    ld-script: default.ld
    primary-ext: .axf
    link-flags:
      - -nostartfiles
      - -specs=nano.specs
    steps:
      - name: binary-output
        config:
          formats: bin,hex
```

Each named configuration inherits from `defaults` and can override any setting. Lists replace entirely (no merging).

### Configuration Fields

| Field | Description |
|-------|-------------|
| `target` | Target platform name (resolves to `targets/<name>/` directories) |
| `config` | Build config: `Release`, `Debug`, `Trace` |
| `components` | List of components to build |
| `toolchain-prefix` | GCC prefix (e.g. `arm-none-eabi-`) |
| `arch-flags` | Architecture compiler/linker flags |
| `c-flags` / `cxx-flags` | Extra C/C++ compiler flags |
| `link-flags` | Extra linker flags |
| `defines` | Extra preprocessor defines |
| `include-dirs` | Extra include directories (relative to project root) |
| `primary-ext` | Output extension (`.elf`, `.axf`) |
| `ld-script` | Linker script filename (searched in target/component dirs) |
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
| `c-flags` / `cxx-flags` | Extra compiler flags |
| `link-flags` | Extra linker flags |
| `steps` | Build steps contributed by this component |
| `exclude-sources` | Source file patterns to exclude |

## Target System

Targets define platform-specific settings. They can inherit from parent targets via `target.yaml`:

```yaml
# targets/cortex-m/target.yaml
requires:
  - cmsis
toolchain-prefix: arm-none-eabi-
arch-flags:
  - -mthumb
primary-ext: .axf
ld-script: default.ld
link-flags:
  - -nostartfiles
  - -specs=nano.specs
link-dirs:
  - ld_fallbacks/
```

Target inheritance is resolved recursively. For example, `cortex-m4f` inherits from `cortex-m4`, which inherits from `cortex-m3`, which inherits from `cortex-m`.

Falls back to parsing `Include.mk` (`TARGETS += cortex-m`) for backwards compatibility.

## Build Steps

Steps run at specific phases of the build pipeline:

| Phase | When | Example |
|-------|------|---------|
| `PreBuild` | Before compilation | Generate sources, extract version |
| `PreLink` | After compilation, before linking | Modify objects |
| `PostBuild` | After linking | Generate binary outputs, disassembly |

### Built-in Steps

| Step | Phase | Description |
|------|-------|-------------|
| `git-version` | PreBuild | Extract version from git tags into `APP_VERSION` defines |
| `size-report` | PostBuild | Print binary size via `size` tool |
| `disassembly` | PostBuild | Generate `.S` and `.SS` disassembly files |
| `binary-output` | PostBuild | Convert ELF to bin/hex/srec via `objcopy` |

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

Each configuration (or target) declares how to execute its test binaries. With no
`test-runner`, the binary runs directly — that's the host case. For emulated
targets, provide a runner command; `{binary}` and `{filter}` are substituted:

```yaml
configurations:
  # host: no test-runner needed, binaries run directly

  qemu:
    target: cortex-m3
    test-runner:
      command: qemu-system-arm
      args: [-machine, lm3s6965evb, -nographic, -semihosting, -kernel, "{binary}"]
      timeout: 60

  renode:
    target: cortex-m4
    test-runner:
      command: renode-test
      args: ["{binary}"]
```

A target can also supply the runner (e.g. `targets/qemu-arm/target.yaml`), so any
configuration using that target inherits it. The profile's runner takes precedence
over the target's.

A complete, runnable Cortex-M3 + QEMU target (startup, linker script, semihosting,
and the qemu `test-runner`) lives in [`examples/cortex-m3-qemu`](examples/cortex-m3-qemu).
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

## Incremental Builds

The build system uses GCC's `-MMD -MP` flags to generate dependency files. On subsequent builds, it parses these `.d` files to check if any included header has changed, rebuilding only affected translation units.

## Building from Source

```bash
cd minuteos
dotnet build
dotnet run -- --help
```

## Running Tests

```bash
dotnet test minuteos.tests
```
