namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Converts the ELF output to binary formats (bin, hex) using objcopy.
/// Configure the output format via step config.
/// </summary>
public class BinaryOutputStep : IBuildStep
{
    public string Name => "binary-output";
    public BuildPhase DefaultPhase => BuildPhase.PostBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var elfPath = context.Configuration.PrimaryOutput;
        if (!File.Exists(elfPath))
            return StepResult.Fail($"ELF not found: {elfPath}");

        var basePath = Path.ChangeExtension(elfPath, null);
        var toolchain = context.Toolchain;
        var formats = context.StepConfig.GetValueOrDefault("formats", "bin,hex")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var format in formats)
        {
            var (objcopyFormat, ext) = format.ToLowerInvariant() switch
            {
                "bin" => ("binary", ".bin"),
                "hex" => ("ihex", ".hex"),
                "srec" => ("srec", ".srec"),
                _ => (format, $".{format}"),
            };

            var outputPath = basePath + ext;
            var result = await toolchain.RunToolAsync(
                toolchain.ObjCopy,
                ["-O", objcopyFormat, elfPath, outputPath],
                context.Configuration.Layout.ProjectRoot,
                cancellationToken);

            if (result.Success)
                context.Logger.LogInformation("Generated {File}", outputPath);
            else
                return StepResult.Fail($"objcopy failed for {format}: {result.StdErr}");
        }

        return StepResult.Ok();
    }
}
