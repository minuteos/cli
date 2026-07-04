using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Controls the target's power through a source-measure unit — currently the
/// STLINK-V3PWR (a port of the minute-debug extension's SMU support). Defaults
/// come from the configuration's <c>smu.*</c> settings.
/// </summary>
[Command("power", Description = "Turn target power on/off via the configured SMU (STLINK-V3PWR)")]
public class PowerCommand : LoggingCommand
{
    [Argument(Description = "on or off")]
    public string State { get; set; } = "";

    [Option("--configuration", "-c", Description = "Configuration (default: the first one)")]
    public string Configuration { get; set; } = "";

    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    [Option("--port", Description = "SMU control serial port (autodetected when omitted)")]
    public string? Port { get; set; }

    [Option("--output", Description = "Output channel (default from smu.output, else vout)")]
    public string? Output { get; set; }

    [Option("--voltage", Description = "Output voltage in volts (default from smu.voltage, else 3.3)")]
    public double Voltage { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        bool on;
        switch (State.ToLowerInvariant())
        {
            case "on": on = true; break;
            case "off": on = false; break;
            default:
                Logger.LogError("Specify 'on' or 'off'.");
                return 1;
        }

        // Settings are optional here - the SMU works without a project too.
        Settings settings = Settings.Empty;
        try
        {
            var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);
            var projectConfig = ProjectConfig.Load(projectRoot);
            var configName = Configuration != "" ? Configuration : projectConfig.ConfigurationNames.FirstOrDefault();
            if (configName != null)
                settings = BuildConfiguration.Create(projectConfig, configName, projectRoot).Settings;
        }
        catch { /* no project - CLI options + defaults suffice */ }

        var output = Output ?? settings.Scalar("smu.output") ?? "vout";
        var voltage = Voltage > 0 ? Voltage
            : double.TryParse(settings.Scalar("smu.voltage"), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
            : 3.3;

        var port = Port ?? settings.Scalar("smu.port") ?? StlinkSmu.FindPort();
        if (port == null)
        {
            Logger.LogError("Failed to autodetect the STLINK-V3PWR control port; use --port or set `smu.port`.");
            return 1;
        }

        Logger.LogInformation("SMU: STLINK-V3PWR on {Port}, {Output} @ {Voltage} V", port, output, voltage);
        try
        {
            using var smu = new StlinkSmu(new TtyTransport(port), Logger);
            await smu.ConfigureAsync(output, voltage, cancellationToken);
            await smu.PowerAsync(output, on, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError("SMU operation failed: {Message}", ex.Message);
            return 1;
        }

        return 0;
    }
}
