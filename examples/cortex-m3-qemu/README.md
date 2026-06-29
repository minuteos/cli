# cortex-m3 emulated-test example (qemu + renode)

A minimal, self-contained ARM Cortex-M3 target that boots under emulation and
runs `minuteos test` suites via semihosting - on both QEMU (`lm3s6965evb`) and
Renode. This is a reference for wiring up an emulated test target; a real project
would get its startup/linker pieces from `lib-arm`.

## Files

| File | Purpose |
|------|---------|
| `targets/cortex-m3/target.yaml` | toolchain prefix, arch flags, linker script, and the qemu `test-runner` |
| `targets/cortex-m3/startup.c` | vector table, reset handler, `.data`/`.bss` init, `main()` entry |
| `targets/cortex-m3/syscalls.c` | semihosting retargeting of `_write`/`_exit` + newlib stubs |
| `targets/cortex-m3/lm3s.ld` | LM3S6965 memory map; places the `test_cases` section |
| `renode/lm3s.repl` | Renode platform (cortex-m3 + flash/ram + semihosting UART) |
| `renode/run.sh` | adapts Renode to the `{binary}`→stdout test-runner contract |

## Usage

Drop `targets/cortex-m3/` into your project's `lib/targets/` (or a `lib-*` root),
then add a configuration that builds for it:

```yaml
configurations:
  qemu:
    target: cortex-m3
    config: Release
```

The `test-runner` is supplied by `target.yaml`, so any configuration using the
`cortex-m3` target inherits the qemu invocation:

```yaml
test-runner:
  command: qemu-system-arm
  args: [-machine, lm3s6965evb, -nographic, -semihosting, -kernel, "{binary}"]
  timeout: 30
```

Then:

```bash
minuteos test -c qemu
```

```
=== Testing configuration: qemu ===
Discovered 1 test suite(s).

  PASS  base/sanity  (2/2, 39ms)

Total: 2  Passed: 2  Failed: 0
```

## Running under Renode instead

Renode drives a machine from a script rather than executing an ELF directly, so
this example includes a small wrapper (`renode/run.sh`) that bridges it to the
test-runner contract. Point a configuration's `test-runner` at it (a config-level
runner overrides the target's qemu one):

```yaml
configurations:
  renode:
    target: cortex-m3
    config: Release
    test-runner:
      command: /abs/path/to/examples/cortex-m3-qemu/renode/run.sh
      args: ["{binary}"]
```

```bash
minuteos test -c renode
#   PASS  base/sanity  (2/2, 4048ms)
```

The same suites pass under both emulators; the wrapper loads the binary onto the
`renode/lm3s.repl` platform, captures semihosting output, and echoes it back.

## Requirements

- `arm-none-eabi-gcc` (tested with 13.2.1)
- `qemu-system-arm` (tested with 8.2.2) and/or `renode` (tested with 1.15.3)

Works at both `-O0` (`config: Debug`) and `-O3` (`config: Release`).

## A note on the startup

`_start_c` calls `main(0, &argv0)` with explicit `argc`/`argv` rather than
`main()`. This matters: calling `main()` with the wrong signature leaves garbage
in the argument registers, and the resulting stray `argc > 1` comparison - feeding
the IT-blocks the compiler emits for the optional test-name filter - was enough to
make qemu's TCG mis-execute the following conditional branch at `-O3`, silently
skipping the whole test loop. Passing real arguments fixes it. A reminder that on
bare metal the C runtime contract (a correctly-formed `main` entry) is yours to
uphold.
