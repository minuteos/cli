using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Covers resolution of how a configuration's image is launched: the new
/// Run-phase step (target-overridable), the legacy test-runner fallback, and
/// direct execution, plus the shell-style argument tokenizer.
/// </summary>
public class RunSpecResolverTests : IDisposable
{
    private readonly string _root;

    public RunSpecResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private BuildConfiguration Configure(string yaml, string configName)
    {
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"), yaml);
        return BuildConfiguration.Create(ProjectConfig.Load(_root), configName, _root);
    }

    // === Tokenizer ===

    [Theory]
    [InlineData("-a -b -c", new[] { "-a", "-b", "-c" })]
    [InlineData("-kernel \"a b.elf\"", new[] { "-kernel", "a b.elf" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    [InlineData("-append \"\"", new[] { "-append", "" })]
    public void Tokenize_HonorsQuotes(string input, string[] expected)
    {
        Assert.Equal(expected, RunSpecResolver.Tokenize(input));
    }

    // === Resolution ===

    [Fact]
    public void NoRunStep_RunsImageDirectly()
    {
        var config = Configure(
            "name: d\nconfigurations:\n  host:\n    target: host\n    components: []\n", "host");

        var spec = RunSpecResolver.Resolve(config, "/out/app.elf", filter: null);

        Assert.Equal("/out/app.elf", spec.Program);
        Assert.Empty(spec.Args);
    }

    [Fact]
    public void NoRunStep_WithFilter_PassesFilterAsArg()
    {
        var config = Configure(
            "name: d\nconfigurations:\n  host:\n    target: host\n    components: []\n", "host");

        var spec = RunSpecResolver.Resolve(config, "/out/app.elf", filter: "MyCase");

        Assert.Equal("/out/app.elf", spec.Program);
        Assert.Equal(["MyCase"], spec.Args);
    }

    [Fact]
    public void QemuRunStep_DefaultsCommand_AndSubstitutesImage()
    {
        var config = Configure(
            "name: d\nconfigurations:\n  qemu:\n    target: host\n    components: []\n" +
            "    steps:\n" +
            "      - name: qemu\n" +
            "        phase: Run\n" +
            "        config:\n" +
            "          args: '-machine lm3s6965evb -nographic -kernel \"{image}\"'\n" +
            "          timeout: \"30\"\n", "qemu");

        var spec = RunSpecResolver.Resolve(config, "/out/app.elf", filter: null);

        Assert.Equal("qemu-system-arm", spec.Program);
        Assert.Equal(["-machine", "lm3s6965evb", "-nographic", "-kernel", "/out/app.elf"], spec.Args);
        Assert.Equal(30, spec.TimeoutSeconds);
    }

    [Fact]
    public void RunStep_ExplicitCommand_Wins()
    {
        var config = Configure(
            "name: d\nconfigurations:\n  c:\n    target: host\n    components: []\n" +
            "    steps:\n" +
            "      - name: run\n" +
            "        phase: Run\n" +
            "        config:\n" +
            "          command: my-emulator\n" +
            "          args: '--run {image} --filter {filter}'\n", "c");

        var spec = RunSpecResolver.Resolve(config, "/out/app.elf", filter: "Foo");

        Assert.Equal("my-emulator", spec.Program);
        Assert.Equal(["--run", "/out/app.elf", "--filter", "Foo"], spec.Args);
    }

}
