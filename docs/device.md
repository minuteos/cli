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

## Black Magic Probe: the minute-debug workflow

The [minute-debug](https://github.com/minuteos/vs-debugger) extension's primary
probe is the [Black Magic Probe](https://black-magic.org/) — the probe **is**
the gdb server on a serial port, and flashing goes through gdb itself. A board
that sets `debug.server: bmp` gets the same mechanism from the CLI:

```yaml
# targets/my-board/target.yaml
settings:
  debug.server: bmp
  # bmp.port: /dev/ttyACM0     # autodetected via /dev/serial/by-id when omitted
  # bmp.power: "true"           # probe-supplied target power (monitor tpwr)
```

- `flash` → `gdb --batch` with `target extended-remote <port>`,
  `monitor swdp_scan`, `attach 1`, `load` (gdb's exit detaches and the target
  runs) — exactly the extension's `flash()` flow.
- `erase` → the same session with `monitor erase_mass`.
- `debug` → attaches gdb directly to the probe (there is no server process).

## SMU: target power (STLINK-V3PWR)

The extension's SMU support (source-measure units; currently the STLINK-V3PWR)
is ported as `minuteos power`:

```yaml
settings:
  smu.type: stlink
  # smu.port: /dev/ttyACM2     # autodetected (control channel, USB interface 1)
  # smu.output: vout            # default
  # smu.voltage: "3.3"           # default
  # smu.start-power-on: "true"  # used by the generated launch config
  # smu.stop-power-off: "true"
```

```bash
minuteos power on          # volt vout 3300m; pwr vout on
minuteos power off [-c cfg] [--voltage 3.3] [--output vout] [--port ...]
```

The driver speaks the V3PWR's line protocol (`power_monitor`,
`format bin_hexa`, `volt <out> <mV>m`, `pwr <out> on|off`, `ack` responses) over
a raw tty — a direct port of the extension's driver.

## J-Link (legacy workflow)

The Make-era workflow was J-Link based, with `JLINK_DEVICE` as the single
per-board knob. That is still supported: a board that sets **`jlink.device`**
gets all three operations synthesized — no command lines to write:

```yaml
# targets/my-board/target.yaml
settings:
  jlink.device: EFR32MG12P332F1024GL125
  # jlink.interface: SWD        # default
  # jlink.speed: "4000"          # default
  # jlink.swo-frequency: "1000000"  # used by `minuteos vscode`
```

- `flash` → `JLinkExe -Device … -CommanderScript` (`loadfile {image}; r; g; qc`)
- `erase` → `JLinkExe … -CommanderScript` (`erase; qc`)
- `gdb-server` → `JLinkGDBServer -Device … -Port {port}`
- `-d <serial>` selects among multiple probes (`-SelectEmuBySN`)

Commander scripts are written (substituted) to `out/<cfg>/<op>.device-script`,
so what ran is always inspectable. An explicit `flash`/`erase`/`gdb-server`
step overrides the synthesized default (e.g. to use openocd instead). A step's
`script:` block plus `{script}` in `args` gives the same command-file mechanism
to any tool.

## VS Code integration

The tool is the **single source of truth** for debug configuration:
`minuteos info -c <cfg> --json` emits the resolved minute-debug launch model
(program, gdb, server, smu, svd, the full settings bag) for the extension to
consume at debug time — see
[debugger integration](design/debugger-integration.md). `minuteos vscode
--slim` emits launch entries that are just `{ "config": "<name>" }` references
(requires extension support); the default emits fully inline entries.

`minuteos vscode` generates `.vscode/`:

- **launch.json** — `Launch <cfg>` / `Attach <cfg>` entries:
  - **`minute-debug`** for configurations with `debug.server` — server
    (`bmp`/`qemu`/`renode`, inline when `bmp.port`/`bmp.power`/`qemu.machine`…
    are set), `smu` from the `smu.*` settings, `svd` from `debug.svd`,
    `smartLoad` (on unless `debug.smart-load: "false"`), build `preLaunchTask`.
  - **`cortex-debug`** (servertype jlink + SWO console) for configurations with
    `jlink.device` — the port of the Make-era `VSCode.mk`.
- **tasks.json** — `minuteos: build <cfg>` tasks (first configuration is the
  default build task).
- **c_cpp_properties.json** — IntelliSense from the generated
  `compile_commands.json` (always refreshed; launch/tasks are kept unless
  `--force`).

## Commands

| Command | What it does |
|---------|--------------|
| `minuteos flash [-c cfg] [-d serial]` | Build the configuration, then run its `flash` step. |
| `minuteos erase [-c cfg] [-d serial]` | Run the `erase` step (no build). |
| `minuteos debug [-c cfg]` | Build, start the `gdb-server` step in the background, attach `<toolchain-prefix>gdb {image}` via `target extended-remote localhost:<gdb-port>`, and kill the server when gdb exits. |
| `minuteos debug --server-only` | Only run the gdb server in the foreground — for an IDE / editor extension to attach to. |
| `minuteos power on\|off` | Drive target power via the configured SMU (STLINK-V3PWR). |

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
