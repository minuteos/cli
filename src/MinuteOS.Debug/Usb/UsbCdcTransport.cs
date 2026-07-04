using System.Text;
using System.Threading.Channels;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;

namespace MinuteOS.Debug.Usb;

/// <summary>
/// The STLINK-V3PWR SMU control connection over libusb - the cross-platform
/// (Windows included) transport that replaces opening the device's CDC virtual
/// serial port as a tty. It claims the CDC-Data interface (bulk IN/OUT) on the
/// same USB stack as the ST-Link probe and SWO capture, so there is no
/// <c>stty</c> / <c>/dev/serial</c> dependency.
///
/// LibUsbDotNet is synchronous, so one dedicated thread services the blocking
/// bulk-IN reads and frames the byte stream into lines for an async
/// <see cref="Channel{T}"/>; <see cref="ReadLineAsync"/> awaits that channel, so
/// the SMU sample loop stays completion-driven and parks no thread of its own.
/// EXPERIMENTAL / hardware-unverified, like the rest of the V3PWR ascii path.
/// </summary>
public sealed class UsbCdcTransport : ISmuTransport
{
    // The STLINK-V3PWR power-monitor USB function.
    public const ushort VendorId = 0x0483;
    public const ushort ProductId = 0x3757;

    private readonly UsbBulkInterface _usb;
    private readonly UsbEndpointWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Thread _pump;
    private readonly ILogger _logger;

    private UsbCdcTransport(UsbBulkInterface usb, ILogger logger)
    {
        _usb = usb;
        _writer = usb.OpenWriter();
        _logger = logger;
        _pump = new Thread(() => Pump(usb.OpenReader(4096)))
        {
            IsBackground = true,
            Name = "smu-usb-pump",
        };
        _pump.Start();
    }

    public static UsbCdcTransport Open(string? serial, ILogger logger)
    {
        var usb = UsbBulkInterface.Claim(VendorId, [ProductId], serial,
            i => UsbBulkInterface.HasBulkIn(i) && UsbBulkInterface.HasBulkOut(i), "STLINK-V3PWR SMU", logger);
        try
        {
            try
            {
                usb.SetControlLineState(dtr: true, rts: true);
            }
            catch (Exception ex)
            {
                logger.LogDebug("SMU: asserting DTR/RTS failed ({Message}); continuing", ex.Message);
            }
            return new UsbCdcTransport(usb, logger);
        }
        catch
        {
            usb.Dispose();
            throw;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        // libusb writes are synchronous; commands are infrequent and tiny, so a
        // brief pool-thread offload keeps the caller non-blocking.
        return Task.Run(() =>
        {
            var error = _writer.Write(bytes, 1000, out _);
            if (error != Error.Success)
                throw new IOException($"SMU USB write failed: {error}");
        }, cancellationToken);
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null; // pump ended - stream closed
        }
    }

    private void Pump(UsbEndpointReader reader)
    {
        var buffer = new byte[4096];
        var pending = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var error = reader.Read(buffer, 100, out var count);
                if (error == Error.Timeout)
                    continue;
                if (error != Error.Success)
                {
                    _logger.LogDebug("SMU USB read ended: {Error}", error);
                    break;
                }

                pending.Append(Encoding.ASCII.GetString(buffer, 0, count));
                while (TryTakeLine(pending, out var line))
                    _lines.Writer.TryWrite(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SMU USB pump error");
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    private static bool TryTakeLine(StringBuilder pending, out string line)
    {
        var text = pending.ToString();
        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            line = "";
            return false;
        }
        line = text[..newline].TrimEnd('\r');
        pending.Remove(0, newline + 1);
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (!_pump.Join(TimeSpan.FromSeconds(1)))
            _logger.LogDebug("SMU USB pump did not stop in time");
        _usb.Dispose();
        _cts.Dispose();
    }
}
