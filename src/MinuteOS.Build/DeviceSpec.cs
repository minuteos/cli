using MinuteOS.Build.Steps;

namespace MinuteOS.Build;

/// <summary>
/// A resolved device operation: how to flash/erase/attach-to the hardware a
/// configuration targets. <see cref="Config"/> carries the step's raw config for
/// operation-specific keys (e.g. <c>gdb-port</c>).
/// </summary>
public record DeviceSpec(string Program, List<string> Args, IReadOnlyDictionary<string, string> Config);

/// <summary>
/// Resolves device operations from a configuration's <c>Device</c>-phase steps.
/// Like the run step, a board target declares how to perform each operation on
/// its hardware and the tool stays probe-agnostic:
///
///   # targets/my-board/target.yaml
///   steps:
///     - name: flash
///       phase: Device
///       config:
///         command: openocd
///         args: '-f interface/stlink.cfg -f target/stm32f4x.cfg -c "program {image} verify reset exit"'
///     - name: erase
///       phase: Device
///       config:
///         command: st-flash
///         args: 'erase'
///     - name: gdb-server
///       phase: Device
///       config:
///         command: openocd
///         args: '-f interface/stlink.cfg -f target/stm32f4x.cfg'
///         gdb-port: "3333"
///
/// Placeholders in args: {image} (the primary output), {image-base} (without
/// extension, so <c>{image-base}.bin</c> selects the objcopy output),
/// {image-dir}, {name}, {device} (the CLI <c>--device</c> value), {port} (the
/// step's <c>gdb-port</c>). A <c>[...]</c> group is dropped wholesale when a
/// placeholder inside it resolves empty, so optional flags degrade gracefully:
/// <c>args: '[--serial {device}] -c "program {image}"'</c>.
/// </summary>
public static class DeviceSpecResolver
{
    private static readonly System.Text.RegularExpressions.Regex OptionalGroup =
        new(@"\[([^\[\]]*)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Placeholder =
        new(@"\{[a-z-]+\}", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>The well-known operation step names (usable without an explicit phase).</summary>
    public static readonly IReadOnlySet<string> OperationNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "flash", "erase", "gdb-server" };

    /// <summary>
    /// Resolves an operation for a configuration: an explicit Device-phase step
    /// wins; otherwise, when the settings bag carries <c>jlink.device</c>, a
    /// J-Link default invocation is synthesized (matching the Make-era workflow
    /// where <c>JLINK_DEVICE</c> was the single per-board knob). Null when
    /// neither is available. The last (most specific) matching step wins.
    /// </summary>
    public static DeviceSpec? Resolve(BuildConfiguration config, string operation, string image, string? device = null)
    {
        var stepRef = config.StepRefs.LastOrDefault(s =>
            s.Name.Equals(operation, StringComparison.OrdinalIgnoreCase)
            && (s.Phase == BuildPhase.Device || s.Phase == null));

        var cfg = stepRef?.Config ?? new Dictionary<string, string>();
        var command = cfg.GetValueOrDefault("command");
        var argsTemplate = cfg.GetValueOrDefault("args");
        var script = cfg.GetValueOrDefault("script");

        if (string.IsNullOrWhiteSpace(command))
        {
            // No explicit command: fall back to the J-Link provider.
            if (JLinkDefaults(operation, config.Settings) is not var (jcmd, jargs, jscript))
                return null;
            command = jcmd;
            argsTemplate ??= jargs;
            script ??= jscript;
        }

        var port = cfg.GetValueOrDefault("gdb-port", "3333");
        string Sub(string token) => Substitute(token, config, image, device, port);

        // A `script:` block is written (substituted) next to the outputs and
        // referenced via {script} - for tools driven by command files
        // (JLinkExe -CommanderScript, openocd -f, ...).
        if (script != null && argsTemplate?.Contains("{script}") == true)
        {
            var scriptPath = Path.Combine(config.OutputRoot, $"{operation}.device-script");
            Directory.CreateDirectory(config.OutputRoot);
            File.WriteAllText(scriptPath, Sub(script.ReplaceLineEndings("\n")) + "\n");
            argsTemplate = argsTemplate.Replace("{script}", scriptPath);
        }

        // Drop [optional groups] whose placeholders resolve empty, then tokenize
        // (quotes keep substituted paths-with-spaces whole) and substitute.
        var raw = OptionalGroup.Replace(argsTemplate ?? "", m =>
            Placeholder.Matches(m.Groups[1].Value).Any(ph => Sub(ph.Value).Length == 0)
                ? "" : m.Groups[1].Value);

        var args = RunSpecResolver.Tokenize(raw)
            .Select(Sub)
            .Where(a => a.Length > 0)
            .ToList();

        return new DeviceSpec(command, args, cfg);
    }

    /// <summary>
    /// Built-in J-Link invocations for the standard operations, driven purely by
    /// settings: <c>jlink.device</c> (required), <c>jlink.interface</c> (SWD),
    /// <c>jlink.speed</c> (4000). Matches the vsix/Make-era J-Link workflow.
    /// </summary>
    private static (string Command, string Args, string? Script)? JLinkDefaults(string operation, Settings settings)
    {
        var jdevice = settings.Scalar("jlink.device");
        if (string.IsNullOrEmpty(jdevice))
            return null;

        var jif = settings.Scalar("jlink.interface") ?? "SWD";
        var jspeed = settings.Scalar("jlink.speed") ?? "4000";
        var common = $"-NoGui 1 -Device {jdevice} -If {jif} -Speed {jspeed}";

        return operation.ToLowerInvariant() switch
        {
            "flash" => ("JLinkExe",
                $"{common} [-SelectEmuBySN {{device}}] -AutoConnect 1 -ExitOnError 1 -CommanderScript {{script}}",
                "loadfile {image}\nr\ng\nqc"),
            "erase" => ("JLinkExe",
                $"{common} [-SelectEmuBySN {{device}}] -AutoConnect 1 -ExitOnError 1 -CommanderScript {{script}}",
                "erase\nqc"),
            "gdb-server" => ("JLinkGDBServer",
                $"-Device {jdevice} -If {jif} -Speed {jspeed} -Port {{port}} [-Select USB={{device}}] -NoGui",
                null),
            _ => null,
        };
    }

    private static string Substitute(string token, BuildConfiguration config, string image, string? device, string port)
    {
        var imageBase = Path.Combine(Path.GetDirectoryName(image) ?? "", Path.GetFileNameWithoutExtension(image));
        return token
            .Replace("{image-base}", imageBase)
            .Replace("{image-dir}", Path.GetDirectoryName(image) ?? "")
            .Replace("{image}", image)
            .Replace("{binary}", image)
            .Replace("{name}", config.OutputName)
            .Replace("{device}", device ?? "")
            .Replace("{port}", port);
    }
}
