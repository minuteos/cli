using Microsoft.Extensions.Logging;

namespace MinuteOS.Build.Graph.Steps;

/// <summary>
/// Converts the linked image into flashable formats with objcopy (graph form of
/// the <c>gcc:objcopy</c> step). Consumes the ELF, produces bin/hex/srec images.
/// Config: <c>formats: bin,hex,srec</c> shorthand, or explicit
/// <c>format</c>/<c>ext</c>/<c>args</c>.
/// </summary>
public sealed class GccObjcopyStep(IReadOnlyDictionary<string, string> config) : IGraphStep
{
    public string Name => "gcc:objcopy";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))],
        [("kind", "image")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var image = ctx.Inputs.FirstOrDefault();
        if (image == null)
            yield break;
        var basePath = Path.ChangeExtension(image.Id, null);

        foreach (var (format, ext, extra) in ResolveOutputs(config))
        {
            var outPath = basePath + ext;
            var outArtifact = Artifact.File(outPath, ("kind", "image"), ("format", ext.TrimStart('.')));
            yield return new BuildAction($"objcopy {format}", [image], [outArtifact], async actx =>
            {
                var args = new List<string> { "-O", format };
                args.AddRange(extra);
                args.AddRange([image.Id, outPath]);
                var r = await actx.Toolchain.RunToolAsync(actx.Toolchain.ObjCopy, args, actx.ProjectRoot, actx.CancellationToken);
                if (!r.Success)
                    return ActionResult.Fail($"objcopy -O {format} failed: {r.StdErr.Trim()}");
                actx.Logger.LogInformation("Generated {File}", outPath);
                return ActionResult.Ok();
            })
            {
                ConfigKey = $"-O {format} {string.Join(' ', extra)}",
            };
        }
    }

    private static IEnumerable<(string Format, string Ext, string[] Extra)> ResolveOutputs(
        IReadOnlyDictionary<string, string> cfg)
    {
        if (cfg.TryGetValue("formats", out var formats) && !string.IsNullOrWhiteSpace(formats))
        {
            foreach (var f in formats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (format, ext) = Standard(f);
                yield return (format, ext, []);
            }
            yield break;
        }

        if (cfg.TryGetValue("format", out var single) && !string.IsNullOrWhiteSpace(single))
        {
            var (format, stdExt) = Standard(single);
            var ext = cfg.TryGetValue("ext", out var e) && !string.IsNullOrWhiteSpace(e) ? e : stdExt;
            var extra = cfg.TryGetValue("args", out var a) && !string.IsNullOrWhiteSpace(a)
                ? a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
            yield return (format, ext, extra);
        }
    }

    private static (string Format, string Ext) Standard(string spec) => spec.ToLowerInvariant() switch
    {
        "bin" or "binary" => ("binary", ".bin"),
        "hex" or "ihex" => ("ihex", ".hex"),
        "srec" => ("srec", ".srec"),
        _ => (spec, "." + spec),
    };
}

/// <summary>Emits plain (.S) and source-interleaved (.SS) disassembly from the ELF.</summary>
public sealed class DisassemblyStep : IGraphStep
{
    public string Name => "disassembly";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))],
        [("kind", "text")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var image = ctx.Inputs.FirstOrDefault();
        if (image == null)
            yield break;
        var basePath = Path.ChangeExtension(image.Id, null);

        yield return Dump("disassembly", image, basePath + ".S", ["-d"]);
        yield return Dump("disassembly-source", image, basePath + ".SS", ["-d", "-S"]);
    }

    private BuildAction Dump(string label, Artifact image, string outPath, string[] flags) =>
        new(label, [image], [Artifact.File(outPath, ("kind", "text"))], async actx =>
        {
            var r = await actx.Toolchain.RunToolAsync(actx.Toolchain.ObjDump, [.. flags, image.Id], actx.ProjectRoot, actx.CancellationToken);
            if (!r.Success)
                return ActionResult.Fail($"objdump failed: {r.StdErr.Trim()}");
            await File.WriteAllTextAsync(outPath, r.StdOut, actx.CancellationToken);
            actx.Logger.LogInformation("Generated {File}", outPath);
            return ActionResult.Ok();
        });
}

/// <summary>Reports the size of the linked image (no output artifact).</summary>
public sealed class SizeStep : IGraphStep
{
    public string Name => "size";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.One, ("kind", "image"), ("format", "elf"))]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var image = ctx.Inputs.FirstOrDefault();
        if (image == null)
            yield break;

        // No output artifact -> always runs (matches the legacy size report).
        yield return new BuildAction("size", [image], [], async actx =>
        {
            var r = await actx.Toolchain.SizeAsync(image.Id, actx.CancellationToken);
            if (r.Success && !string.IsNullOrWhiteSpace(r.StdOut))
                actx.Logger.LogInformation("\n{Size}", r.StdOut.TrimEnd());
            return ActionResult.Ok();
        });
    }
}
