namespace MinuteOS.Build.Graph;

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
