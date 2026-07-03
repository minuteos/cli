# Unified profiling timeline (`mtrace`)

`minuteos dap` can record a **single timeline** that combines the profiling
sources it already sees - DWT **PC samples**, ITM **log** output, and **SMU**
current/voltage measurements - into one compact, lossless binary stream. From
that one recording any display format (JSONL today, others later) is derived; the
binary is the source of truth.

## Why one host clock

The SWO stream carries no usable device timestamps (the DWT local-timestamp
frames are not correlated across sources), so the only time base shared by PC
samples, logs and the separate SMU channel is **host arrival time**. The
recorder stamps every event with a monotonic clock started at record start, so
events are ordered by when the adapter saw them - good enough to line power
draw up against code and logs, but not a cycle-accurate on-target clock.

## The binary format

Layout: a 16-byte header (`mtrc` magic, `u16` version, `u16` flags, `i64`
`startUnixNanos`) followed by records. Each record is

```
[deltaNs uvarint][tag u8][payload]
```

`deltaNs` is nanoseconds since the previous record (the clock is monotonic, so
delta-coding keeps timestamps to 1-3 bytes). PC values are delta-coded against
the previous PC and measurements against the channel's previous value, both as
zigzag varints, so nearby addresses and smoothly-varying current cost only a
byte or two. Records:

| Tag | Event | Payload |
|-----|-------|---------|
| `0x01` | PC sample | pc delta (zigzag varint) |
| `0x02` | PC sample, core sleeping | — |
| `0x03` | Log | port uvarint, length uvarint, bytes |
| `0x04` | Measurement | channel uvarint, value delta (zigzag varint) |
| `0x05` | Channel def | channel, kind `u8`, scale `f64`, name, unit |
| `0x06` | Mark | kind `u8`, text |

Tags `< 0x80` are core records a reader of the same major version must
understand; tags `>= 0x80` are length-prefixed extension records an older
reader skips. A measurement's SI value is `raw * channel.scale` - `raw` is kept
exactly, so no fidelity is lost regardless of the device's native units.

The implementation is `MinuteOS.Debug/Trace/` (`TraceWriter`, `TraceReader`,
`TraceRecorder`).

## Recording a session

Two custom DAP requests drive it:

- `minuteos.trace.start` `{ "path"?: string }` - begin recording (default
  `<cwd>/trace.mtrace`). Returns `{ path }`.
- `minuteos.trace.stop` - finalize. Returns `{ path, events }`.

Logs and SMU measurements are always captured; **PC events require PC sampling
to be enabled** (start profiling, or set `swo.profile`) so the DWT emits PC
packets. A recording still in progress is finalized on disconnect.

## SMU measurements (pluggable)

The SMU sample stream is behind `ISmuSampleSource`. Real STLINK-V3PWR streaming
acquisition (the `power_monitor` binary frame protocol) plugs in there and
shares the `SessionSmu` connection; until then `NullSmuSampleSource` stands in
and no power track is recorded. Everything downstream - channel definitions,
delta-coded samples, export - is already wired, so enabling real acquisition is
a localized change.

## Querying (timeline view)

The same data answers the two queries a timeline UI needs - a PC histogram (the
pie) and a downsampled power/log series (the chart) - through one query core
(`TimelineStore`), used both live and for replay.

**Live**, over the running session:

- `minuteos.timeline.start` / `.stop` - begin/stop retaining samples and ticking.
  `start` also turns on DWT PC sampling (reference-counted with the profiler, a
  no-op without SWO), so the pie populates without a separate profiling step.
- `minuteos.timeline` event `{ now }` - a ~250 ms tick prompting the client to pull.
- `minuteos.timeline.histogram` `{ from?, to?, granularity?, top? }` - PC buckets.
- `minuteos.timeline.series` `{ from?, to?, maxPoints? }` - power points + logs.

`granularity` is `function` (default), `line`, or `address`. Line resolution
comes from a DWARF `.debug_line` reader (`MinuteOS.Debug/Dwarf`), function from
`.symtab`.

**Replay**, over a saved recording (same shapes, no session needed):

```bash
minuteos trace histogram trace.mtrace --elf app.axf -g line [--from ns --to ns]
minuteos trace series    trace.mtrace [--max-points 600]
```

## Exporting

```bash
minuteos trace export trace.mtrace [--elf out/board/app.axf] [-o timeline.jsonl]
```

Emits one JSON event per line (`t` absolute ns, `dt` ns since start, `kind`, and
payload); `--elf` symbolicates PC samples to function names. Other export
targets (e.g. Perfetto) can be added as further `--format` values over the same
reader.
