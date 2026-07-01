using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Build;

/// <summary>
/// Restores a project's external dependencies. Three kinds (see
/// <see cref="Dependency"/>): reference an existing <c>path</c>, fetch a commit
/// <c>tar</c>ball into a shared cache, or full <c>clone</c>. Also initializes git
/// submodules. Lib roots for the build come from
/// <see cref="ResolveDir"/> of each dependency plus any <c>lib*</c> dirs in the
/// project root.
/// </summary>
public static partial class DependencyRestorer
{
    public sealed record DependencyResult(string Name, string Status, bool Ok);

    /// <summary>Shared tarball cache root (override with <c>MINUTEOS_CACHE</c>).</summary>
    public static string CacheRoot =>
        Environment.GetEnvironmentVariable("MINUTEOS_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "minuteos", "deps");

    private static readonly HttpClient Http = new();

    /// <summary>The on-disk directory a dependency resolves to (used as a lib root).</summary>
    public static string ResolveDir(Dependency dep, string projectRoot) => dep.Kind switch
    {
        DependencyKind.Path => Path.GetFullPath(
            Path.IsPathRooted(dep.Path!) ? dep.Path! : Path.Combine(projectRoot, dep.Path!)),
        DependencyKind.Tar => Path.Combine(CacheRoot, Sanitize(dep.Directory), CacheKey(dep)),
        _ => Path.Combine(projectRoot, dep.Directory),
    };

    public static async Task<IReadOnlyList<DependencyResult>> RestoreAsync(
        ProjectConfig project, string projectRoot, ILogger logger, CancellationToken cancellationToken)
    {
        var results = new List<DependencyResult>();

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
            var dir = ResolveDir(dep, projectRoot);

            if (IsPopulated(dir))
            {
                results.Add(new(name, dep.Kind == DependencyKind.Tar ? "cached" : "present", true));
                continue;
            }

            try
            {
                switch (dep.Kind)
                {
                    case DependencyKind.Path:
                        // Nothing to fetch - the directory must be provided (e.g. an
                        // uninitialized submodule).
                        results.Add(new(name, $"missing path: {dir}", false));
                        break;

                    case DependencyKind.Tar:
                        var url = TarUrl(dep);
                        logger.LogInformation("Fetching {Name} tarball <- {Url}", name, url);
                        await FetchTarballAsync(url, dir, cancellationToken);
                        results.Add(new(name, "cached", true));
                        break;

                    default:
                        logger.LogInformation("Cloning {Name} <- {Git}", name, dep.Git);
                        var args = new List<string> { "clone" };
                        if (!string.IsNullOrEmpty(dep.Ref))
                            args.AddRange(["--branch", dep.Ref]);
                        args.AddRange([dep.Git!, dir]);
                        var (ok, _, err) = await GitAsync(projectRoot, args, cancellationToken);
                        results.Add(new(name, ok ? "cloned" : $"clone failed: {err.Trim()}", ok));
                        break;
                }
            }
            catch (Exception ex)
            {
                results.Add(new(name, $"failed: {ex.Message}", false));
            }
        }

        return results;
    }

    /// <summary>Declared dependencies whose resolved directory is absent or empty.</summary>
    public static IReadOnlyList<string> MissingDependencies(ProjectConfig project, string projectRoot) =>
        (project.Dependencies ?? [])
            .Where(d => !IsPopulated(ResolveDir(d, projectRoot)))
            .Select(d => d.Directory)
            .ToList();

    // --- tarball fetch ---------------------------------------------------------

    private static string TarUrl(Dependency dep)
    {
        if (!string.IsNullOrEmpty(dep.Tar))
            return dep.Tar;

        var git = dep.Git!.TrimEnd('/');
        if (git.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            git = git[..^4];

        var gh = GitHubRegex().Match(git);
        if (gh.Success)
            return $"https://codeload.github.com/{gh.Groups[1].Value}/{gh.Groups[2].Value}/tar.gz/{dep.Commit}";

        return $"{git}/archive/{dep.Commit}.tar.gz"; // GitLab / Gitea style
    }

    private static async Task FetchTarballAsync(string url, string destDir, CancellationToken cancellationToken)
    {
        // Extract into a temp dir then move, so a failure never leaves a partial
        // cache entry. GitHub tarballs nest under "<repo>-<ref>/"; strip that.
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

    private static string StripTopSegment(string name)
    {
        var idx = name.IndexOf('/');
        return idx < 0 ? "" : name[(idx + 1)..];
    }

    private static string CacheKey(Dependency dep) =>
        !string.IsNullOrEmpty(dep.Commit) ? dep.Commit : Sanitize(dep.Tar ?? "tar");

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
