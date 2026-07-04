using System.Text;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;

namespace MinuteOS.Debug.Usb;

/// <summary>
/// The STLINK-V3PWR SMU control connection over libusb - the cross-platform
/// (Windows included) transport that replaces opening the device's CDC virtual
/// serial port as a tty. It claims the CDC-Data interface (bulk IN/OUT) on the
/// same USB stack as the ST-Link probe and SWO capture as an async
/// <see cref="UsbBulkStream"/>, so there is no <c>stty</c> / <c>/dev/serial</c>
/// dependency and the read awaits libusb completions rather than parking a
/// thread. This transport only frames the byte stream into lines.
///
/// EXPERIMENTAL / hardware-unverified, like the rest of the V3PWR ascii path.
/// </summary>
public sealed class UsbCdcTransport : ISmuTransport
{
    // The STLINK-V3PWR power-monitor USB function.
    public const ushort VendorId = 0x0483;
    public const ushort ProductId = 0x3757;

    private readonly UsbBulkStream _stream;
    private readonly byte[] _readBuffer = new byte[512];
    private readonly StringBuilder _pending = new();

    private UsbCdcTransport(UsbBulkStream stream)
    {
        _stream = stream;
    }

    public static UsbCdcTransport Open(string? serial, ILogger logger)
    {
        var stream = UsbBulkStream.Claim(VendorId, [ProductId], serial,
            i => UsbBulkInterface.HasBulkIn(i) && UsbBulkInterface.HasBulkOut(i), "STLINK-V3PWR SMU", logger);
        try
        {
            try
            {
                stream.Interface.SetControlLineState(dtr: true, rts: true);
            }
            catch (Exception ex)
            {
                logger.LogDebug("SMU: asserting DTR/RTS failed ({Message}); continuing", ex.Message);
            }
            return new UsbCdcTransport(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        => _stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), cancellationToken).AsTask();

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryTakeLine(out var line))
                return line;

            int count;
            try
            {
                count = await _stream.ReadAsync(_readBuffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            if (count <= 0)
                return null; // stream closed

            _pending.Append(Encoding.ASCII.GetString(_readBuffer, 0, count));
        }
    }

    private bool TryTakeLine(out string line)
    {
        var text = _pending.ToString();
        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            line = "";
            return false;
        }
        line = text[..newline].TrimEnd('\r');
        _pending.Remove(0, newline + 1);
        return true;
    }

    public void Dispose() => _stream.Dispose();
}
