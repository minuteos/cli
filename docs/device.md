# Device management (flash, erase, debug)

Device operations follow the same pattern as [run steps](configuration.md#run-steps):
a **board target declares how to perform each operation on its hardware** as
`Device`-phase steps, and the tool stays probe-agnostic — openocd, pyOCD,
st-flash, J-Link, dfu-util, or anything else is just a command the target
configures.

```yaml
# targets/my-board/target.yaml
steps:
  - name: flash
    phase: Device
    config:
      command: openocd
      args: '-f interface/stlink.cfg -f target/stm32f4x.cfg [-c "adapter serial {device}"] -c "program {image} verify reset exit"'

  - name: erase
    phase: Device
    config:
      command: pyocd
      args: 'erase --chip -t stm32f405rg [--probe {device}]'

  - name: gdb-server
    phase: Device
    config:
      command: openocd
      args: '-f interface/stlink.cfg -f target/stm32f4x.cfg'
      gdb-port: "3333"
      # gdb: arm-none-eabi-gdb   # client override (default: <toolchain-prefix>gdb)
```

Any configuration using that target inherits the operations; a config-level step
overrides the target's (most specific wins).

## Commands

| Command | What it does |
|---------|--------------|
| `minuteos flash [-c cfg] [-d serial]` | Build the configuration, then run its `flash` step. |
| `minuteos erase [-c cfg] [-d serial]` | Run the `erase` step (no build). |
| `minuteos debug [-c cfg]` | Build, start the `gdb-server` step in the background, attach `<toolchain-prefix>gdb {image}` via `target extended-remote localhost:<gdb-port>`, and kill the server when gdb exits. |
| `minuteos debug --server-only` | Only run the gdb server in the foreground — for an IDE / editor extension to attach to. |

All run with live, interactive stdio (you see the probe tool's output; gdb is
fully interactive).

## Placeholders

Substituted in `args`:

| Placeholder | Value |
|-------------|-------|
| `{image}` / `{binary}` | the primary output (e.g. `out/board/app.axf`) |
| `{image-base}` | the primary output without extension — `{image-base}.bin` selects a `gcc:objcopy` product |
| `{image-dir}` | the output directory |
| `{name}` | the output name |
| `{device}` | the CLI `--device`/`-d` value (probe serial / selector) |
| `{port}` | the step's `gdb-port` (default 3333) |

An **optional group** `[...]` is dropped wholesale when a placeholder inside it
resolves empty — so `[--serial {device}]` disappears cleanly when no `-d` is
given, instead of leaving a dangling flag.

## IDE / extension integration

`minuteos debug --server-only` is the attach point for an editor extension: it
builds, starts the configured gdb server on `gdb-port`, and stays in the
foreground until stopped. The extension (or a `launch.json`) then connects its
own debug adapter to `localhost:<gdb-port>` with the ELF at the path printed by
`minuteos info` (`Output:`). Programmatic consumers can resolve the same
information via `DeviceSpecResolver.Resolve(config, "gdb-server", image)`.
