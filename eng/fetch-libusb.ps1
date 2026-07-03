# Fetch the official libusb DLL for a Windows RID into
# src/MinuteOS.Debug/native/<rid>/, for CI to embed into a single-file /
# NativeAOT publish. The binaries are not committed. Uses the official libusb
# release archive from GitHub (reachable on GitHub-hosted runners).
#
# Usage: pwsh eng/fetch-libusb.ps1 -Rid win-x64   # win-x64 | win-arm64
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$Version = '1.0.27'
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path "$PSScriptRoot/..").Path
$dest = Join-Path $root "src/MinuteOS.Debug/native/$Rid"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$archive = Join-Path $tmp "libusb-$Version.7z"
$extract = Join-Path $tmp "libusb-$Version"
$url = "https://github.com/libusb/libusb/releases/download/v$Version/libusb-$Version.7z"

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $archive
& 7z x $archive "-o$extract" -y | Out-Null

# The release ships per-toolchain/arch DLLs; pick the VS build for this arch.
$archMatch = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'MS64|x64' }
$dll = Get-ChildItem -Path $extract -Recurse -Filter 'libusb-1.0.dll' |
    Where-Object { $_.FullName -match 'VS' -and $_.FullName -match "(\\|/)($archMatch)(\\|/)" } |
    Select-Object -First 1
if (-not $dll) {
    throw "libusb-1.0.dll for $Rid not found under $extract"
}

Copy-Item $dll.FullName (Join-Path $dest 'libusb-1.0.dll') -Force
Write-Host "libusb for ${Rid}: $($dll.FullName)"
Get-ChildItem $dest
