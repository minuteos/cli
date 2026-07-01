using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph.Steps;

/// <summary>
/// Derives version info from git tags and contributes <c>APP_VERSION*</c> defines
/// to the settings bag (graph form of the legacy git-version step). A settings
/// <b>augmenter</b>: it produces <c>kind=settings</c>, so the engine runs it before
/// any reader and merges its defines into the working bag. Always runs (the commit
/// can change every build); a version change therefore re-fingerprints every
/// compile (the accepted full-rebuild cost of global defines).
///
/// Config: <c>tag-pattern</c> (default <c>v*</c>).
/// </summary>
public sealed class GitVersionStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "git-version";

    public StepSignature Signature => StepSignature.Source(consumes: null, [("kind", "settings")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var projectRoot = ctx.Config.ProjectRoot;
        var tagPattern = config.GetValueOrDefault("tag-pattern", "v*");

        yield return new BuildAction("git-version", [], [], async actx =>
        {
            var output = await DescribeAsync(projectRoot, tagPattern, actx.CancellationToken);
            if (string.IsNullOrEmpty(output))
            {
                actx.Logger.LogWarning("git describe found no version tags; skipping APP_VERSION");
                return ActionResult.Ok();
            }

            var defines = ParseDefines(output);
            if (!actx.Quiet && defines.Count > 0)
                actx.Logger.LogInformation("  version: {Version}", defines[0]);

            return new ActionResult(true, SettingsAdditions: new Dictionary<string, IReadOnlyList<string>>
            {
                ["defines"] = defines,
            });
        })
        {
            AlwaysRun = true,
        };
    }

    private static async Task<string?> DescribeAsync(string root, string pattern, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git",
                ["describe", "--tags", $"--match={pattern}", "--long", "--dirty"])
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return null;
            var output = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }

    // Parse: v1.2.3-5-gabcdef[-dirty]
    private static List<string> ParseDefines(string output)
    {
        var defines = new List<string>();
        var dirty = output.EndsWith("-dirty");
        var clean = dirty ? output[..^6] : output;
        var parts = clean.Split('-');
        if (parts.Length < 3)
            return defines;

        var commit = parts[^1];
        var distance = parts[^2];
        var tag = string.Join('-', parts[..^2]);
        if (tag.StartsWith('v')) tag = tag[1..];
        var version = distance == "0" ? tag : $"{tag}.{distance}";

        defines.Add($"APP_VERSION=\"{version}\"");
        defines.Add($"APP_VERSION_COMMIT=\"{commit}\"");
        if (dirty) defines.Add("APP_VERSION_DIRTY=1");

        var v = version.Split('.');
        if (v.Length >= 3
            && int.TryParse(v[0], out var major) && int.TryParse(v[1], out var minor) && int.TryParse(v[2], out var patch))
        {
            int.TryParse(v.ElementAtOrDefault(3), out var build);
            var v32 = (major << 24) | (minor << 16) | (patch << 8) | build;
            defines.Add($"APP_VERSION_32=0x{v32:X8}");
        }
        return defines;
    }
}
