using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;

namespace MinuteOS.Debug;

/// <summary>
/// In-session target power via a source-measure unit (STLINK-V3PWR) - the port
/// of the extension's in-session SMU lifecycle. A debug session brackets target
/// power the way standalone <c>minuteos power</c> does one-shot: the output is
/// switched on before the probe connects (when <c>startPowerOn</c>) and off on
/// teardown (when <c>stopPowerOff</c>), reusing the same <see cref="StlinkSmu"/>
/// driver as the command.
/// </summary>
public sealed class SessionSmu : IAsyncDisposable
{
    private readonly StlinkSmu _smu;
    private readonly string _output;
    private readonly bool _stopPowerOff;
    private readonly ILogger _logger;

    private SessionSmu(StlinkSmu smu, string output, bool stopPowerOff, ILogger logger)
    {
        _smu = smu;
        _output = output;
        _stopPowerOff = stopPowerOff;
        _logger = logger;
    }

    /// <summary>
    /// Brackets a session with the launch <c>smu</c> value (a type name or an
    /// inline object). Returns null when no SMU is configured or it asks for no
    /// lifecycle action (neither <c>startPowerOn</c> nor <c>stopPowerOff</c>) -
    /// standalone power control stays the user's job then. Turns the output on
    /// immediately when <c>startPowerOn</c>.
    /// </summary>
    public static SessionSmu? Create(JsonNode? smu, ILogger logger)
    {
        if (smu is null)
            return null;

        var config = smu as JsonObject ?? new JsonObject { ["type"] = smu.GetValue<string>() };
        var type = config["type"]?.GetValue<string>() ?? "";
        if (!string.Equals(type, "stlink", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"SMU type '{type}' is not supported by `minuteos dap` (supported: stlink)");

        var startPowerOn = config["startPowerOn"]?.GetValue<bool>() ?? false;
        var stopPowerOff = config["stopPowerOff"]?.GetValue<bool>() ?? false;
        if (!startPowerOn && !stopPowerOff)
            return null;

        var output = config["output"]?.GetValue<string>() ?? "vout";
        var voltage = ParseVoltage(config["voltage"]);
        var port = config["port"]?.GetValue<string>() ?? StlinkSmu.FindPort()
            ?? throw new InvalidOperationException(
                "Failed to autodetect the STLINK-V3PWR control port; set `smu.port` (or smu.port in the launch configuration).");

        logger.LogInformation("SMU: STLINK-V3PWR on {Port}, {Output} @ {Voltage} V", port, output, voltage);
        var driver = new StlinkSmu(new TtyTransport(port), logger);
        try
        {
            driver.Configure(output, voltage);
            if (startPowerOn)
                driver.Power(output, true);
        }
        catch
        {
            driver.Dispose();
            throw;
        }
        return new SessionSmu(driver, output, stopPowerOff, logger);
    }

    private static double ParseVoltage(JsonNode? node)
    {
        if (node is null)
            return 3.3;
        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return double.TryParse(node.ToString(), CultureInfo.InvariantCulture, out var v) ? v : 3.3;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_stopPowerOff)
                _smu.Power(_output, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Turning off the SMU failed: {Message}", ex.Message);
        }
        _smu.Dispose();
        return ValueTask.CompletedTask;
    }
}
