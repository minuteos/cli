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
    /// Resolves an operation for a configuration, or null when its steps don't
    /// declare it. The last (most specific) matching step wins.
    /// </summary>
    public static DeviceSpec? Resolve(BuildConfiguration config, string operation, string image, string? device = null)
    {
        var stepRef = config.StepRefs.LastOrDefault(s =>
            s.Name.Equals(operation, StringComparison.OrdinalIgnoreCase)
            && (s.Phase == BuildPhase.Device || s.Phase == null));

        if (stepRef == null)
            return null;

        var cfg = stepRef.Config ?? new Dictionary<string, string>();
        if (!cfg.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
            return null;

        var port = cfg.GetValueOrDefault("gdb-port", "3333");
        string Sub(string token) => Substitute(token, config, image, device, port);

        // Drop [optional groups] whose placeholders resolve empty, then tokenize
        // (quotes keep substituted paths-with-spaces whole) and substitute.
        var raw = OptionalGroup.Replace(cfg.GetValueOrDefault("args", ""), m =>
            Placeholder.Matches(m.Groups[1].Value).Any(ph => Sub(ph.Value).Length == 0)
                ? "" : m.Groups[1].Value);

        var args = RunSpecResolver.Tokenize(raw)
            .Select(Sub)
            .Where(a => a.Length > 0)
            .ToList();

        return new DeviceSpec(command, args, cfg);
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
