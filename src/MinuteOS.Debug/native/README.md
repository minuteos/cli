# Embedded native libusb

libusb is embedded into the assembly so a single-file / NativeAOT `minuteos`
carries it, then extracted + loaded at runtime by `EmbeddedNativeLibrary`.

The binaries are **not committed**. CI fetches each platform's official libusb
into `native/<rid>/` before publishing (`eng/fetch-libusb.sh` /
`eng/fetch-libusb.ps1`, driven by `.github/workflows/aot.yml`):

| rid | file | source |
|-----|------|--------|
| linux-x64 / linux-arm64 | `libusb-1.0.so.0` | Debian/Ubuntu `libusb-1.0-0` (udev backend; needs system libudev/libcap) |
| osx-x64 / osx-arm64 | `libusb-1.0.dylib` | Homebrew `libusb` (IOKit backend) |
| win-x64 / win-arm64 | `libusb-1.0.dll` | official libusb release archive (WinUSB backend) |

A normal clone has none of these, so the build embeds nothing and the loader
falls back to a system-installed libusb. To build a self-contained binary
locally, run the fetch script for your RID first, e.g. `eng/fetch-libusb.sh linux-x64`.
