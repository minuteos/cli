# Getting started

## Install

The CLI is a .NET tool:

```bash
dotnet tool install --global minuteos
```

Or run it from a checkout:

```bash
dotnet run --project src/MinuteOS.Cli -- <command>
```

Requirements: a `gcc`/`g++` on `PATH` for host builds; a cross toolchain (e.g.
`arm-none-eabi-gcc`) and an emulator (`qemu-system-arm`) for embedded targets.

## A first project

A project is a directory with a `minuteos.yaml` and a `src/` folder:

```
hello/
├── minuteos.yaml
└── src/
    └── main.cpp
```

```yaml
# minuteos.yaml
name: hello
configurations:
  host:
    target: host        # compile/link for the build machine (plain gcc/g++)
    components: []
```

```cpp
// src/main.cpp
#include <cstdio>
int main() { std::printf("Hello!\n"); return 0; }
```

Build and run it:

```bash
minuteos build -c host      # -> out/host/hello.elf
minuteos run   -c host      # build, then execute
```

(This is the [`hello-host`](../examples/hello-host) example.)

## Commands

| Command | What it does |
|---------|--------------|
| `build [-c <cfg>]` | Build a configuration (all, if omitted). `-j N` for parallelism, `--hash` for content-hash incrementality. |
| `run -c <cfg>` | Build, then execute the image — directly (host) or via the target's `run` step (emulator). |
| `test [-c <cfg>]` | Discover and run test suites (`-f` filter cases, `-s` filter suites). |
| `info [-c <cfg>]` | Show the resolved configuration (targets, components, settings). |
| `restore` | Restore external dependencies (init git submodules, clone declared repos). |
| `migrate` | Convert legacy Make `Include.mk` files to YAML. |
| `new`, `init`, `clean` | Scaffold / initialize / clean. |

`-v` raises log verbosity (`-vv` shows every toolchain command line); `-q` lowers it.

## Multiple configurations

A project usually has several configurations — e.g. a host build for fast test
iteration and an embedded target:

```yaml
configurations:
  host:
    target: host
    components: [kernel]
  qemu:
    target: qemu-arm      # inherits cortex-m3 -> cortex-m -> cmsis
    config: Release
    components: [kernel]
```

```bash
minuteos test -c host      # fast host test loop
minuteos test -c qemu      # same suites under QEMU
```

## Incremental builds

Builds are incremental with no configuration. A no-op rebuild runs no compiler;
editing a header recompiles exactly the translation units that include it;
changing a flag or removing a source triggers the right relink. See
[the build model](build-model.md).
