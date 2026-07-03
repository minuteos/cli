using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug;

/// <summary>
/// Loads a native library that ships embedded in this assembly, so a
/// single-file / NativeAOT build carries it without a system install. The
/// per-platform binary (built without udev, so it needs nothing but libc) is
/// embedded as a resource; on first use it is extracted to a content-addressed
/// cache directory and <see cref="NativeLibrary.Load(string)"/>ed under its
/// real soname. Once resident, any subsequent P/Invoke or resolver that asks
/// for the same soname binds to this copy - so the actual DllImports (in
/// LibUsbDotNet) are left untouched. Best-effort: if the current platform has
/// no embedded copy, or extraction fails, it no-ops and normal resolution
/// (a system-installed library) takes over.
/// </summary>
public static class EmbeddedNativeLibrary
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Loaded = [];

    /// <summary>
    /// Ensures the embedded copy of <paramref name="baseName"/> (e.g.
    /// <c>"libusb-1.0"</c>) is resident. Idempotent and exception-safe.
    /// </summary>
    public static void Ensure(string baseName, ILogger logger)
    {
        lock (Gate)
        {
            if (!Loaded.Add(baseName))
                return;

            try
            {
                if (!TryLoad(baseName, logger))
                    logger.LogDebug("No embedded {Name} for {Rid}; falling back to system resolution",
                        baseName, Rid());
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Embedded {Name} load failed; falling back to system resolution", baseName);
            }
        }
    }

    /// <summary>
    /// The runtime identifier, derived from OS + process architecture rather
    /// than <see cref="RuntimeInformation.RuntimeIdentifier"/>, which is empty
    /// under NativeAOT.
    /// </summary>
    private static string Rid()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private static bool TryLoad(string baseName, ILogger logger)
    {
        // Platform-specific on-disk file name (its soname / DLL name).
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{baseName}.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"{baseName}.dylib"
            : $"{baseName}.so.0";

        // Resource: MinuteOS.Debug.native.<rid>.<fileName>
        var assembly = typeof(EmbeddedNativeLibrary).Assembly;
        var resource = $"MinuteOS.Debug.native.{Rid()}.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream == null)
            return false;

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        // Content-addressed cache path: upgrades don't collide, and a second
        // process reuses the same file.
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        var dir = Path.Combine(
            Environment.GetEnvironmentVariable("MINUTEOS_CACHE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "minuteos"),
            "native", hash);
        var path = Path.Combine(dir, fileName);

        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
        {
            Directory.CreateDirectory(dir);
            // Write to a temp file then move, so a concurrent reader never sees a partial file.
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException) { /* lost the race; the other writer's copy is fine */ }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        NativeLibrary.Load(path);
        logger.LogDebug("Loaded embedded {File} from {Path}", fileName, path);
        return true;
    }
}
