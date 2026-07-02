using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Build;

/// <summary>
/// Restores a project's external dependencies (see <see cref="Dependency"/>):
/// initializes git submodules, verifies <c>path</c> dependencies, and fetches
/// <c>remote</c> dependencies as commit tarballs into a shared cache - never a
/// full clone. Mutable refs (branch/tag) are resolved to a commit via
/// <c>git ls-remote</c> at restore time and recorded in <see cref="DependencyLock"/>,
/// so builds are offline and deterministic between restores.
/// </summary>
public static partial class DependencyRestorer
{
    public sealed record DependencyResult(string Name, string Status, bool Ok);

    /// <summary>Shared tarball cache root (override with <c>MINUTEOS_CACHE</c>).</summary>
    public static string CacheRoot =>
        Environment.GetEnvironmentVariable("MINUTEOS_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "minuteos", "deps");

    private const string Unresolved = "_unresolved_";

    private static readonly HttpClient Http = new();

    /// <summary>
    /// The on-disk directory a dependency resolves to (used as a lib root).
    /// Mutable-ref remotes resolve through the lock file; before the first
    /// restore they point at a non-existent placeholder (=> reported missing).
    /// </summary>
    public static string ResolveDir(Dependency dep, string projectRoot)
    {
        if (dep.Kind == DependencyKind.Path)
        {
            var dir = dep.Path ?? dep.Name!;
            return Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(projectRoot, dir));
        }
        return Path.Combine(CacheRoot, Sanitize(dep.Directory), CacheKey(dep, projectRoot));
    }

    private static string CacheKey(Dependency dep, string projectRoot)
    {
        if (!string.IsNullOrEmpty(dep.Tar))
            return Sanitize(dep.Tar);
        if (dep.RefIsCommit)
            return dep.Ref!;
        return DependencyLock.Load(projectRoot).Get(dep.Directory) ?? Unresolved;
    }

    /// <param name="frozen">
    /// Fail instead of re-resolving a mutable ref: builds must use exactly the
    /// locked commits (CI safety). Pinned commits and paths are unaffected.
    /// </param>
    public static async Task<IReadOnlyList<DependencyResult>> RestoreAsync(
        ProjectConfig project, string projectRoot, ILogger logger, CancellationToken cancellationToken,
        bool frozen = false)
    {
        var results = new List<DependencyResult>();
        var locks = DependencyLock.Load(projectRoot);

        // Path dependencies are typically submodules - initialize them first.
        if (File.Exists(Path.Combine(projectRoot, ".gitmodules")))
        {
            logger.LogInformation("Initializing git submodules...");
            var (ok, _, err) = await GitAsync(projectRoot, ["submodule", "update", "--init", "--recursive"], cancellationToken);
            if (!ok)
                logger.LogWarning("git submodule update failed: {Error}", err.Trim());
        }

        foreach (var dep in project.Dependencies ?? [])
        {
            var name = dep.Directory;
            try
            {
                results.Add(dep.Kind == DependencyKind.Path
                    ? RestorePath(dep, projectRoot)
                    : await RestoreRemoteAsync(dep, locks, frozen, logger, cancellationToken));
            }
            catch (Exception ex)
            {
                results.Add(new(name, $"failed: {ex.Message}", false));
            }
        }

        locks.Save();
        return results;
    }

    private static DependencyResult RestorePath(Dependency dep, string projectRoot)
    {
        var dir = ResolveDir(dep, projectRoot);
        return IsPopulated(dir)
            ? new(dep.Directory, "present", true)
            : new(dep.Directory, $"missing: {dir} (initialize the submodule / provide the directory)", false);
    }

    private static async Task<DependencyResult> RestoreRemoteAsync(
        Dependency dep, DependencyLock locks, bool frozen, ILogger logger, CancellationToken cancellationToken)
    {
        var name = dep.Directory;

        // Determine the cache key: explicit tarball, pinned commit, or a mutable
        // ref resolved via ls-remote (falling back to the lock when offline).
        string key;
        string note = "";
        if (!string.IsNullOrEmpty(dep.Tar))
        {
            key = Sanitize(dep.Tar);
        }
        else if (dep.RefIsCommit)
        {
            key = dep.Ref!;
        }
        else if (frozen)
        {
            // Frozen: mutable refs must already be locked; never re-resolve.
            if (locks.Get(name) is not { } frozenCommit)
                return new(name, $"ref '{dep.Ref ?? "HEAD"}' is not locked (run restore without --frozen)", false);
            key = frozenCommit;
            note = $"frozen at {Short(frozenCommit)}";
        }
        else
        {
            var resolved = await LsRemoteAsync(dep.Git!, dep.Ref, cancellationToken);
            if (resolved != null)
            {
                locks.Set(name, resolved);
                key = resolved;
                note = $"{dep.Ref ?? "HEAD"} -> {Short(resolved)}";
            }
            else if (locks.Get(name) is { } locked)
            {
                key = locked;
                note = $"offline - using locked {Short(locked)}";
            }
            else
            {
                return new(name, $"cannot resolve ref '{dep.Ref ?? "HEAD"}' of {dep.Git}", false);
            }
        }

        var dir = Path.Combine(CacheRoot, Sanitize(name), key);
        if (IsPopulated(dir))
            return new(name, Join("cached", note), true);

        var url = TarUrl(dep, key);
        logger.LogInformation("Fetching {Name} tarball <- {Url}", name, url);
        await FetchTarballAsync(url, dir, cancellationToken);
        return new(name, Join("fetched", note), true);
    }

    /// <summary>Declared dependencies whose resolved directory is absent or empty.</summary>
    public static IReadOnlyList<string> MissingDependencies(ProjectConfig project, string projectRoot) =>
        (project.Dependencies ?? [])
            .Where(d => !IsPopulated(ResolveDir(d, projectRoot)))
            .Select(d => d.Directory)
            .ToList();

    // --- tarball fetch ---------------------------------------------------------

    private static string TarUrl(Dependency dep, string commit)
    {
        if (!string.IsNullOrEmpty(dep.Tar))
            return dep.Tar;

        var git = dep.Git!.TrimEnd('/');
        if (git.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            git = git[..^4];

        var gh = GitHubRegex().Match(git);
        if (gh.Success)
            return $"https://codeload.github.com/{gh.Groups[1].Value}/{gh.Groups[2].Value}/tar.gz/{commit}";

        return $"{git}/archive/{commit}.tar.gz"; // GitLab / Gitea style
    }

    private static async Task FetchTarballAsync(string url, string destDir, CancellationToken cancellationToken)
    {
        // Extract into a temp dir then move, so a failure never leaves a partial
        // cache entry. Archive entries nest under "<repo>-<ref>/"; strip that.
        var tmp = destDir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(tmp);
        try
        {
            await using var source = File.Exists(url)
                ? File.OpenRead(url)
                : await Http.GetStreamAsync(url, cancellationToken);
            await using var gz = new GZipStream(source, CompressionMode.Decompress);
            using var tar = new TarReader(gz);

            for (var entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken);
                 entry is not null;
                 entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken))
            {
                var rel = StripTopSegment(entry.Name);
                if (string.IsNullOrEmpty(rel))
                    continue;
                var outPath = Path.Combine(tmp, rel);
                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(outPath);
                }
                else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    await entry.ExtractToFileAsync(outPath, overwrite: true, cancellationToken);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destDir)!);
            Directory.Move(tmp, destDir);
        }
        catch
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    // --- ref resolution ---------------------------------------------------------

    /// <summary>
    /// Resolves a branch/tag (or HEAD when null) to a commit via
    /// <c>git ls-remote</c> - a single cheap request, no clone. Annotated tags
    /// prefer the peeled (<c>^{}</c>) commit. Null when unreachable/unknown.
    /// </summary>
    private static async Task<string?> LsRemoteAsync(string url, string? refName, CancellationToken cancellationToken)
    {
        var args = refName == null
            ? new List<string> { "ls-remote", url, "HEAD" }
            : ["ls-remote", url, refName, refName + "^{}"];

        var (ok, output, _) = await GitAsync(Environment.CurrentDirectory, args, cancellationToken);
        if (!ok)
            return null;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2)
            .ToList();

        // Peeled tag first (the commit an annotated tag points at), else the first match.
        var peeled = lines.FirstOrDefault(p => p[1].EndsWith("^{}", StringComparison.Ordinal));
        return (peeled ?? lines.FirstOrDefault())?[0];
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    private static string Join(string a, string b) => b.Length == 0 ? a : $"{a} ({b})";

    private static string StripTopSegment(string name)
    {
        var idx = name.IndexOf('/');
        return idx < 0 ? "" : name[(idx + 1)..];
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());

    private static bool IsPopulated(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    private static async Task<(bool Ok, string Out, string Err)> GitAsync(
        string cwd, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
            var o = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            var e = await p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);
            return (p.ExitCode == 0, o, e);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    [GeneratedRegex(@"github\.com[:/]([^/]+)/([^/]+)$")]
    private static partial Regex GitHubRegex();
}
