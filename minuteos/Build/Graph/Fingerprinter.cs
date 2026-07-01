using System.Security.Cryptography;

namespace MinuteOS.Cli.Build.Graph;

/// <summary>
/// Computes an opaque fingerprint of a file for the action cache. The fingerprint
/// format is private to the implementation, so the strategy can be swapped without
/// changing the cache format (docs/design/task-graph.md).
/// </summary>
public interface IFingerprinter
{
    /// <summary>A stable fingerprint of the file's current state (the file exists).</summary>
    string Compute(string path);
}

/// <summary>
/// Fast default: last-write time + size. Cheap (no read), and adequate for a build
/// where inputs change through the tool. Can miss a same-size edit that preserves
/// the mtime - rare in practice; use <see cref="ContentHashFingerprinter"/> if it
/// matters.
/// </summary>
public sealed class MtimeSizeFingerprinter : IFingerprinter
{
    public string Compute(string path)
    {
        var fi = new FileInfo(path);
        return $"m:{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
    }
}

/// <summary>
/// Content SHA-256. Robust (mtime-independent: a touch with no content change does
/// not rebuild; a same-size edit does), at the cost of reading every input.
/// </summary>
public sealed class ContentHashFingerprinter : IFingerprinter
{
    public string Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return "s:" + Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
