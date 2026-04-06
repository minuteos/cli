namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Reports the size of the linked output.
/// </summary>
public class SizeReportStep : IBuildStep
{
    public string Name => "size-report";
    public BuildPhase DefaultPhase => BuildPhase.PostBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var elfPath = context.Configuration.PrimaryOutput;
        if (!File.Exists(elfPath))
            return StepResult.Fail($"ELF not found: {elfPath}");

        var result = await context.Toolchain.SizeAsync(elfPath, cancellationToken);
        if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
            context.Logger.LogInformation("\n{Size}", result.StdOut.TrimEnd());

        return StepResult.Ok();
    }
}
