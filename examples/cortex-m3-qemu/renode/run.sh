#!/usr/bin/env bash
#
# Renode test-runner adapter for minuteos.
#
# minuteos's test-runner contract is "run {binary}, print the test output to
# stdout". Renode instead drives a machine from a script and routes semihosting
# output to a UART peripheral. This wrapper bridges the two: it generates a
# throwaway script that loads the binary, captures semihosting output to a temp
# file, runs the emulation, and then echoes that output to stdout.
#
# Usage: run.sh <path-to-elf>
#
set -euo pipefail

BIN="$1"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

OUT="$(mktemp)"
RESC="$(mktemp --suffix=.resc)"
trap 'rm -f "$OUT" "$RESC"' EXIT

cat > "$RESC" <<EOF
mach create
machine LoadPlatformDescription @${DIR}/lm3s.repl
sysbus LoadELF @${BIN}
sysbus.cpu.semihostingUart CreateFileBackend @${OUT} true
emulation RunFor "0.5"
quit
EOF

renode --disable-xwt --console --plain "$RESC" >/dev/null 2>&1 || true

cat "$OUT"
