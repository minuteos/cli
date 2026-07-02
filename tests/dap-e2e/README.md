# `minuteos dap` end-to-end test

Drives the debug adapter (`minuteos dap`) with a scripted DAP client against
the [`cortex-m3-qemu`](../../examples/cortex-m3-qemu) example target running
under `qemu-system-arm`:

initialize → launch `{config: qemu}` (resolved through the build system) →
setBreakpoints → configurationDone → stopped(breakpoint) →
threads/stackTrace/scopes → locals/globals/registers → evaluate → step →
continue → second hit → readMemory → semihosting output as `output` events →
pause → disconnect.

```bash
./run.sh              # scaffolds into a temp dir, builds, runs the client
./run.sh /some/dir    # keep the scaffolded project around for inspection
```

Requires `dotnet`, `python3`, `arm-none-eabi-gcc`, `qemu-system-arm`, and an
ARM-capable gdb (`arm-none-eabi-gdb` or `gdb-multiarch`).
