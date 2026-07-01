using Microsoft.Extensions.DependencyInjection;
using MinuteOS.Build;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Proves the build system is usable programmatically: register it via MEDI,
/// resolve <see cref="IBuildRunner"/>, and build a resolved configuration - no CLI.
/// </summary>
public class ProgrammaticBuildTests : IDisposable
{
    private readonly string _root;

    public ProgrammaticBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"minuteos-prog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "minuteos.yaml"),
            "name: prog\nconfigurations:\n  host:\n    target: host\n    components: []\n");
        File.WriteAllText(Path.Combine(_root, "src", "main.cpp"), "int main(){ return 0; }\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Build_ViaDI_ProducesTheImage()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddMinuteosBuild()
            .BuildServiceProvider();

        var runner = provider.GetRequiredService<IBuildRunner>();

        var project = ProjectConfig.Load(_root);
        var config = BuildConfiguration.Create(project, "host", _root);

        var ok = await runner.BuildAsync(config, new BuildOptions { Quiet = true });

        Assert.True(ok);
        Assert.True(File.Exists(config.PrimaryOutput));
    }
}
