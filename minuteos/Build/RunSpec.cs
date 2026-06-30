using System.Text;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Build;

/// <summary>
/// A resolved launch specification: how to execute a built image. Produced by
/// <see cref="RunSpecResolver"/> and consumed by both <c>minuteos run</c> (live
/// stdio) and the test executor (capture + timeout + parse).
/// </summary>
public record RunSpec(string Program, List<string> Args, int TimeoutSeconds);

/// <summary>
/// Resolves how to run a configuration's image from its <c>Run</c>-phase step
/// (the target-overridable replacement for the top-level <c>test-runner</c>),
/// falling back to the legacy <c>test-runner</c> field, then to direct execution.
/// </summary>
public static class RunSpecResolver
{
    private const int DefaultTimeoutSeconds = 60;

    private static readonly HashSet<string> RunStepNames =
        new(StringComparer.OrdinalIgnoreCase) { "run", "qemu", "renode", "exec" };

    public static RunSpec Resolve(BuildConfiguration config, string image, string? filter)
    {
        // Prefer the most-specific Run-phase step (StepRefs accumulate parents-first,
        // so the last matching one is the most specific override).
        var runRef = config.StepRefs.LastOrDefault(
            s => s.Phase == BuildPhase.Run || RunStepNames.Contains(s.Name));

        if (runRef != null)
        {
            var cfg = runRef.Config ?? new();
            var timeout = cfg.TryGetValue("timeout", out var t) && int.TryParse(t, out var ts)
                ? ts : DefaultTimeoutSeconds;
            var command = cfg.GetValueOrDefault("command", DefaultCommand(runRef.Name));

            if (string.IsNullOrEmpty(command))
                return DirectExec(image, filter, timeout);

            var args = Tokenize(cfg.GetValueOrDefault("args", ""))
                .Select(a => Substitute(a, image, filter))
                .ToList();
            return new RunSpec(command, args, timeout);
        }

        // Legacy fallback: the deprecated top-level/target test-runner field.
        if (config.TestRunner != null)
        {
            var (program, legacyArgs) = config.TestRunner.Resolve(image, filter);
            return new RunSpec(program, legacyArgs, config.TestRunner.Timeout ?? DefaultTimeoutSeconds);
        }

        return DirectExec(image, filter, DefaultTimeoutSeconds);
    }

    private static RunSpec DirectExec(string image, string? filter, int timeout)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(filter))
            args.Add(filter);
        return new RunSpec(image, args, timeout);
    }

    private static string DefaultCommand(string stepName) => stepName.ToLowerInvariant() switch
    {
        "qemu" => "qemu-system-arm",
        "renode" => "renode",
        _ => "", // "run"/"exec": no command => direct execution
    };

    private static string Substitute(string token, string image, string? filter) => token
        .Replace("{image}", image)
        .Replace("{binary}", image)
        .Replace("{filter}", filter ?? "");

    /// <summary>
    /// Splits a shell-style argument string into tokens, honoring double quotes so
    /// a placeholder that expands to a path with spaces stays a single argument.
    /// A quoted empty string (<c>""</c>) yields an empty token (parity with the
    /// legacy runner's handling of an unset {filter}).
    /// </summary>
    internal static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, hasToken = false;

        foreach (var c in s)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    hasToken = false;
                }
            }
            else
            {
                sb.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            tokens.Add(sb.ToString());
        return tokens;
    }
}
