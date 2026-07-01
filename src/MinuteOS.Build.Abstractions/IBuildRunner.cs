using MinuteOS.Build.Graph;

namespace MinuteOS.Build;

/// <summary>
/// The consumer entry point: builds a resolved configuration through the task-graph
/// engine. Resolve it from DI (<c>AddMinuteosBuild</c>) and hand it an
/// <see cref="IBuildConfiguration"/>.
/// </summary>
public interface IBuildRunner
{
    Task<bool> BuildAsync(IBuildConfiguration config, BuildOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Options for a build.</summary>
public sealed record BuildOptions
{
    /// <summary>Max parallel jobs (0 = processor count).</summary>
    public int Parallelism { get; init; }

    /// <summary>Suppress informational logging (only warnings/errors).</summary>
    public bool Quiet { get; init; }

    /// <summary>Fingerprint strategy for the action cache (null = mtime+size).</summary>
    public IFingerprinter? Fingerprinter { get; init; }
}
