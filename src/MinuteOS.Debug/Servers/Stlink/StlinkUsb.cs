using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// The ST-Link's command transport: a claimed USB interface with one bulk OUT
/// (16-byte commands + write payloads) and one bulk IN (responses) endpoint,
/// over libusb. The port of the extension's <c>services/usb.ts</c> claim for
/// the ST-Link debug interface.
/// </summary>
internal sealed class StlinkUsb : IDisposable
{
    // ST-Link VID and the known debug PIDs (V2, V2-1, V3 variants).
    public const ushort VendorId = 0x0483;
    public static readonly ushort[] ProductIds = [0x3748, 0x374b, 0x3752, 0x374d, 0x374e, 0x374f, 0x3753, 0x3754];

    private readonly UsbContext _context;
    private readonly IUsbDevice _device;
    private readonly int _interface;
    private readonly UsbEndpointWriter _writer;
    private readonly UsbEndpointReader _reader;

    public string? Serial { get; }

    private StlinkUsb(UsbContext context, IUsbDevice device, int iface,
        UsbEndpointWriter writer, UsbEndpointReader reader, string? serial)
    {
        _context = context;
        _device = device;
        _interface = iface;
        _writer = writer;
        _reader = reader;
        Serial = serial;
    }

    public static StlinkUsb Open(ushort vendorId, IReadOnlyList<ushort> productIds, string? serial, ILogger logger)
    {
        var context = new UsbContext();
        try
        {
            var device = context.Find(d => d.VendorId == vendorId && productIds.Contains((ushort)d.ProductId)
                    && (serial == null || SerialOf(d) == serial))
                ?? throw new InvalidOperationException(
                    "No ST-Link found - is one connected" + (serial != null ? $" with serial {serial}?" : "?"));
            device.Open();

            foreach (var cfg in device.Configs)
            {
                foreach (var iface in cfg.Interfaces)
                {
                    var outEp = iface.Endpoints.FirstOrDefault(e => (e.EndpointAddress & 0x80) == 0 && Bulk(e));
                    var inEp = iface.Endpoints.FirstOrDefault(e => (e.EndpointAddress & 0x80) != 0 && Bulk(e));
                    if (outEp == null || inEp == null)
                        continue;

                    logger.LogInformation("ST-Link on USB {Vid:x4}:{Pid:x4}, interface {If}, OUT 0x{Out:x2} / IN 0x{In:x2}",
                        vendorId, device.ProductId, iface.Number, outEp.EndpointAddress, inEp.EndpointAddress);
                    device.SetConfiguration(cfg.ConfigurationValue);
                    device.ClaimInterface(iface.Number);
                    var writer = device.OpenEndpointWriter((WriteEndpointID)outEp.EndpointAddress, EndpointType.Bulk);
                    var reader = device.OpenEndpointReader((ReadEndpointID)inEp.EndpointAddress, 8192, EndpointType.Bulk);
                    return new StlinkUsb(context, device, iface.Number, writer, reader, SerialOf(device));
                }
            }

            throw new InvalidOperationException("ST-Link device has no bulk IN/OUT debug interface");
        }
        catch
        {
            context.Dispose();
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

    private static bool Bulk(LibUsbDotNet.Info.UsbEndpointInfo e) => (e.Attributes & 0x3) == (byte)EndpointType.Bulk;

    private static string? SerialOf(IUsbDevice device)
    {
        try
        {
            var opened = device.IsOpen;
            if (!opened)
                device.Open();
            var serial = device.Info.SerialNumber;
            if (!opened)
                device.Close();
            return string.IsNullOrEmpty(serial) ? null : serial;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _device.ReleaseInterface(_interface); } catch { /* already gone */ }
        _device.Dispose();
        _context.Dispose();
    }
}
