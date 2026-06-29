# cortex-m3 + qemu example

A minimal, self-contained ARM Cortex-M3 target that boots under QEMU's
`lm3s6965evb` machine and runs `minuteos test` suites via semihosting. This is
a reference for wiring up an emulated test target; a real project would get its
startup/linker pieces from `lib-arm`.

## Files

| File | Purpose |
|------|---------|
| `targets/cortex-m3/target.yaml` | toolchain prefix, arch flags, linker script, and the qemu `test-runner` |
| `targets/cortex-m3/startup.c` | vector table, reset handler, `.data`/`.bss` init, `main()` entry |
| `targets/cortex-m3/syscalls.c` | semihosting retargeting of `_write`/`_exit` + newlib stubs |
| `targets/cortex-m3/lm3s.ld` | LM3S6965 memory map; places the `test_cases` section |

## Usage

Drop `targets/cortex-m3/` into your project's `lib/targets/` (or a `lib-*` root),
then add a configuration that builds for it:

```yaml
configurations:
  qemu:
    target: cortex-m3
    config: Debug      # see note below
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

## Requirements

- `arm-none-eabi-gcc` (tested with 13.2.1)
- `qemu-system-arm` (tested with 8.2.2)

## Note on optimization level

This example builds tests at `-O0` (`config: Debug`). arm-gcc 13.2 mis-compiles
the testrunner's section-walk loop at `-O3` for this deliberately-minimal
runtime (the host build is unaffected and runs at `-O3`). Test binaries are
commonly built unoptimized anyway. A production startup/runtime (e.g. lib-arm's)
does not have this sensitivity.
