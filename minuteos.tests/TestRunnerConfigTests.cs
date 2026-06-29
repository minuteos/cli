using MinuteOS.Cli.Build;

namespace MinuteOS.Cli.Tests;

public class TestRunnerConfigTests
{
    [Fact]
    public void Resolve_NoCommand_RunsBinaryDirectly()
    {
        var runner = new TestRunnerConfig();

        var (program, args) = runner.Resolve("/out/tests/base/sanity/sanity.elf", null);

        Assert.Equal("/out/tests/base/sanity/sanity.elf", program);
        Assert.Empty(args);
    }

    [Fact]
    public void Resolve_NoCommand_WithFilter_PassesFilterAsArg()
    {
        var runner = new TestRunnerConfig();

        var (program, args) = runner.Resolve("/out/sanity.elf", "ticks");

        Assert.Equal("/out/sanity.elf", program);
        Assert.Single(args);
        Assert.Equal("ticks", args[0]);
    }

    [Fact]
    public void Resolve_Qemu_SubstitutesBinaryPlaceholder()
    {
        var runner = new TestRunnerConfig
        {
            Command = "qemu-system-arm",
            Args = ["-machine", "lm3s6965evb", "-nographic", "-semihosting", "-kernel", "{binary}"],
        };

        var (program, args) = runner.Resolve("/out/tests/base/sanity/sanity.axf", null);

        Assert.Equal("qemu-system-arm", program);
        Assert.Equal(
            ["-machine", "lm3s6965evb", "-nographic", "-semihosting", "-kernel", "/out/tests/base/sanity/sanity.axf"],
            args);
    }

    [Fact]
    public void Resolve_Qemu_DropsStandaloneFilterTokenWhenNoFilter()
    {
        var runner = new TestRunnerConfig
        {
            Command = "qemu-system-arm",
            Args = ["-kernel", "{binary}", "-append", "{filter}"],
        };

        // {filter} is its own token but as the value of -append; only a *standalone*
        // {filter} token is dropped. Here it is standalone so it is removed.
        var (_, args) = runner.Resolve("/out/x.axf", null);

        Assert.Equal(["-kernel", "/out/x.axf", "-append"], args);
    }

    [Fact]
    public void Resolve_Qemu_SubstitutesFilterWhenPresent()
    {
        var runner = new TestRunnerConfig
        {
            Command = "qemu-system-arm",
            Args = ["-kernel", "{binary}", "{filter}"],
        };

        var (_, args) = runner.Resolve("/out/x.axf", "mytest");

        Assert.Equal(["-kernel", "/out/x.axf", "mytest"], args);
    }

    [Fact]
    public void Resolve_Renode_SubstitutesBinary()
    {
        var runner = new TestRunnerConfig
        {
            Command = "renode-test",
            Args = ["--variable", "BIN:{binary}", "run-tests.robot"],
        };

        var (program, args) = runner.Resolve("/out/x.elf", null);

        Assert.Equal("renode-test", program);
        Assert.Equal(["--variable", "BIN:/out/x.elf", "run-tests.robot"], args);
    }
}
