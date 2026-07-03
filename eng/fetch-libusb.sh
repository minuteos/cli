#!/usr/bin/env bash
#
# Fetch the official libusb for a RID into src/MinuteOS.Debug/native/<rid>/, for
# CI to embed into a single-file / NativeAOT publish. The binaries are not
# committed; a normal clone builds with none embedded and the loader
# (EmbeddedNativeLibrary) falls back to a system libusb.
#
# Runs on the target-arch runner (so the native package manager yields the right
# arch). Windows uses fetch-libusb.ps1 instead.
#
# Usage: eng/fetch-libusb.sh <rid>     # linux-x64 | linux-arm64 | osx-x64 | osx-arm64
set -euo pipefail

RID="${1:?usage: fetch-libusb.sh <rid>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/src/MinuteOS.Debug/native/$RID"
mkdir -p "$DEST"

case "$RID" in
  linux-x64|linux-arm64)
    # Official Debian/Ubuntu package (built with the udev backend; the extracted
    # copy resolves libudev/libcap from the system at load time).
    sudo apt-get update -qq
    sudo apt-get install -y --no-install-recommends libusb-1.0-0
    triplet=$([ "$RID" = linux-arm64 ] && echo aarch64-linux-gnu || echo x86_64-linux-gnu)
    cp -L "/usr/lib/$triplet/libusb-1.0.so.0" "$DEST/libusb-1.0.so.0"
    ;;
  osx-x64|osx-arm64)
    # Official Homebrew build (IOKit backend; no udev).
    brew install libusb >/dev/null
    cp -L "$(brew --prefix libusb)/lib/libusb-1.0.0.dylib" "$DEST/libusb-1.0.dylib"
    ;;
  *)
    echo "unsupported RID for this script: $RID (Windows uses fetch-libusb.ps1)" >&2
    exit 1
    ;;
esac

echo "libusb for $RID:"
ls -l "$DEST"
