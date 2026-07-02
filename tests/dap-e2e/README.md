# `minuteos dap` end-to-end test

Drives the debug adapter (`minuteos dap`) with a scripted DAP client against
the [`cortex-m3-qemu`](../../examples/cortex-m3-qemu) example target, twice:

- **qemu**: initialize → launch `{config: qemu}` (resolved through the build
  system) → setBreakpoints → configurationDone → stopped(breakpoint) →
  threads/stackTrace/scopes → locals/globals/registers → evaluate →
  disassemble → step → continue → second hit → readMemory → semihosting
  output as `output` events → pause → disconnect.
- **renode** (skipped when renode is not installed): launch with the renode
  server (driven over its Telnet monitor), SWO via the ITM-capture overlay
  (the firmware's stimulus-port writes come back as `output` events), SVD
  peripheral scopes from a local `.svd` — a firmware-written "register" is
  read back with bitfield decode — and SWO **profiling**: the firmware emits
  DWT PC samples with a 3:1 skew between two functions, and
  `minuteos.profile.start`/`.stop` returns them symbolicated from the ELF
  with matching weights — then graceful teardown.

```bash
./run.sh              # scaffolds into a temp dir, builds, runs the client
./run.sh /some/dir    # keep the scaffolded project around for inspection
```

Requires `dotnet`, `python3`, `arm-none-eabi-gcc`, `qemu-system-arm`, and an
ARM-capable gdb (`arm-none-eabi-gdb` or `gdb-multiarch`); `renode` enables
the second scenario.
