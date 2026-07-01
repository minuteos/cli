namespace MinuteOS.Build;

/// <summary>
/// An external dependency providing a lib root (e.g. <c>lib</c>, <c>lib-arm</c>).
/// Two kinds:
///
///   # 1. path - an existing directory, typically a git submodule. Git pins the
///   #    version; `restore` initializes submodules. `path` defaults to `name`.
///   - name: lib
///   - name: lib-shared
///     path: ../shared/lib
///
///   # 2. remote - fetched as a commit tarball into a shared cache (never a full
///   #    clone). `ref` is a commit SHA, branch, or tag (default: HEAD). Mutable
///   #    refs are resolved to a commit at restore time and recorded in
///   #    minuteos.lock; builds are offline.
///   - name: lib-arm
///     git: https://github.com/minuteos/lib-arm
///     ref: main
/// </summary>
public class Dependency
{
    /// <summary>Dependency identity; also the default directory for path deps.</summary>
    public string? Name { get; set; }

    /// <summary>Path kind: the directory (relative to the project root, or absolute). Defaults to <see cref="Name"/>.</summary>
    public string? Path { get; set; }

    /// <summary>Remote kind: the repository URL (fetched as a tarball, never cloned).</summary>
    public string? Git { get; set; }

    /// <summary>Remote kind: commit SHA, branch, or tag (default: HEAD).</summary>
    public string? Ref { get; set; }

    /// <summary>Remote kind (explicit): a tarball URL or local <c>.tar.gz</c> path.</summary>
    public string? Tar { get; set; }

    /// <summary>The dependency's identity (for cache keys, the lock file, and messages).</summary>
    public string Directory => Name ?? Path ?? throw new InvalidOperationException("dependency has no name or path");

    public DependencyKind Kind =>
        !string.IsNullOrEmpty(Git) || !string.IsNullOrEmpty(Tar) ? DependencyKind.Remote : DependencyKind.Path;

    /// <summary>
    /// True when <see cref="Ref"/> is an (abbreviated) commit SHA - immutable, so it
    /// is its own cache key and needs no resolution. Anything else (branch, tag) is
    /// mutable and resolved to a commit at restore time.
    /// </summary>
    public bool RefIsCommit =>
        Ref is { Length: >= 7 and <= 40 } r && r.All(Uri.IsHexDigit);
}

public enum DependencyKind
{
    /// <summary>An existing directory (typically a submodule).</summary>
    Path,

    /// <summary>Fetched as a commit tarball into the shared cache.</summary>
    Remote,
}
