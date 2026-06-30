using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("minuteos.tests")]

namespace MinuteOS.Cli.Build;

/// <summary>
/// Result of running a single test case, parsed from the testrunner output.
/// </summary>
public record TestCaseResult(string Name, bool Passed, string? Detail);

/// <summary>
/// Result of executing one test-suite binary.
/// </summary>
public record TestRunResult
{
    public required bool TimedOut { get; init; }
    public required int ExitCode { get; init; }
    public required string Output { get; init; }
    public required List<TestCaseResult> Cases { get; init; }
    public int? ReportedTotal { get; init; }
    public int? ReportedPassed { get; init; }
    public int? ReportedFailed { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of failed cases - from the testrunner summary if present,
    /// otherwise inferred from parsed cases.
    /// </summary>
    public int Failed => ReportedFailed ?? Cases.Count(c => !c.Passed);

    public int Passed => ReportedPassed ?? Cases.Count(c => c.Passed);

    public int Total => ReportedTotal ?? Cases.Count;

    /// <summary>
    /// A suite succeeds when it ran to completion (no timeout), exited cleanly,
    /// and reported no failures.
    /// </summary>
    public bool Success => !TimedOut && ExitCode == 0 && Failed == 0;
}

/// <summary>
/// Executes compiled test binaries via a configurable runner (host direct,
/// emulator, etc.) and parses the structured testrunner output.
/// </summary>
public class TestExecutor
{
    private readonly ILogger _logger;

    public TestExecutor(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<TestRunResult> RunAsync(
        string binaryPath,
        TestRunnerConfig? runner,
        string? filter,
        CancellationToken cancellationToken)
    {
        runner ??= new TestRunnerConfig();
        var (program, args) = runner.Resolve(binaryPath, filter);
        var timeoutSeconds = runner.Timeout ?? 60;

        _logger.LogDebug("Test run: {Program} {Args}", program, string.Join(' ', args));

        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(binaryPath),
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        var sw = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new TestRunResult
            {
                TimedOut = false,
                ExitCode = -1,
                Output = $"Failed to start '{program}': {ex.Message}",
                Cases = [],
                Duration = sw.Elapsed,
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        sw.Stop();
        var text = output.ToString();
        var cases = ParseCases(text);
        var (total, passed, failed) = ParseSummary(text);

        return new TestRunResult
        {
            TimedOut = timedOut,
            ExitCode = timedOut ? -1 : process.ExitCode,
            Output = text,
            Cases = cases,
            ReportedTotal = total,
            ReportedPassed = passed,
            ReportedFailed = failed,
            Duration = sw.Elapsed,
        };
    }

    // ##TEST## <name> PASS
    // ##TEST## <name> FAIL <detail...>
    private static readonly Regex CaseRegex = new(
        @"^##TEST##\s+(\S+)\s+(PASS|FAIL)(?:\s+(.*))?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // ##SUMMARY## total=N passed=N failed=N
    private static readonly Regex SummaryRegex = new(
        @"^##SUMMARY##\s+total=(\d+)\s+passed=(\d+)\s+failed=(\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Real minuteos testrunner (Markdown): "**28** :white_check_mark: / **0** :x:"
    private static readonly Regex MarkdownSummaryRegex = new(
        @"\*\*(\d+)\*\*\s*:white_check_mark:\s*/\s*\*\*(\d+)\*\*\s*:x:",
        RegexOptions.Compiled);

    // Real minuteos testrunner per-test row:
    // "| <file> | <name> | <duration> | :white_check_mark: |"  (or :x:)
    private static readonly Regex MarkdownRowRegex = new(
        @"^\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*[\d.]+\s*\|\s*:(white_check_mark|x):",
        RegexOptions.Multiline | RegexOptions.Compiled);

    internal static List<TestCaseResult> ParseCases(string output)
    {
        var cases = new List<TestCaseResult>();
        foreach (Match m in CaseRegex.Matches(output))
        {
            var name = m.Groups[1].Value;
            var passed = m.Groups[2].Value == "PASS";
            var detail = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            cases.Add(new TestCaseResult(name, passed, string.IsNullOrEmpty(detail) ? null : detail));
        }

        // Fall back to the real minuteos testrunner's Markdown table rows.
        if (cases.Count == 0)
        {
            foreach (Match m in MarkdownRowRegex.Matches(output))
            {
                var location = m.Groups[1].Value.Trim();
                var name = m.Groups[2].Value.Trim();
                var passed = m.Groups[3].Value == "white_check_mark";
                cases.Add(new TestCaseResult(name, passed, passed ? null : location));
            }
        }

        return cases;
    }

    internal static (int? Total, int? Passed, int? Failed) ParseSummary(string output)
    {
        var m = SummaryRegex.Match(output);
        if (m.Success)
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));

        // Real minuteos testrunner Markdown summary.
        var md = MarkdownSummaryRegex.Match(output);
        if (md.Success)
        {
            int passed = int.Parse(md.Groups[1].Value);
            int failed = int.Parse(md.Groups[2].Value);
            return (passed + failed, passed, failed);
        }

        return (null, null, null);
    }
}
