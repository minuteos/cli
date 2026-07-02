using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Build;

/// <summary>Transport to an SMU's control serial port (abstracted for tests).</summary>
public interface ISmuTransport : IDisposable
{
    void WriteLine(string line);
    string? ReadLine(TimeSpan timeout);
}

/// <summary>
/// STLINK-V3PWR source-measure unit driver - a port of the minute-debug
/// extension's SMU support. The device talks a simple line protocol on its
/// control serial port (USB interface 1, VID:PID 0483:3757): commands are
/// acknowledged with <c>ack ...</c> lines; voltages are sent as millivolts
/// (<c>volt vout 3300m</c>); switches as on/off (<c>pwr vout on</c>).
/// </summary>
public sealed class StlinkSmu : IDisposable
{
    private readonly ISmuTransport _transport;
    private readonly ILogger _logger;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public StlinkSmu(ISmuTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Finds the SMU's control port: /dev/serial/by-id links containing
    /// STLINK-V3PWR, preferring USB interface 1 (the control channel).
    /// </summary>
    public static string? FindPort()
    {
        const string byId = "/dev/serial/by-id";
        if (!Directory.Exists(byId))
            return null;

        var candidates = Directory.EnumerateFiles(byId)
            .Where(p => Path.GetFileName(p).Contains("STLINK-V3PWR", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetFileName(p).Contains("if01", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        if (candidates.Count == 0)
            return null;
        var info = new FileInfo(candidates[0]);
        return info.LinkTarget != null
            ? Path.GetFullPath(Path.Combine(byId, info.LinkTarget))
            : candidates[0];
    }

    /// <summary>Initializes the session: quiet prompt, binary-hex format, output voltage.</summary>
    public void Configure(string output, double voltage)
    {
        Execute("power_monitor");
        Execute("format", "bin_hexa");
        Execute("volt", output, voltage);
    }

    public void Power(string output, bool on)
    {
        _logger.LogInformation(on ? "Turning on {Output}" : "Turning off {Output}", output.ToUpperInvariant());
        Execute("pwr", output, on);
    }

    /// <summary>
    /// Sends one command and waits for its <c>ack</c>. Argument formatting
    /// matches the extension: numbers become millis with an <c>m</c> suffix,
    /// booleans become on/off.
    /// </summary>
    internal void Execute(params object[] args)
    {
        var command = string.Join(' ', args.Select(FormatArg));
        _logger.LogDebug("SMU> {Command}", command);
        _transport.WriteLine(command);

        var deadline = DateTime.UtcNow + CommandTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = _transport.ReadLine(deadline - DateTime.UtcNow);
            if (line == null)
                break;
            _logger.LogDebug("SMU< {Line}", line);
            if (line.StartsWith("ack ", StringComparison.Ordinal) || line == "ack")
                return;
        }
        throw new TimeoutException($"SMU command timed out: {command}");
    }

    internal static string FormatArg(object arg) => arg switch
    {
        bool b => b ? "on" : "off",
        double d => Math.Round(d * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
        int i => (i * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
        _ => arg.ToString() ?? "",
    };

    public void Dispose() => _transport.Dispose();
}

/// <summary>
/// Raw-tty transport (Linux/macOS): configures the port with stty and uses plain
/// file I/O, avoiding a native serial dependency. The V3PWR ignores the baud rate.
/// </summary>
public sealed class TtyTransport : ISmuTransport
{
    private readonly FileStream _stream;
    private readonly StringBuilder _pending = new();

    public TtyTransport(string port)
    {
        // Raw mode, no echo; baud is irrelevant to the USB CDC device.
        Process.Start(new ProcessStartInfo("stty", ["-F", port, "raw", "-echo", "115200"])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        })?.WaitForExit();

        _stream = new FileStream(port, FileMode.Open, FileAccess.ReadWrite);
    }

    public void WriteLine(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        _stream.Write(bytes);
        _stream.Flush();
    }

    public string? ReadLine(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var buffer = new byte[256];
        while (DateTime.UtcNow < deadline)
        {
            var newline = _pending.ToString().IndexOf('\n');
            if (newline >= 0)
            {
                var line = _pending.ToString(0, newline).TrimEnd('\r');
                _pending.Remove(0, newline + 1);
                return line;
            }

            var readTask = _stream.ReadAsync(buffer.AsMemory());
            if (!readTask.AsTask().Wait(deadline - DateTime.UtcNow))
                return null;
            var n = readTask.Result;
            if (n <= 0)
                return null;
            _pending.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        return null;
    }

    public void Dispose() => _stream.Dispose();
}
