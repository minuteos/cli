using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class TestOutputParsingTests
{
    [Fact]
    public void ParseSummary_MinuteosFormat()
    {
        var (total, passed, failed) = TestExecutor.ParseSummary(
            "##TEST## a PASS\n##SUMMARY## total=3 passed=2 failed=1\n");

        Assert.Equal(3, total);
        Assert.Equal(2, passed);
        Assert.Equal(1, failed);
    }

    [Fact]
    public void ParseSummary_RealTestrunnerMarkdown_AllPass()
    {
        // Output from the real minuteos testrunner (lib/targets/all/testrunner).
        var output =
            "| **TOTAL** | **28** | **105.557** | **28** :white_check_mark: / **0** :x: |\n" +
            "<summary>28 tests (<b>PASSED</b>)\n\n**28** :white_check_mark: / **0** :x:\n</summary>";

        var (total, passed, failed) = TestExecutor.ParseSummary(output);

        Assert.Equal(28, total);
        Assert.Equal(28, passed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void ParseSummary_RealTestrunnerMarkdown_WithFailures()
    {
        var output =
            "| **TOTAL** | **30** | **12.000** | **28** :white_check_mark: / **2** :x: |\n" +
            "<summary>30 tests (<b>FAILED</b>)\n\n**28** :white_check_mark: / **2** :x:\n</summary>";

        var (total, passed, failed) = TestExecutor.ParseSummary(output);

        Assert.Equal(30, total);
        Assert.Equal(28, passed);
        Assert.Equal(2, failed);
    }

    [Fact]
    public void ParseCases_RealTestrunnerMarkdown_RowsAndResults()
    {
        var output =
            "| Test file | Test name | Duration (ms) | Result |\n" +
            "| /p/Delegate.cpp:43 | 01 Simple member | 0.001 | :white_check_mark: |\n" +
            "| /p/Span.cpp:87 | 03 Splitting | 0.002 | :x: |\n" +
            "| **TOTAL** | **2** | **0.003** | **1** :white_check_mark: / **1** :x: |\n";

        var cases = TestExecutor.ParseCases(output);

        // Header and TOTAL rows are excluded; only the two data rows parse.
        Assert.Equal(2, cases.Count);
        Assert.Equal("01 Simple member", cases[0].Name);
        Assert.True(cases[0].Passed);
        Assert.Equal("03 Splitting", cases[1].Name);
        Assert.False(cases[1].Passed);
    }

    [Fact]
    public void ParseSummary_NoRecognizedFormat_ReturnsNulls()
    {
        var (total, passed, failed) = TestExecutor.ParseSummary("some unrelated output\n");
        Assert.Null(total);
        Assert.Null(passed);
        Assert.Null(failed);
    }
}
