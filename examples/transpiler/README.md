# transpiler

Shows source generation: a `transpile` step turns `*.cs` into generated C++ that
is compiled and linked like any other source.

```bash
minuteos build -c host
minuteos run   -c host      # -> "generated symbol reports origin: src/greeting.cs"
```

How it flows through the build graph:

1. `scan:cs` discovers `src/greeting.cs` (a `kind=source, lang=cs` artifact).
2. `transpile` consumes the whole `.cs` set and **produces** generated
   `*.g.cpp` + a header dir (dynamic outputs).
3. `gcc:compile` picks up the generated `.cpp` (lazy fan-out) and adds the
   generated header dir to its include path.

Incremental: the transpiler only rewrites files whose content changed, so a
no-op rebuild recompiles nothing; adding a `.cs` compiles only the new unit.

The built-in `transpile` is a **stub** (it emits a marker symbol per input). A
real C#->C++ transpiler implements the same in-process step contract — see
`docs/writing-a-step.md`.
