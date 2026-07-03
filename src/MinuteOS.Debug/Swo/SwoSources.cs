using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;
using MinuteOS.Debug.Servers;

namespace MinuteOS.Debug.Swo;

/// <summary>
/// A source of raw SWO bytes - the port of the extension's <c>Swo</c> plugin
/// contract. <see cref="ConnectAsync"/> prepares the transport;
/// <see cref="EnableAsync"/> turns the emission on once gdb is attached.
/// </summary>
public interface ISwoSource : IAsyncDisposable
{
    /// <summary>The SWO byte stream; null when the transport is unavailable.</summary>
    Stream? Stream { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task EnableAsync(IGdbServer server, MiClient mi, CancellationToken cancellationToken = default);
}

public static class SwoSourceFactory
{
    /// <summary>
    /// Creates an SWO source from the launch configuration's <c>swo</c> value
    /// (a type name or an inline object with <c>type</c>), and the parsed
    /// <see cref="SwoConfig"/> for the session.
    /// </summary>
    public static (ISwoSource Source, SwoConfig Config) Create(JsonNode swo, ILogger logger)
    {
        var config = swo as JsonObject ?? new JsonObject { ["type"] = swo.GetValue<string>() };
        var swoConfig = new SwoConfig
        {
            CpuFrequency = ToInt(config["cpuFrequency"]),
            SwvFrequency = ToInt(config["swvFrequency"]),
            Format = ToInt(config["format"]) == 1 ? SwvFormat.Manchester : SwvFormat.Uart,
            PcSample = (config["profile"] ?? config["pcSample"])?.GetValue<bool>() ?? false,
        };
        var type = config["type"]?.GetValue<string>() ?? "";
        ISwoSource source = type.ToLowerInvariant() switch
        {
            "bmp" => new BmpSwo(config, logger),
            "renode" => new RenodeSwo(logger),
            _ => throw new NotSupportedException($"Unsupported SWO source: {type}"),
        };
        return (source, swoConfig);

        static int ToInt(JsonNode? node) => node == null ? 0 : (int)MiClient.ParseNumberLong(node);
    }
}

/// <summary>
/// SWO from a Black Magic Probe. The probe exposes the trace stream on a
/// dedicated USB bulk interface; by default the CLI claims it directly via
/// libusb (the port of the extension's <c>services/usb.ts</c>), so SWO works
/// with no manual setup on the same platforms the extension supported, Windows
/// included. An explicit <c>port</c> (a device path, e.g. one exposed by a udev
/// rule) bypasses the USB claim. Enabling probes the firmware's command set -
/// different BMP versions use <c>swo enable</c> vs <c>traceswo enable</c>.
/// </summary>
public sealed class BmpSwo(JsonObject config, ILogger logger) : ISwoSource
{
    // The BMP trace-capture interface (the extension's built-in BMP SWO preset).
    private const ushort DefaultVendorId = 0x1d50;
    private const ushort DefaultProductId = 0x6018;
    private const string DefaultInterface = "Trace Capture";

    private FileStream? _fileStream;
    private UsbTraceStream? _usb;

    public Stream? Stream => _fileStream ?? _usb?.Stream;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (config["port"]?.GetValue<string>() is { } port)
        {
            _fileStream = new FileStream(port, FileMode.Open, FileAccess.Read);
            logger.LogInformation("SWO stream from {Port}", port);
            return Task.CompletedTask;
        }

        var vendorId = Usb.UsbIds.Parse(config["vid"]) ?? DefaultVendorId;
        var productId = Usb.UsbIds.Parse(config["pid"]) ?? DefaultProductId;
        var interfaceName = config["interface"]?.GetValue<string>() ?? DefaultInterface;
        try
        {
            _usb = UsbTraceStream.Open(vendorId, productId, interfaceName, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "BMP SWO USB capture unavailable ({Message}); set a 'port' device path to read the trace channel. SWO disabled",
                ex.Message);
        }
        return Task.CompletedTask;
    }

    public async Task EnableAsync(IGdbServer server, MiClient mi, CancellationToken cancellationToken = default)
    {
        if (server is not BmpGdbServer)
            logger.LogWarning("Using BMP SWO without BMP as a GDB server - you need to enable the SWO output manually");

        // Different BMP versions use different commands; look at help to know which.
        var help = await mi.MonitorAsync("help", cancellationToken);
        var hasSwoCommand = (help.Output ?? "").Split('\n')
            .Select(line => line.Split(" -- ", 2))
            .Any(parts => parts.Length == 2 && parts[0].Trim() == "swo");
        await mi.MonitorAsync(hasSwoCommand ? "swo enable" : "traceswo enable", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _fileStream?.Dispose();
        if (_usb != null)
            await _usb.DisposeAsync();
    }
}

/// <summary>
/// SWO under Renode. Renode's Cortex-M models omit the CoreSight ITM/DWT/TPIU
/// and the ROM table, so there is no native SWO stream. We compile a small C#
/// peripheral (MinuteItmCapture) into the running emulator and overlay two
/// instances onto the machine, without touching the user's .resc:
///
///  - an ITM block at 0xE0000000 that turns each stimulus-port write into a
///    properly framed ITM source packet and streams it back over a loopback
///    socket, reporting ITM as enabled so the firmware's ITM_SendChar emits;
///  - a ROM table at 0xE00FF000 pointing SCS/DWT/ITM/TPIU at 0xE0000000 so
///    the trace setup's register writes land in the absorbing overlay.
///
/// Neither region is populated by stock Renode platforms, so the overlay does
/// not collide with the user's .repl.
/// </summary>
public sealed class RenodeSwo(ILogger logger) : ISwoSource
{
    private string? _dir;
    private TcpListener? _listener;
    private TcpClient? _client;
    private Task? _acceptor;
    private Pipe? _pipe;

    public Stream? Stream { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        // The C# peripheral connects back once Renode instantiates it; pump
        // the accepted socket into a pipe so the session can read a Stream
        // that exists before the connection does.
        _pipe = new Pipe();
        Stream = _pipe.Reader.AsStream();
        _acceptor = Task.Run(async () =>
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(CancellationToken.None);
                _client = client; // so dispose can sever the pump
                logger.LogDebug("Renode ITM capture connected");
                await client.GetStream().CopyToAsync(_pipe.Writer.AsStream(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Renode SWO acceptor finished");
            }
            finally
            {
                await _pipe.Writer.CompleteAsync();
            }
        }, CancellationToken.None);

        _dir = Directory.CreateTempSubdirectory("minute-renode-swo-").FullName;
        File.WriteAllText(Path.Combine(_dir, "MinuteSwo.cs"), RenodeGdbServer.GetPluginSource("MinuteItmCapture.cs"));
        File.WriteAllText(Path.Combine(_dir, "overlay.repl"),
            $"itmCapture: Miscellaneous.MinuteItmCapture @ sysbus 0xE0000000\n" +
            $"    port: {port}\n" +
            $"\n" +
            $"coresightRomTable: Miscellaneous.MinuteRomTable @ sysbus 0xE00FF000\n");
        return Task.CompletedTask;
    }

    public async Task EnableAsync(IGdbServer server, MiClient mi, CancellationToken cancellationToken = default)
    {
        if (server is not RenodeGdbServer renode)
        {
            logger.LogWarning("Renode SWO requires the Renode GDB server; skipping ITM overlay");
            return;
        }
        if (_dir == null)
            return;
        logger.LogInformation("Overlaying ITM capture peripheral");
        await renode.IncludeFileAsync(Path.Combine(_dir, "MinuteSwo.cs"), cancellationToken);
        await renode.LoadPlatformOverlayAsync(Path.Combine(_dir, "overlay.repl"), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        _client?.Dispose(); // unblocks the pump - renode holds its end open until it exits
        if (_acceptor != null)
            await _acceptor;
        if (_dir != null)
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
