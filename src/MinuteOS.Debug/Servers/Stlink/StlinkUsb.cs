using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Usb;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// The ST-Link's command transport: a claimed USB interface with one bulk OUT
/// (16-byte commands + write payloads) and one bulk IN (responses) endpoint, on
/// top of the shared <see cref="UsbBulkInterface"/>.
/// </summary>
internal sealed class StlinkUsb : IDisposable
{
    // ST-Link VID and the known debug PIDs (V2, V2-1, V3 variants).
    public const ushort VendorId = 0x0483;
    public static readonly ushort[] ProductIds = [0x3748, 0x374b, 0x3752, 0x374d, 0x374e, 0x374f, 0x3753, 0x3754];

    private readonly UsbBulkInterface _usb;
    private readonly UsbEndpointWriter _writer;
    private readonly UsbEndpointReader _reader;

    public string? Serial => _usb.Serial;

    private StlinkUsb(UsbBulkInterface usb)
    {
        _usb = usb;
        _writer = usb.OpenWriter();
        _reader = usb.OpenReader(8192);
    }

    public static StlinkUsb Open(ushort vendorId, IReadOnlyList<ushort> productIds, string? serial, ILogger logger)
    {
        var usb = UsbBulkInterface.Claim(vendorId, productIds, serial,
            i => UsbBulkInterface.HasBulkIn(i) && UsbBulkInterface.HasBulkOut(i), "ST-Link", logger);
        try
        {
            return new StlinkUsb(usb);
        }
        catch
        {
            usb.Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data, int timeout = 1000)
    {
        var error = _writer.Write(data.ToArray(), timeout, out _);
        if (error != Error.Success)
            throw new IOException($"ST-Link USB write failed: {error}");
    }

    /// <summary>Reads one response of <paramref name="length"/> bytes, or null on timeout.</summary>
    public byte[]? Read(int length, int timeout = 1000)
    {
        var buffer = new byte[length];
        var error = _reader.Read(buffer, timeout, out var count);
        if (error == Error.Timeout)
            return null;
        if (error != Error.Success)
            throw new IOException($"ST-Link USB read failed: {error}");
        return count == length ? buffer : buffer[..count];
    }

    public void Dispose() => _usb.Dispose();
}
