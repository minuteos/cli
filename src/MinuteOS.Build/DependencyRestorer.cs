using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Build;

/// <summary>
/// Restores a project's external dependencies: initializes git submodules and
/// clones any declared dependency that is missing and has a git URL. lib roots
/// (<c>lib</c>, <c>lib-arm</c>, ...) are normally submodules; this makes a fresh
/// checkout buildable without a manual <c>git submodule update</c>.
/// </summary>
public static class DependencyRestorer
{
    public sealed record DependencyResult(string Name, string Status, bool Ok);

    public static async Task<IReadOnlyList<DependencyResult>> RestoreAsync(
        ProjectConfig project, string projectRoot, ILogger logger, CancellationToken cancellationToken)
    {
        var results = new List<DependencyResult>();

        // 1. Initialize git submodules (covers lib/lib-arm added as submodules).
        if (File.Exists(Path.Combine(projectRoot, ".gitmodules")))
        {
            logger.LogInformation("Initializing git submodules...");
            var (ok, _, err) = await GitAsync(projectRoot, ["submodule", "update", "--init", "--recursive"], cancellationToken);
            if (!ok)
                logger.LogWarning("git submodule update failed: {Error}", err.Trim());
        }

        // 2. Clone declared dependencies that are still missing and have a URL.
        foreach (var dep in project.Dependencies ?? [])
        {
            var name = dep.Directory;
            var dir = Path.Combine(projectRoot, name);

            if (IsPopulated(dir))
            {
                results.Add(new(name, "present", true));
                continue;
            }
            if (string.IsNullOrEmpty(dep.Git))
            {
                results.Add(new(name, "missing (no git url - initialize the submodule)", false));
                continue;
            }

            logger.LogInformation("Cloning {Name} <- {Git}", name, dep.Git);
            var args = new List<string> { "clone" };
            if (!string.IsNullOrEmpty(dep.Ref))
                args.AddRange(["--branch", dep.Ref]);
            args.AddRange([dep.Git, dir]);

            var (ok, _, err) = await GitAsync(projectRoot, args, cancellationToken);
            results.Add(new(name, ok ? "cloned" : $"clone failed: {err.Trim()}", ok));
        }

        return results;
    }

    /// <summary>Declared dependencies whose directory is absent or empty (for build-time guidance).</summary>
    public static IReadOnlyList<string> MissingDependencies(ProjectConfig project, string projectRoot) =>
        (project.Dependencies ?? [])
            .Select(d => d.Directory)
            .Where(n => !IsPopulated(Path.Combine(projectRoot, n)))
            .ToList();

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
}
