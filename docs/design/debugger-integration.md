# Integrating the tool with minute-debug (no duplicated code)

Status: **phases 1 and 3 are implemented in this repo** — the full debug
engine is ported; what remains is the extension-side swap to
`DebugAdapterExecutable` in [minuteos/vs-debugger].

## The problem

The CLI's device layer was ported *from* the extension, so today two
implementations of the same logic exist in two languages:

| Logic | Extension (TS) | CLI (C#) |
|---|---|---|
| Config schema (server/smu/swo/svd/smartLoad) | `configuration.ts` | `debug.*`/`smu.*` settings + `vscode` gen |
| BMP flash / attach flow | `probe/*.ts` | `Bmp.cs` + device specs |
| STLINK-V3PWR SMU driver | `smu/drivers/stlink.ts` | `StlinkSmu.cs` |
| qemu/renode server drivers | `gdb-server/drivers/*` | run/device steps |
| GDB/MI + DAP session | `gdb/*`, `debug-adapter/*` | — |

Left alone, these drift. The languages differ, so "share a library" is not
directly available — the sharing boundary must be a **process boundary**, and
the question is where to put it.

## The end-state (matches the stated direction: debugging moves into the tool)

**The CLI owns the entire device + debug engine; the extension becomes a thin
VS Code frontend.**

- `minuteos dap` — a Debug Adapter Protocol server over stdio, implemented in
  `MinuteOS.Debug` (a new library beside `MinuteOS.Build`): the GDB/MI layer,
  probe drivers (BMP/qemu/renode), SMU, SWO decoding, smart-load. This is a
  port of the extension's `gdb/`, `gdb-server/`, `probe/`, `smu/`, `swo/`, and
  `debug-adapter/` — done **once**, after which the TS copies are deleted.
- The extension keeps only what must live in VS Code: the debugger
  contribution, a `DebugAdapterDescriptorFactory` that spawns `minuteos dap`
  (it already has `descriptor-factory.ts` — swapping to
  `DebugAdapterExecutable` is a few lines), the SVD tree UI, the Renode
  framebuffer webview, and the SWO console rendering — all fed through DAP
  custom events/requests from the adapter.
- Nice side effect: the Renode plugin sources the extension carries
  (`renode-framebuffer.cs`, `renode-itm.cs`) are **already C#** (they are
  injected into Renode, a .NET app) — they move to the CLI repo with no
  translation at all.
- The CLI's `flash`/`debug`/`power` commands and the extension then share one
  implementation by construction; headless debugging (`minuteos dap` under any
  DAP client, or scripted MI) comes for free.

This is the only option that actually removes the duplication rather than
managing it. It is also the largest (the DAP session + MI layer are ~2–3k
lines of TS to port), so it lands in phases; each phase deletes some TS.

## Phases

### Phase 1 — configuration comes from the tool (implemented)

The most damaging duplication is the **configuration schema**: the extension's
launch config and the CLI's settings describe the same thing, and
`minuteos vscode` bakes a *snapshot* into launch.json that goes stale when the
project changes.

Fix: the tool is the single source of truth, queryable at debug time:

- `minuteos info -c <cfg> --json` emits the resolved debug model — the exact
  shape of the extension's `InputLaunchConfiguration`:

  ```json
  {
    "name": "board",
    "program": "out/board/app.axf",
    "cwd": "/abs/project",
    "gdb": "arm-none-eabi-gdb",
    "server": { "type": "bmp", "power": true },
    "smu": { "type": "stlink", "voltage": 3.3, "startPowerOn": true },
    "svd": "EFR32MG12P",
    "smartLoad": true,
    "settings": { "...": "the full bag, for anything else" }
  }
  ```

- Extension side (small change in `configuration.ts`'s
  `expandConfiguration`): support `{ "type": "minute-debug", "config": "board" }`
  — when `config` is present, run `minuteos info -c board --json` in the
  workspace folder and merge the result under the explicit launch properties
  (explicit wins). Sketch:

  ```ts
  if (input.config) {
    const json = await execFile('minuteos', ['info', '-c', input.config, '--json'], { cwd })
    input = { ...JSON.parse(json.stdout), ...input }
  }
  ```

- `minuteos vscode` then emits the tiny reference form for minute-debug
  entries once the extension supports it (`--slim` today, default later):

  ```json
  { "name": "Launch board", "type": "minute-debug", "request": "launch",
    "config": "board", "preLaunchTask": "minuteos: build board" }
  ```

  Nothing in launch.json can go stale; board settings changes flow through
  automatically.

### Phase 2 — one-shot device operations delegate to the tool

The extension's standalone `flash()` API (exposed for other extensions) and
its power control duplicate `minuteos flash` / `minuteos power`. Once the
extension depends on the CLI being present (phase 1 already assumes it for
config), its non-session operations become thin wrappers that spawn the CLI —
and `probe/flash.ts` + the standalone SMU paths are deleted. In-session logic
(the DAP session's own load / SMU lifecycle) stays in TS until phase 3.

### Phase 3 — `minuteos dap` (the end-state above)

Port `gdb/mi*`, the server drivers, the SMU/SWO plumbing, and
`debug-adapter/session.ts` into `MinuteOS.Debug`; expose `minuteos dap`.
The extension drops to the frontend described above and its `src/` shrinks to
UI + descriptor. From then on there is exactly one device/debug codebase.

**Implemented.** `MinuteOS.Debug` now contains:

- `Mi/MiParser` + `Mi/MiClient` — the GDB/MI layer (`gdb --interpreter=mi2`,
  token-prefixed one-at-a-time commands, async record routing, per-thread
  run-state tracking with awaitable stopped/not-stopped gates) — the port of
  `gdb/mi.ts` + `gdb/instance.ts`.
- `Servers/` — the `GdbServer` contract plus three drivers, the port of
  `gdb-server/`:
  - **qemu**: `qemu-system-arm ... -gdb tcp:<port> -kernel <program> -S`,
    skipLoad;
  - **BMP**: serial-port autodetect, `monitor tpwr` / `swdp_scan` /
    `attach 1` / `uid`;
  - **renode**: `renode --disable-gui -p -P <port>` driven over its Telnet
    monitor (`RenodeMonitor` + a Telnet option filter), script/commands/
    machine selection, `machine StartGdbServer`, graceful `quit` on teardown,
    and the include/overlay hooks (`i @file`,
    `machine LoadPlatformDescription`) the SWO and framebuffer bridges use.
    The Renode plugin peripherals (`MinuteItmCapture`, `MinuteRomTable`,
    `MinuteFramebuffer`) are already C# — they moved over verbatim as
    embedded resources, compiled *by Renode* at runtime.
- `Probe` — gdb + server started in parallel, `target-select extended-remote`,
  attach, and the smart-load-aware `load` — the port of `probe/`.
- `Cortex` — ROM-table discovery, DEMCR vector catch (exception breakpoints),
  and the DWT/ITM/TPIU trace setup — the port of `gdb/cortex.ts`.
- `Swo/` — the incremental ITM/DWT packet decoder (`SwoParser`), the session
  that configures trace and drains the stream (`SwoSession`), and the sources:
  **renode** (ITM capture + ROM table overlaid onto the user's platform, the
  byte stream received over a loopback socket) and **BMP** (`swo enable` /
  `traceswo enable` probed via `monitor help`; the stream is read from a
  device path — automatic USB interface claim needs a USB stack and stays
  extension-side for now). Stimulus port 0 becomes DAP `output` events.
- `Svd/` — the CMSIS-SVD parser (`derivedFrom` resolution, register-size
  inheritance, all three field bit-range notations), the on-disk cache over
  the `cmsis-svd/cmsis-svd-data` index (x-wildcard model matching, largest
  match wins; local `.svd` paths parse directly with no network), and the
  DAP peripheral scopes: grouped peripherals → registers (block reads via
  gdb) → decoded bitfields, ANSI-colored like the extension.
- `Dap/DapConnection` + `Dap/DebugSession` — the DAP server:
  launch/attach (with `{config: name}` resolution through the build system),
  breakpoints (source/instruction/exception), threads/stack/scopes,
  varobj-based locals/globals/expansion, registers, SVD peripherals,
  evaluate (incl. `>console` and `-raw-mi` REPL passthrough), stepping,
  pause, readMemory, disassemble (via the ported range-caching
  `DisassemblyCache`), and the stopped/continued/thread/output event flow —
  the port of `debug-adapter/session.ts` + `disassembly.ts`.
- `minuteos dap` — the whole thing over stdio (stdout is the protocol;
  diagnostics go to stderr).

The end-to-end flow is validated by `tests/dap-e2e/` — a Python DAP client
driving `minuteos dap` against the `cortex-m3-qemu` example twice: under
**qemu** (breakpoint hit, locals/globals/registers, evaluate, disassembly,
step, semihosting output as DAP output events, pause, clean teardown) and
under **renode** (Telnet-monitor-driven server, ITM stimulus writes decoded
into output events through the overlay, SVD peripheral scope reading a
firmware-written register with bitfield decode, graceful quit).

## Crossing the in-process boundary

The extension today runs its DAP session **in-process**
(`DebugAdapterInlineImplementation`), which lets it call VS Code APIs directly
from the session. Moving the session behind `minuteos dap` puts a process
boundary there, so each of those direct calls needs a DAP-shaped path:

| In-process use | Out-of-process path (implemented unless noted) |
|---|---|
| `vscode.debug.activeDebugConsole.append(...)` (SWO ch0, console replies) | standard `output` events — SWO stimulus port 0 and server/gdb output arrive this way |
| `progress(...)` during load/flash | standard `progressStart`/`progressUpdate`/`progressEnd` events (the client advertises `supportsProgressReporting`) — not wired yet; load currently reports via `output` |
| SVD tree view / peripheral UI | the adapter serves SVD *as DAP scopes* (Peripherals → registers → bitfields), so a stock DAP client gets the tree for free; the on-disk SVD cache under `~/.cache/minuteos/svd` is shareable with the extension |
| Renode framebuffer webview | already a socket side-channel today — the adapter overlays the tap, relays the frame stream on a local port, and announces it with a `minuteos.display` custom event `{host, port}` |
| config expansion + presets | resolved inside the adapter (`{config: name}` → build system), so the frontend needs no logic at all |

Nothing in the session actually *requires* being in-process — the inline
implementation was a convenience. The remaining step is entirely
extension-side: swap the descriptor factory
(`DebugAdapterInlineImplementation` → `DebugAdapterExecutable('minuteos',
['dap'])`), render the `minuteos.display` stream in the webview, and delete
the ported TS. One engine-side gap remains: BMP SWO capture reads from a
device path rather than claiming the USB trace interface itself (that needs a
user-space USB stack; the extension keeps doing it in-process until then, or
the path can be provided by udev).

## Why not the inverse (extension as the engine, CLI delegates)?

- The CLI must work headless/CI without VS Code or Node.
- The build system (the config source of truth) is already here; debugging
  needs the build's outputs and settings, not vice versa.
- The stated direction is debugging moving *into* the tool.

[minuteos/vs-debugger]: https://github.com/minuteos/vs-debugger
