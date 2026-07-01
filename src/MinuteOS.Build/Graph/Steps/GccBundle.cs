using Microsoft.Extensions.Logging;
using MinuteOS.Build.Steps;

namespace MinuteOS.Build.Graph.Steps;

/// <summary>
/// Discovers native sources from the structural source dirs and emits one
/// <c>kind=source</c> artifact per file, tagged by language. The native (gcc)
/// scan; a different language ships its own scan that claims its own extensions.
/// </summary>
public sealed class GccScanStep : IGraphStep
{
    public string Name => "scan:gcc";

    public StepSignature Signature => StepSignature.Source(
        consumes: null,
        [("kind", "source"), ("lang", "c")],
        [("kind", "source"), ("lang", "cpp")],
        [("kind", "source"), ("lang", "asm")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var sources = new SourceCollector()
            .CollectSources(ctx.Config.SourceDirs, ctx.Config.ProjectRoot)
            .Select(f => Artifact.File(f.FullPath, ("kind", "source"), ("lang", Lang(f.Language))))
            .ToList();

        // Sources already exist on disk; the scan just publishes them. Plan
        // re-enumerates every build, so a skipped action still yields the current
        // set (added/removed files are reflected); AlwaysRun keeps it cheap and
        // its outputs fingerprinted for orphan tracking.
        yield return new BuildAction("scan", [], sources, _ => Task.FromResult(ActionResult.Ok()))
        {
            AlwaysRun = true,
        };
    }

    internal static string Lang(SourceLanguage l) => l switch
    {
        SourceLanguage.C => "c",
        SourceLanguage.Cpp => "cpp",
        _ => "asm",
    };

    internal static SourceLanguage Language(string lang) => lang switch
    {
        "c" => SourceLanguage.C,
        "cpp" => SourceLanguage.Cpp,
        _ => SourceLanguage.Assembly,
    };
}

/// <summary>
/// Compiles each source artifact into an object, reading flags from the settings
/// bag. Honors the PCH (emitted as a prerequisite action). Produces
/// <c>kind=object</c>.
/// </summary>
public sealed class GccCompileStep : IGraphStep
{
    public string Name => "gcc:compile";

    public StepSignature Signature => StepSignature.Source(
        consumes:
        [
            Selector.Of(("kind", "source"), ("lang", "c")),
            Selector.Of(("kind", "source"), ("lang", "cpp")),
            Selector.Of(("kind", "source"), ("lang", "asm")),
            Selector.Of(("kind", "header-dir")),
        ],
        [("kind", "object")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var config = ctx.Config;
        var settings = ctx.Settings;
        var projectRoot = config.ProjectRoot;

        // Generated header dirs (e.g. a transpiler's) join the -I path.
        var extraIncludes = ctx.Inputs.Where(a => a.Kind == "header-dir").Select(a => a.Id).ToList();
        var sources = ctx.Inputs.Where(a => a.Kind == "source").ToList();

        // PCH prerequisite (cpp compiles -include it). Args are built here so they
        // serve as the action's config fingerprint.
        Artifact? pchArtifact = null;
        if (config.Pch != null)
        {
            pchArtifact = Artifact.File(config.PchGchFile, ("kind", "pch"));
            var pchArgs = new List<string> { "-c", config.Pch };
            GccFlags.AppendCompileFlags(pchArgs, settings, config, [], extraIncludes, Path.GetDirectoryName(config.Pch)!, SourceLanguage.Cpp);
            pchArgs.AddRange(["-o", config.PchGchFile]);

            yield return new BuildAction("pch", [Artifact.File(config.Pch, ("kind", "header"))], [pchArtifact], async actx =>
            {
                if (!actx.Quiet)
                    actx.Logger.LogInformation("  precompiling {Pch}", Path.GetFileName(config.Pch));
                var r = await actx.Toolchain.RunToolAsync(actx.Toolchain.CXX, pchArgs, projectRoot, actx.CancellationToken);
                if (!string.IsNullOrWhiteSpace(r.StdErr)) actx.Logger.LogWarning("{Err}", r.StdErr.TrimEnd());
                if (!r.Success) return ActionResult.Fail($"PCH failed: exit {r.ExitCode}");
                return new ActionResult(true, DiscoveredInputs: DepFile.Parse(DepFile.GetDepPath(config.PchGchFile)));
            })
            {
                ConfigKey = string.Join(' ', pchArgs),
            };
        }

        foreach (var source in sources)
        {
            var lang = GccScanStep.Language(source.Properties.GetValueOrDefault("lang", "cpp"));
            var sourcePath = source.Id;
            var rel = Path.GetRelativePath(projectRoot, sourcePath);
            var objPath = Path.Combine(config.ObjectDir, Path.ChangeExtension(rel, ".o"));
            var objArtifact = Artifact.File(objPath, ("kind", "object"));

            var inputs = pchArtifact != null && lang == SourceLanguage.Cpp
                ? new[] { source, pchArtifact }
                : [source];

            var compiler = lang == SourceLanguage.C ? "gcc" : "g++";
            var args = new List<string> { "-c", sourcePath };
            GccFlags.AppendCompileFlags(args, settings, config, [], extraIncludes, Path.GetDirectoryName(sourcePath)!, lang);
            if (lang == SourceLanguage.Cpp && config.Pch != null &&
                !sourcePath.EndsWith(".nopch.cpp", StringComparison.Ordinal))
            {
                args.AddRange(["-include", config.PchIncludeBase, "-Winvalid-pch"]);
            }
            args.AddRange(["-o", objPath]);

            yield return new BuildAction($"compile {rel}", inputs, [objArtifact], async actx =>
            {
                if (!actx.Quiet)
                    actx.Logger.LogInformation("  {Compiler} -c {Source}", compiler, rel);
                var program = lang == SourceLanguage.C ? actx.Toolchain.CC : actx.Toolchain.CXX;
                var r = await actx.Toolchain.RunToolAsync(program, args, projectRoot, actx.CancellationToken);
                if (!string.IsNullOrWhiteSpace(r.StdErr)) actx.Logger.LogWarning("{Err}", r.StdErr.TrimEnd());
                if (!r.Success) return ActionResult.Fail($"compile {rel} failed: exit {r.ExitCode}");
                // Discovered header deps from the .d file feed the next up-to-date check.
                return new ActionResult(true, DiscoveredInputs: DepFile.Parse(DepFile.GetDepPath(objPath)));
            })
            {
                ConfigKey = string.Join(' ', args),
            };
        }
    }
}

/// <summary>
/// Links all object artifacts into the primary image, reading flags from the
/// settings bag. Produces <c>kind=image, format=elf</c>.
/// </summary>
public sealed class GccLinkStep : IGraphStep
{
    public string Name => "gcc:link";

    public StepSignature Signature => StepSignature.Source(
        consumes: [Selector.Of(Cardinality.Many, ("kind", "object"))],
        [("kind", "image"), ("format", "elf")]);

    public IEnumerable<BuildAction> Plan(PlanContext ctx)
    {
        var config = ctx.Config;
        var s = ctx.Settings;
        var output = config.PrimaryOutput;
        var format = config.PrimaryExt.TrimStart('.');
        var objects = ctx.Inputs.Select(a => a.Id).ToList();

        var imageArtifact = Artifact.File(output, ("kind", "image"), ("format", format));

        var args = new List<string> { "-o", output };
        args.AddRange(s.List("gcc.arch-flags"));
        args.AddRange(objects.Order());

        var ld = s.Scalar("gcc.ld-script");
        if (ld != null) args.AddRange(["-T", ld]);

        foreach (var dir in s.List("gcc.link-dirs"))
            args.AddRange(["-L", ResolveLinkDir(dir, config)]);

        var libDirs = config.TargetDirs.Concat(config.ComponentDirs);
        if (Directory.Exists(config.SourceDir))
            libDirs = new[] { config.SourceDir }.Concat(libDirs);
        foreach (var dir in libDirs)
            args.AddRange(["-L", dir]);

        args.AddRange(s.List("gcc.link-flags"));
        args.Add("-Wl,--gc-sections");

        yield return new BuildAction("link", ctx.Inputs, [imageArtifact], async actx =>
        {
            if (!actx.Quiet)
            {
                actx.Logger.LogInformation("");
                actx.Logger.LogInformation("Linking...");
            }

            var r = await actx.Toolchain.RunToolAsync(actx.Toolchain.CXX, args, actx.ProjectRoot, actx.CancellationToken);
            if (!string.IsNullOrWhiteSpace(r.StdErr)) actx.Logger.LogWarning("{Err}", r.StdErr.TrimEnd());
            return r.Success ? ActionResult.Ok() : ActionResult.Fail($"link failed: exit {r.ExitCode}");
        })
        {
            ConfigKey = string.Join(' ', args),
        };
    }

    /// <summary>
    /// A relative link dir (e.g. from a migrated target's <c>gcc.link-dirs</c>) is
    /// relative to the target/component dir it was declared in; resolve it against
    /// those dirs. Absolute paths pass through.
    /// </summary>
    private static string ResolveLinkDir(string dir, IBuildConfiguration config)
    {
        if (Path.IsPathRooted(dir))
            return dir;
        foreach (var searchDir in config.TargetDirs.Concat(config.ComponentDirs))
        {
            var candidate = Path.Combine(searchDir, dir);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return dir;
    }
}
