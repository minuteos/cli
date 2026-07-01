namespace MinuteOS.Build;

/// <summary>
/// An external dependency providing a lib root (e.g. <c>lib</c>, <c>lib-arm</c>).
/// Three kinds, cheapest first:
///
///   # 1. path - point at an existing directory (a submodule checked out here or
///   #    shared elsewhere). Nothing is fetched.
///   - name: lib
///     path: ../shared/lib
///
///   # 2. tar - fetch a specific commit's tarball into a shared cache (no .git,
///   #    no full clone; content-addressed by commit).
///   - name: lib-arm
///     git: https://github.com/minuteos/lib-arm
///     commit: 0a1b2c3...
///
///   # 3. clone - a full git clone into the project.
///   - name: lib
///     git: https://github.com/minuteos/lib
///     ref: main
/// </summary>
public class Dependency
{
    /// <summary>Directory name / identity (also the default <see cref="Directory"/>).</summary>
    public string? Name { get; set; }

    /// <summary>Kind 1: an existing directory (relative to the project root, or absolute).</summary>
    public string? Path { get; set; }

    /// <summary>Clone URL (kind 2 with <see cref="Commit"/>, or kind 3).</summary>
    public string? Git { get; set; }

    /// <summary>Kind 2: the commit to fetch as a tarball (content-addressed cache key).</summary>
    public string? Commit { get; set; }

    /// <summary>Kind 2 (explicit): a tarball URL or local <c>.tar.gz</c> path.</summary>
    public string? Tar { get; set; }

    /// <summary>Kind 3: branch/tag/commit to check out when cloning.</summary>
    public string? Ref { get; set; }

    /// <summary>The dependency's identity for cache keys and clone dirs.</summary>
    public string Directory => Name ?? Path ?? throw new InvalidOperationException("dependency has no name or path");

    public DependencyKind Kind =>
        !string.IsNullOrEmpty(Path) ? DependencyKind.Path
        : !string.IsNullOrEmpty(Tar) || (!string.IsNullOrEmpty(Git) && !string.IsNullOrEmpty(Commit)) ? DependencyKind.Tar
        : DependencyKind.Clone;
}

public enum DependencyKind
{
    /// <summary>Reference an existing directory.</summary>
    Path,

    /// <summary>Fetch a commit tarball into a shared cache.</summary>
    Tar,

    /// <summary>Full git clone into the project.</summary>
    Clone,
}
