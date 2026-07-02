using System.Globalization;
using System.Xml.Linq;

namespace MinuteOS.Build;

/// <summary>
/// Writes test results as JUnit XML for CI systems (GitLab/GitHub/Jenkins test
/// report ingestion). One <c>testsuite</c> per minuteos suite; a suite that
/// failed to build is reported as a single errored case.
/// </summary>
public static class JUnitReport
{
    public static void Write(string path, string name, IReadOnlyList<(string Suite, TestRunResult? Result)> suites)
    {
        var doc = new XElement("testsuites",
            new XAttribute("name", name),
            new XAttribute("tests", suites.Sum(s => s.Result?.Total ?? 1)),
            new XAttribute("failures", suites.Sum(s => s.Result?.Failed ?? 0)),
            new XAttribute("errors", suites.Count(s => s.Result == null)),
            new XAttribute("time", Seconds(suites.Sum(s => s.Result?.Duration.TotalSeconds ?? 0))),
            suites.Select(s => Suite(s.Suite, s.Result)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        new XDocument(doc).Save(path);
    }

    private static XElement Suite(string id, TestRunResult? result)
    {
        if (result == null)
        {
            // Build failure: one errored placeholder case so CI shows the suite.
            return new XElement("testsuite",
                new XAttribute("name", id),
                new XAttribute("tests", 1),
                new XAttribute("failures", 0),
                new XAttribute("errors", 1),
                new XElement("testcase",
                    new XAttribute("name", "build"),
                    new XAttribute("classname", id),
                    new XElement("error", new XAttribute("message", "build failed"))));
        }

        var suite = new XElement("testsuite",
            new XAttribute("name", id),
            new XAttribute("tests", result.Total),
            new XAttribute("failures", result.Failed),
            new XAttribute("errors", 0),
            new XAttribute("time", Seconds(result.Duration.TotalSeconds)));

        foreach (var c in result.Cases)
        {
            var tc = new XElement("testcase",
                new XAttribute("name", c.Name),
                new XAttribute("classname", id));
            if (!c.Passed)
                tc.Add(new XElement("failure", new XAttribute("message", c.Detail ?? "failed")));
            suite.Add(tc);
        }

        // A suite that produced no parsable cases but failed (timeout, crash)
        // still needs a visible failure.
        if (result.Cases.Count == 0 && !result.Success)
        {
            suite.SetAttributeValue("tests", 1);
            suite.SetAttributeValue("failures", 1);
            suite.Add(new XElement("testcase",
                new XAttribute("name", "run"),
                new XAttribute("classname", id),
                new XElement("failure", new XAttribute("message",
                    result.TimedOut ? "timed out" : $"exited {result.ExitCode}"))));
        }

        return suite;
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
