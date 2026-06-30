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
    public void LoadTarget_ConvertsObjcopyRulesToObjcopySteps()
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
            Assert.Equal("gcc:objcopy", s.Name);
            Assert.Equal(BuildPhase.PostBuild, s.Phase);
            Assert.NotNull(s.Config);
        });

        var bin = Assert.Single(meta.Steps, s => s.Config!["format"] == "binary");
        Assert.Equal(".bin", bin.Config!["ext"]);
        Assert.False(bin.Config!.ContainsKey("args"));

        var srec = Assert.Single(meta.Steps, s => s.Config!["format"] == "srec");
        Assert.Equal(".s37", srec.Config!["ext"]);
        Assert.Equal("--srec-forceS3", srec.Config!["args"]);

        var hex = Assert.Single(meta.Steps, s => s.Config!["format"] == "ihex");
        Assert.Equal(".hex", hex.Config!["ext"]);
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
        Assert.Equal("gcc:objcopy", step.Name);
        Assert.Equal(BuildPhase.PostBuild, step.Phase);
        Assert.Equal("binary", step.Config!["format"]);
        Assert.Equal(".bin", step.Config!["ext"]);
    }
}
