using MinuteOS.Build;
using MinuteOS.Build.Steps;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("test", Description = "Build and run test suites")]
public class TestCommand : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration to test (omit to use all)")]
    public string Configuration { get; set; } = "";

    [Option("--filter", "-f", Description = "Only run test cases whose name contains this string")]
    public string? Filter { get; set; }

    [Option("--suite", "-s", Description = "Only run suites whose id (component/suite) contains this string")]
    public string? SuiteFilter { get; set; }

    [Option("--jobs", "-j", Description = "Number of parallel compilation jobs")]
    public int Jobs { get; set; }

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    [Option("--junit", Description = "Write a JUnit XML test report to this file (for CI)")]
    public string? Junit { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        ProjectConfig projectConfig;
        try
        {
            projectConfig = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("{Message}", ex.Message);
            return 1;
        }

        var configNames = Configuration != ""
            ? [Configuration]
            : projectConfig.ConfigurationNames.ToList();

        var parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount;
        var executor = new TestExecutor(Logger);

        var anyFailures = false;
        var grandTotal = 0;
        var grandPassed = 0;
        var grandFailed = 0;
        var report = new List<(string Suite, TestRunResult? Result)>();

        foreach (var configName in configNames)
        {
            Logger.LogInformation("=== Testing configuration: {Name} ===", configName);

            BuildConfiguration baseConfig;
            try
            {
                baseConfig = BuildConfiguration.Create(projectConfig, configName, projectRoot);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to resolve '{Name}': {Message}", configName, ex.Message);
                anyFailures = true;
                continue;
            }

            // The 'test' pseudo-target may add hardware stubs; include it for discovery context.
            var discovery = new TestDiscovery();
            var suites = discovery.Discover(baseConfig.TargetDirs);

            if (!string.IsNullOrEmpty(SuiteFilter))
                suites = suites.Where(s => s.Id.Contains(SuiteFilter)).ToList();

            if (suites.Count == 0)
            {
                Logger.LogWarning("No test suites found.");
                continue;
            }

            // Confirm a testrunner component is available to host the tests.
            var testrunnerDirs = baseConfig.Layout.ResolveComponentDirs(baseConfig.TargetDirs, ["testrunner"]);
            if (testrunnerDirs.Count == 0)
            {
                Logger.LogError(
                    "Found {Count} test suite(s) but no 'testrunner' component. " +
                    "Add a testrunner component (e.g. lib/targets/all/testrunner) to build tests.",
                    suites.Count);
                anyFailures = true;
                continue;
            }

            Logger.LogInformation("Discovered {Count} test suite(s).", suites.Count);
            Logger.LogInformation("");

            var toolchain = new Toolchain(baseConfig.Settings.Scalar("gcc.toolchain-prefix") ?? "", Logger);

            foreach (var suite in suites)
            {
                var result = await RunSuiteAsync(
                    projectConfig, configName, projectRoot, suite,
                    toolchain, executor, parallelism, cancellationToken);

                report.Add(($"{configName}/{suite.Id}", result));

                if (result == null)
                {
                    // Build failure - already logged.
                    anyFailures = true;
                    continue;
                }

                grandTotal += result.Total;
                grandPassed += result.Passed;
                grandFailed += result.Failed;

                if (result.Success)
                {
                    Logger.LogInformation("  PASS  {Suite}  ({Passed}/{Total}, {Ms}ms)",
                        suite.Id, result.Passed, result.Total, (int)result.Duration.TotalMilliseconds);
                }
                else
                {
                    anyFailures = true;
                    var reason = result.TimedOut ? "timed out"
                        : result.ExitCode != 0 && result.Cases.Count == 0 ? $"exited {result.ExitCode}"
                        : $"{result.Failed} failed";
                    Logger.LogError("  FAIL  {Suite}  ({Reason})", suite.Id, reason);

                    foreach (var c in result.Cases.Where(c => !c.Passed))
                        Logger.LogError("          {Case}: {Detail}", c.Name, c.Detail ?? "failed");

                    if (result.Cases.Count == 0 && !string.IsNullOrWhiteSpace(result.Output))
                        Logger.LogError("          output: {Output}", result.Output.Trim());
                }
            }

            Logger.LogInformation("");
        }

        Logger.LogInformation("==============================");
        Logger.LogInformation("Total: {Total}  Passed: {Passed}  Failed: {Failed}",
            grandTotal, grandPassed, grandFailed);

        if (Junit != null)
        {
            JUnitReport.Write(Junit, projectConfig.Name ?? "minuteos", report);
            Logger.LogInformation("JUnit report: {Path}", Path.GetFullPath(Junit));
        }

        return anyFailures ? 1 : 0;
    }

    /// <summary>
    /// Builds and runs one test suite. Returns null if the build failed.
    /// </summary>
    private async Task<TestRunResult?> RunSuiteAsync(
        ProjectConfig projectConfig, string configName, string projectRoot, TestSuite suite,
        Toolchain toolchain, TestExecutor executor, int parallelism, CancellationToken cancellationToken)
    {
        // Components: testrunner + the component under test + the suite's own deps.
        var components = new List<string> { "testrunner", suite.Component };
        var suiteMeta = ComponentMeta.TryLoad(suite.Directory, suite.Name);
        if (suiteMeta?.Requires != null)
            components.AddRange(suiteMeta.Requires);

        var overrides = new BuildOverrides
        {
            PrimarySourceDir = suite.Directory,
            Components = components,
            ExtraTargets = ["test"],
            OutputName = suite.Name,
            OutputSubdir = Path.Combine("tests", suite.Component, suite.Name),
        };

        BuildConfiguration testConfig;
        try
        {
            testConfig = BuildConfiguration.Create(projectConfig, configName, projectRoot, overrides);
        }
        catch (Exception ex)
        {
            Logger.LogError("  FAIL  {Suite}  (configure error: {Message})", suite.Id, ex.Message);
            return null;
        }

        var built = await MinuteOS.Build.Graph.GraphRunner.BuildAsync(
            testConfig, toolchain, Logger,
            new BuildOptions { Parallelism = parallelism, Quiet = true },
            cancellationToken: cancellationToken);
        if (!built)
        {
            Logger.LogError("  FAIL  {Suite}  (build failed)", suite.Id);
            return null;
        }

        var spec = RunSpecResolver.Resolve(testConfig, testConfig.PrimaryOutput, Filter);
        return await executor.RunAsync(testConfig.PrimaryOutput, spec, cancellationToken);
    }
}
