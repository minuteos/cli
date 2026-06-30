namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Converts the linked image into raw/flashable formats with the toolchain's
/// objcopy. The first-class replacement for the migrated objcopy <c>shell</c>
/// steps and the <c>binary-output</c> step. Reads the <c>Image</c> slot.
///
/// Two config shapes:
///
///   # convenience: standard formats off the image base name
///   - name: gcc:objcopy
///     config: { formats: "bin,hex,srec" }   # -> app.bin / app.hex / app.srec
///
///   # explicit: one objcopy invocation with custom flags / extension
///   - name: gcc:objcopy
///     config: { format: srec, ext: .s37, args: "--srec-forceS3" }
///
/// objcopy is invoked as: <c>objcopy -O &lt;format&gt; [args] &lt;image&gt; &lt;base&gt;&lt;ext&gt;</c>.
/// </summary>
public class GccObjcopyStep : IBuildStep
{
    public string Name => "gcc:objcopy";
    public BuildPhase DefaultPhase => BuildPhase.PostBuild;

    public async Task<StepResult> ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var image = context.State.Image ?? context.Configuration.PrimaryOutput;
        if (!File.Exists(image))
            return StepResult.Fail($"image not found: {image}");

        var basePath = Path.ChangeExtension(image, null);
        var cfg = context.StepConfig;

        // Convenience: a comma list of standard formats.
        if (cfg.TryGetValue("formats", out var formats) && !string.IsNullOrWhiteSpace(formats))
        {
            foreach (var f in formats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (format, ext) = StandardFormat(f);
                var result = await Convert(context, image, basePath + ext, format, [], cancellationToken);
                if (!result.Success)
                    return result;
            }
            return StepResult.Ok();
        }

        // Explicit: a single invocation with optional custom flags/extension.
        if (cfg.TryGetValue("format", out var single) && !string.IsNullOrWhiteSpace(single))
        {
            var (stdFormat, stdExt) = StandardFormat(single);
            var ext = cfg.TryGetValue("ext", out var e) && !string.IsNullOrWhiteSpace(e) ? e : stdExt;
            var extra = cfg.TryGetValue("args", out var a) && !string.IsNullOrWhiteSpace(a)
                ? a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            return await Convert(context, image, basePath + ext, stdFormat, extra, cancellationToken);
        }

        return StepResult.Fail("gcc:objcopy requires 'formats' or a 'format' in its config");
    }

    private static async Task<StepResult> Convert(
        StepContext context, string image, string output, string format,
        IReadOnlyList<string> extraArgs, CancellationToken cancellationToken)
    {
        var toolchain = context.Toolchain;
        var args = new List<string> { "-O", format };
        args.AddRange(extraArgs);
        args.AddRange([image, output]);

        var result = await toolchain.RunToolAsync(
            toolchain.ObjCopy, args, context.Configuration.Layout.ProjectRoot, cancellationToken);
        if (!result.Success)
            return StepResult.Fail($"objcopy -O {format} failed: {result.StdErr.Trim()}");

        context.Logger.LogInformation("Generated {File}", output);
        return StepResult.Ok();
    }

    /// <summary>Maps a shorthand (bin/hex/srec) or a raw objcopy format to (format, default ext).</summary>
    private static (string Format, string Ext) StandardFormat(string spec) => spec.ToLowerInvariant() switch
    {
        "bin" or "binary" => ("binary", ".bin"),
        "hex" or "ihex" => ("ihex", ".hex"),
        "srec" => ("srec", ".srec"),
        _ => (spec, "." + spec),
    };
}
