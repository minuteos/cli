# Examples

Runnable projects demonstrating the minuteOS build system. See [`../docs`](../docs)
for the concepts.

| Example | Shows | Run |
|---------|-------|-----|
| [`hello-host`](hello-host) | the smallest project | `minuteos run -c host -p examples/hello-host` |
| [`transpiler`](transpiler) | source generation (`*.cs` → C++) | `minuteos run -c host -p examples/transpiler` |
| [`custom-step`](custom-step) | a custom step via the library + DI | `dotnet run --project examples/custom-step -- examples/custom-step/project` |
| [`cortex-m3-qemu`](cortex-m3-qemu) | an ARM target that runs tests under QEMU/Renode | (drop the target into a project; see its README) |

The `host` examples need `gcc`/`g++`. `cortex-m3-qemu` needs `arm-none-eabi-gcc`
plus `qemu-system-arm` and/or `renode`.
