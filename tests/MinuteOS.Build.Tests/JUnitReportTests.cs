using System.Xml.Linq;
using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

public class JUnitReportTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"minuteos-junit-{Guid.NewGuid():N}.xml");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static TestRunResult Result(bool timedOut, int exit, List<TestCaseResult> cases) => new()
    {
        TimedOut = timedOut,
        ExitCode = exit,
        Output = "",
        Cases = cases,
        Duration = TimeSpan.FromMilliseconds(120),
    };

    [Fact]
    public void Write_EmitsSuitesCasesFailuresAndErrors()
    {
        var suites = new List<(string, TestRunResult?)>
        {
            ("host/base/sanity", Result(false, 0,
                [new("ok1", true, null), new("ok2", true, null)])),
            ("host/kernel/sched", Result(false, 1,
                [new("ok", true, null), new("boom", false, "overflow at x.cpp:1")])),
            ("qemu/base/sanity", null),                       // build failed
            ("qemu/io/pipes", Result(true, -1, [])),          // timed out, no cases
        };

        JUnitReport.Write(_path, "proj", suites);

        var root = XDocument.Load(_path).Root!;
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("1", root.Attribute("errors")!.Value);

        var all = root.Elements("testsuite").ToList();
        Assert.Equal(4, all.Count);

        var pass = all.Single(s => s.Attribute("name")!.Value == "host/base/sanity");
        Assert.Equal("2", pass.Attribute("tests")!.Value);
        Assert.Equal("0", pass.Attribute("failures")!.Value);

        var fail = all.Single(s => s.Attribute("name")!.Value == "host/kernel/sched");
        Assert.Equal("1", fail.Attribute("failures")!.Value);
        var failure = fail.Elements("testcase").Single(c => c.Attribute("name")!.Value == "boom")
            .Element("failure")!;
        Assert.Contains("overflow", failure.Attribute("message")!.Value);

        var buildFail = all.Single(s => s.Attribute("name")!.Value == "qemu/base/sanity");
        Assert.Equal("1", buildFail.Attribute("errors")!.Value);

        var timeout = all.Single(s => s.Attribute("name")!.Value == "qemu/io/pipes");
        Assert.Equal("1", timeout.Attribute("failures")!.Value);
        Assert.Contains("timed out",
            timeout.Element("testcase")!.Element("failure")!.Attribute("message")!.Value);
    }
}
