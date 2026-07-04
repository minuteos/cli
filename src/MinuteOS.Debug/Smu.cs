using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;
using MinuteOS.Debug.Trace;

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
    private readonly int _frequencyHz;
    private readonly bool _stopPowerOff;
    private readonly ILogger _logger;

    private SessionSmu(StlinkSmu smu, string output, int frequencyHz, bool stopPowerOff, ILogger logger)
    {
        _smu = smu;
        _output = output;
        _frequencyHz = frequencyHz;
        _stopPowerOff = stopPowerOff;
        _logger = logger;
    }

    /// <summary>
    /// Opens the SMU control connection for the session when the launch
    /// <c>smu</c> value selects one (a type name or an inline object); null when
    /// no SMU is configured. Turns the output on immediately when
    /// <c>startPowerOn</c>. The connection is kept for the session so it can also
    /// stream measurements (see <see cref="CreateSampleSource"/>).
    /// </summary>
    public static async Task<SessionSmu?> CreateAsync(JsonNode? smu, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (smu is null)
            return null;

        var config = smu as JsonObject ?? new JsonObject { ["type"] = smu.GetValue<string>() };
        var type = config["type"]?.GetValue<string>() ?? "";
        if (!string.Equals(type, "stlink", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"SMU type '{type}' is not supported by `minuteos dap` (supported: stlink)");

        var startPowerOn = config["startPowerOn"]?.GetValue<bool>() ?? false;
        var stopPowerOff = config["stopPowerOff"]?.GetValue<bool>() ?? false;
        var brackets = startPowerOn || stopPowerOff;
        var output = config["output"]?.GetValue<string>() ?? "vout";
        var voltage = ParseVoltage(config["voltage"]);
        var frequency = (int?)config["frequency"]?.GetValue<double>() ?? 10_000;

        try
        {
            var port = config["port"]?.GetValue<string>() ?? StlinkSmu.FindPort()
                ?? throw new InvalidOperationException(
                    "Failed to autodetect the STLINK-V3PWR control port; set `smu.port`.");

            logger.LogInformation("SMU: STLINK-V3PWR on {Port}, {Output} @ {Voltage} V", port, output, voltage);
            var driver = new StlinkSmu(new TtyTransport(port), logger);
            try
            {
                await driver.ConfigureAsync(output, voltage, cancellationToken);
                if (startPowerOn)
                    await driver.PowerAsync(output, true, cancellationToken);
            }
            catch
            {
                driver.Dispose();
                throw;
            }
            return new SessionSmu(driver, output, frequency, stopPowerOff, logger);
        }
        catch (Exception ex) when (!brackets)
        {
            // Measurement is optional - don't fail the debug session when the SMU
            // is not connected. Power bracketing (startPowerOn/stopPowerOff) still
            // fails fast, since the target's power depends on it.
            logger.LogWarning("SMU unavailable ({Message}); power monitoring disabled", ex.Message);
            return null;
        }
    }

    /// <summary>A current-measurement stream over this SMU's control connection.</summary>
    public ISmuSampleSource CreateSampleSource()
        => new StlinkSmuSampleSource(_smu, _output, _frequencyHz, _logger);

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

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stopPowerOff)
                await _smu.PowerAsync(_output, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Turning off the SMU failed: {Message}", ex.Message);
        }
        _smu.Dispose();
    }
}
