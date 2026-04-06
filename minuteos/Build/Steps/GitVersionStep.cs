using System.Diagnostics;

namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Extracts version information from git tags and injects
/// APP_VERSION defines. Replaces GitAppVersion.mk.
/// </summary>
public class GitVersionStep : IBuildStep
{
    public string Name => "git-version";
    public BuildPhase DefaultPhase => BuildPhase.PreBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var projectRoot = context.Configuration.Layout.ProjectRoot;
        var tagPattern = context.StepConfig.GetValueOrDefault("tag-pattern", "v*");

        try
        {
            var psi = new ProcessStartInfo("git", ["describe", "--tags", $"--match={tagPattern}", "--long", "--dirty"])
            {
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Failed to start git");

            var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
            {
                context.Logger.LogWarning("git describe failed - no version tags found");
                return StepResult.Ok("No version tags found, skipping");
            }

            // Parse: v1.2.3-5-gabcdef[-dirty]
            var dirty = output.EndsWith("-dirty");
            var clean = dirty ? output[..^6] : output;
            var parts = clean.Split('-');

            if (parts.Length < 3)
                return StepResult.Fail($"Unexpected git describe output: {output}");

            var commit = parts[^1]; // gabcdef
            var distance = parts[^2];
            var tag = string.Join('-', parts[..^2]);
            if (tag.StartsWith('v'))
                tag = tag[1..];

            var version = distance == "0" ? tag : $"{tag}.{distance}";

            context.State.ExtraDefines.Add($"APP_VERSION=\"{version}\"");
            context.State.ExtraDefines.Add($"APP_VERSION_COMMIT=\"{commit}\"");
            if (dirty)
                context.State.ExtraDefines.Add("APP_VERSION_DIRTY=1");

            // Pack into 32-bit: major.minor.patch.build
            var versionParts = version.Split('.');
            if (versionParts.Length >= 3
                && int.TryParse(versionParts[0], out var major)
                && int.TryParse(versionParts[1], out var minor)
                && int.TryParse(versionParts[2], out var patch))
            {
                int.TryParse(versionParts.ElementAtOrDefault(3), out var build);
                var v32 = (major << 24) | (minor << 16) | (patch << 8) | build;
                context.State.ExtraDefines.Add($"APP_VERSION_32=0x{v32:X8}");
            }

            context.Logger.LogInformation("Version: {Version} ({Commit}{Dirty})",
                version, commit, dirty ? ", dirty" : "");

            return StepResult.Ok($"Version {version}");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"git-version step failed: {ex.Message}");
        }
    }
}
