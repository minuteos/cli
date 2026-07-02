#!/usr/bin/env bash
# End-to-end test for `minuteos dap`: debugs the cortex-m3 example under
# qemu-system-arm with a scripted DAP client.
#
# Requires: dotnet, python3, arm-none-eabi-gcc, qemu-system-arm, and a
# multi-arch gdb (arm-none-eabi-gdb or gdb-multiarch).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${1:-$(mktemp -d)}"

GDB="$(command -v arm-none-eabi-gdb || command -v gdb-multiarch)" \
    || { echo "no ARM-capable gdb found"; exit 1; }

echo "== Building minuteos CLI"
dotnet build "$REPO/src/MinuteOS.Cli" -v q --nologo
CLI="$REPO/src/MinuteOS.Cli/bin/Debug/net10.0/minuteos.dll"

echo "== Scaffolding test project in $WORK"
cp -r "$HERE/project/." "$WORK/"
mkdir -p "$WORK/lib"
cp -r "$REPO/examples/cortex-m3-qemu/targets" "$WORK/lib/targets"

echo "== Building test program"
(cd "$WORK" && dotnet "$CLI" build -c qemu -q)

echo "== Driving minuteos dap"
MINUTEOS_DLL="$CLI" MINUTEOS_GDB="$GDB" python3 "$HERE/dap_client.py" "$WORK"
