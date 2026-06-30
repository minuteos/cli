using MinuteOS.Cli.Build;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Tests;

public class MakeImportTests : IDisposable
{
    private readonly string _tempDir;

    public MakeImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"minuteos-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteInclude(string content)
    {
        var dir = Path.Combine(_tempDir, "cortex-m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MakeImport.FileName), content);
        return dir;
    }

    [Fact]
    public void LoadTarget_ConvertsObjcopyRulesToShellSteps()
    {
        // Mirrors lib-arm/targets/cortex-m/Include.mk's binary/srec/ihex rules.
        var dir = WriteInclude(
            "PRIMARY_EXT = .axf\n" +
            "$(OUTPUT).bin: $(PRIMARY_OUTPUT)\n\t$(OBJCOPY) -O binary $< $@\n" +
            "$(OUTPUT).s37: $(PRIMARY_OUTPUT)\n\t$(OBJCOPY) -O srec --srec-forceS3 $< $@\n" +
            "$(OUTPUT).hex: $(PRIMARY_OUTPUT)\n\t$(OBJCOPY) -O ihex $< $@\n");

        var meta = MakeImport.LoadTarget(dir, "cortex-m");

        Assert.NotNull(meta);
        Assert.NotNull(meta.Steps);
        Assert.Equal(3, meta.Steps.Count);
        Assert.All(meta.Steps, s =>
        {
            Assert.Equal("shell", s.Name);
            Assert.Equal(BuildPhase.PostBuild, s.Phase);
            Assert.NotNull(s.Config);
        });

        var commands = meta.Steps.Select(s => s.Config!["command"]).ToList();
        Assert.Contains("{objcopy} -O binary {output} {output-base}.bin", commands);
        Assert.Contains("{objcopy} -O srec --srec-forceS3 {output} {output-base}.s37", commands);
        Assert.Contains("{objcopy} -O ihex {output} {output-base}.hex", commands);
    }

    [Fact]
    public void LoadTarget_StepsSurviveYamlRoundTrip()
    {
        var dir = WriteInclude(
            "$(OUTPUT).bin: $(PRIMARY_OUTPUT)\n\t$(OBJCOPY) -O binary $< $@\n");

        var imported = MakeImport.LoadTarget(dir, "cortex-m")!;
        File.WriteAllText(Path.Combine(dir, TargetMeta.FileName), imported.ToYaml());
        File.Delete(Path.Combine(dir, MakeImport.FileName));

        var reloaded = TargetMeta.TryLoad(dir, "cortex-m");

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.Steps);
        var step = Assert.Single(reloaded.Steps);
        Assert.Equal("shell", step.Name);
        Assert.Equal(BuildPhase.PostBuild, step.Phase);
        Assert.Equal("{objcopy} -O binary {output} {output-base}.bin", step.Config!["command"]);
    }
}
