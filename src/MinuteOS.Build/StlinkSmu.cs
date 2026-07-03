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
    /// Configures ASCII (decimal) streaming at <paramref name="frequencyHz"/> Hz
    /// with unbounded acquisition time. The current then streams as one line per
    /// sample after <see cref="StartStream"/> until <see cref="StopStream"/>.
    /// </summary>
    public void ConfigureStream(int frequencyHz)
    {
        Execute("power_monitor");
        Execute("format", "ascii_dec");
        Execute("freq", frequencyHz.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Execute("acqtime", "0");
    }

    public void StartStream() => Send("start");

    public void StopStream() => Send("stop");

    /// <summary>Sends a raw command line without waiting for an ack (streaming start/stop).</summary>
    public void Send(string command)
    {
        _logger.LogDebug("SMU> {Command}", command);
        _transport.WriteLine(command);
    }

    /// <summary>Reads one line from the device (a sample or metadata), or null on timeout.</summary>
    public string? ReadLine(TimeSpan timeout) => _transport.ReadLine(timeout);

    /// <summary>
    /// Parses one <c>ascii_dec</c> sample line into amperes: the value is
    /// <c>mantissa × 10^±exponent</c> (e.g. <c>"5000-9"</c> = 5 µA). Metadata
    /// lines (<c>TimeStamp…</c>, <c>ack…</c>) and anything else return false.
    /// Matches the LPM01A/PowerShield ascii format the STLINK-V3PWR reuses.
    /// </summary>
    public static bool TryParseAmps(string line, out double amps)
    {
        amps = 0;
        var s = line.Trim();
        // Some formats prefix a record index / timestamp; the value is the last token.
        var space = s.LastIndexOfAny([' ', '\t']);
        if (space >= 0)
            s = s[(space + 1)..];
        if (s.Length == 0)
            return false;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles floatStyle = System.Globalization.NumberStyles.Float;

        // Primary form: integer mantissa, a sign directly after a digit, then an
        // integer exponent ("5000-9" = 5000×10^-9).
        for (var i = 1; i < s.Length; i++)
        {
            if ((s[i] == '+' || s[i] == '-') && char.IsDigit(s[i - 1]))
            {
                if (double.TryParse(s[..i], floatStyle, culture, out var mantissa)
                    && int.TryParse(s[(i + 1)..], out var exponent))
                {
                    amps = mantissa * System.Math.Pow(10, s[i] == '-' ? -exponent : exponent);
                    return true;
                }
                return false;
            }
        }

        // Defensive fallback: a firmware emitting a plain float value in amperes
        // ("1.234e-6", "0.000005"). A bare integer is rejected - it is ambiguous
        // and never the ST format.
        return (s.Contains('.') || s.Contains('e') || s.Contains('E'))
            && double.TryParse(s, floatStyle, culture, out amps);
    }

    /// <summary>
    /// Sends one command and waits for its <c>ack</c>. Argument formatting
    /// matches the extension: numbers become millis with an <c>m</c> suffix,
    /// booleans become on/off.
    /// </summary>
    public void Execute(params object[] args)
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
