using MinuteOS.Build;
using MinuteOS.Build.Graph;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Shared plumbing for device operations (flash / erase / debug): resolve the
/// configuration, optionally build, resolve the operation's DeviceSpec from the
/// target's Device-phase steps, and launch it interactively.
/// </summary>
public abstract class DeviceCommandBase : LoggingCommand
{
    [Option("--configuration", "-c", Description = "Configuration (default: the first one)")]
    public string Configuration { get; set; } = "";

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    [Option("--device", "-d", Description = "Probe/device selector, substituted as {device}")]
    public string? Device { get; set; }

    [Option("--jobs", "-j", Description = "Number of parallel compilation jobs")]
    public int Jobs { get; set; }

    protected async Task<(BuildConfiguration Config, string ProjectRoot)?> ResolveAsync(
        bool build, CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        ProjectConfig projectConfig;
        try
        {
            projectConfig = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("{Message}", ex.Message);
            return null;
        }

        var configName = Configuration != "" ? Configuration : projectConfig.ConfigurationNames.FirstOrDefault();
        if (configName == null)
        {
            Logger.LogError("No configurations defined in {File}.", ProjectConfig.FileName);
            return null;
        }

        BuildConfiguration config;
        try
        {
            config = BuildConfiguration.Create(projectConfig, configName, projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to resolve configuration '{Name}': {Message}", configName, ex.Message);
            return null;
        }

        if (build)
        {
            var toolchain = new Toolchain(config.Settings.Scalar("gcc.toolchain-prefix") ?? "", Logger);
            var options = new BuildOptions { Parallelism = Jobs > 0 ? Jobs : Environment.ProcessorCount };
            if (!await GraphRunner.BuildAsync(config, toolchain, Logger, options, cancellationToken: cancellationToken))
                return null;
        }

        return (config, projectRoot);
    }

    protected DeviceSpec? ResolveOperation(BuildConfiguration config, string operation)
    {
        var spec = DeviceSpecResolver.Resolve(config, operation, config.PrimaryOutput, Device);
        if (spec == null)
            Logger.LogError(
                "Configuration '{Config}' has no '{Op}' step. Add one to the board target, e.g.:\n" +
                "  steps:\n    - name: {Op}\n      phase: Device\n      config:\n" +
                "        command: openocd\n        args: '-f board.cfg ...'",
                config.Name, operation);
        return spec;
    }
}

[Command("flash", Description = "Build a configuration and flash its image to the device")]
public class FlashCommand : DeviceCommandBase
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (await ResolveAsync(build: true, cancellationToken) is not var (config, projectRoot) ||
            ResolveOperation(config, "flash") is not { } spec)
            return 1;

        Logger.LogInformation("");
        Logger.LogInformation("Flashing: {Program} {Args}", spec.Program, string.Join(' ', spec.Args));
        return await Interactive.RunAsync(spec.Program, spec.Args, projectRoot, cancellationToken);
    }
}

[Command("erase", Description = "Erase the target device")]
public class EraseCommand : DeviceCommandBase
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        // No build needed to erase; the configuration just selects the board.
        if (await ResolveAsync(build: false, cancellationToken) is not var (config, projectRoot) ||
            ResolveOperation(config, "erase") is not { } spec)
            return 1;

        Logger.LogInformation("Erasing: {Program} {Args}", spec.Program, string.Join(' ', spec.Args));
        return await Interactive.RunAsync(spec.Program, spec.Args, projectRoot, cancellationToken);
    }
}

[Command("debug", Description = "Build, start the gdb server, and attach gdb to the device")]
public class DebugCommand : DeviceCommandBase
{
    [Option("--server-only", Description = "Only run the gdb server (for an IDE/extension to attach)")]
    public bool ServerOnly { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (await ResolveAsync(build: true, cancellationToken) is not var (config, projectRoot))
            return 1;

        // Black Magic Probe: the probe IS the gdb server on a serial port - no
        // server process; attach gdb directly (matching the minute-debug flow:
        // target extended-remote, swdp_scan, attach 1).
        if (string.Equals(config.Settings.Scalar("debug.server"), "bmp", StringComparison.OrdinalIgnoreCase))
        {
            var bmpPort = config.Settings.Scalar("bmp.port") ?? Bmp.FindPort();
            if (bmpPort == null)
            {
                Logger.LogError("Failed to autodetect the Black Magic Probe port; set `bmp.port` or connect the probe.");
                return 1;
            }
            var bmpPower = config.Settings.Scalar("bmp.power") is "true" or "on" or "1";
            var bmpGdb = (config.Settings.Scalar("gcc.toolchain-prefix") ?? "") + "gdb";
            var attachArgs = Bmp.GdbAttachArgs(bmpPort, bmpPower, config.PrimaryOutput);

            Logger.LogInformation("Attaching to BMP: {Gdb} {Args}", bmpGdb, string.Join(' ', attachArgs));
            return await Interactive.RunAsync(bmpGdb, attachArgs, projectRoot, cancellationToken);
        }

        if (ResolveOperation(config, "gdb-server") is not { } spec)
            return 1;

        var port = spec.Config.GetValueOrDefault("gdb-port", "3333");

        if (ServerOnly)
        {
            Logger.LogInformation("gdb server on port {Port}: {Program} {Args}  (Ctrl+C to stop)",
                port, spec.Program, string.Join(' ', spec.Args));
            return await Interactive.RunAsync(spec.Program, spec.Args, projectRoot, cancellationToken);
        }

        Logger.LogInformation("Starting gdb server: {Program} {Args}", spec.Program, string.Join(' ', spec.Args));
        var server = Interactive.Start(spec.Program, spec.Args, projectRoot);
        if (server == null)
        {
            Logger.LogError("Failed to start '{Program}'.", spec.Program);
            return 1;
        }

        try
        {
            // The client: <toolchain-prefix>gdb by default, overridable per step.
            var gdb = spec.Config.GetValueOrDefault("gdb",
                (config.Settings.Scalar("gcc.toolchain-prefix") ?? "") + "gdb");
            var gdbArgs = new List<string> { config.PrimaryOutput, "-ex", $"target extended-remote localhost:{port}" };

            Logger.LogInformation("Attaching: {Gdb} {Args}", gdb, string.Join(' ', gdbArgs));
            return await Interactive.RunAsync(gdb, gdbArgs, projectRoot, cancellationToken);
        }
        finally
        {
            Interactive.Kill(server);
        }
    }
}
