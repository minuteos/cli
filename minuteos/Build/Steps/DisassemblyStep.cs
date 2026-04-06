namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Generates disassembly files from the linked ELF output.
/// Produces both plain disassembly (.S) and source-interleaved (.SS).
/// </summary>
public class DisassemblyStep : IBuildStep
{
    public string Name => "disassembly";
    public BuildPhase DefaultPhase => BuildPhase.PostBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var elfPath = context.Configuration.PrimaryOutput;
        if (!File.Exists(elfPath))
            return StepResult.Fail($"ELF not found: {elfPath}");

        var basePath = Path.ChangeExtension(elfPath, null);
        var toolchain = context.Toolchain;

        // Plain disassembly
        var result = await toolchain.RunToolAsync(
            toolchain.ObjDump, ["-d", elfPath],
            context.Configuration.Layout.ProjectRoot, cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
        {
            await File.WriteAllTextAsync(basePath + ".S", result.StdOut, cancellationToken);
            context.Logger.LogInformation("Generated {File}", basePath + ".S");
        }

        // Source-interleaved disassembly
        result = await toolchain.RunToolAsync(
            toolchain.ObjDump, ["-d", "-S", elfPath],
            context.Configuration.Layout.ProjectRoot, cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
        {
            await File.WriteAllTextAsync(basePath + ".SS", result.StdOut, cancellationToken);
            context.Logger.LogInformation("Generated {File}", basePath + ".SS");
        }

        return StepResult.Ok();
    }
}
