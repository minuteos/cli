namespace MinuteOS.Build;

/// <summary>
/// An external dependency of a project - normally a git submodule providing a lib
/// root (e.g. <c>lib</c>, <c>lib-arm</c>). Declared in <c>minuteos.yaml</c> and
/// restored by <c>minuteos restore</c>.
///
///   dependencies:
///     - name: lib
///       git: https://github.com/minuteos/lib
///     - name: lib-arm
///       git: https://github.com/minuteos/lib-arm
///       ref: main
/// </summary>
public class Dependency
{
    /// <summary>Directory name under the project root (also the default <see cref="Path"/>).</summary>
    public string? Name { get; set; }

    /// <summary>Clone URL. Omit for a dependency that is only a pre-existing submodule.</summary>
    public string? Git { get; set; }

    /// <summary>Branch, tag, or commit to check out (optional).</summary>
    public string? Ref { get; set; }

    /// <summary>Directory (relative to the project root); defaults to <see cref="Name"/>.</summary>
    public string? Path { get; set; }

    /// <summary>The resolved directory name (Path, else Name).</summary>
    public string Directory => Path ?? Name ?? throw new InvalidOperationException("dependency has no name or path");
}
