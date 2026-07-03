using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Usb;

/// <summary>
/// A claimed USB interface over libusb: finds a device by VID/PID (and optional
/// serial), claims the first interface a selector accepts, and owns the
/// context/device/interface lifecycle. Callers open the bulk endpoints they
/// need. Shared by the SWO trace capture (<c>UsbTraceStream</c>) and the ST-Link
/// transport (<c>StlinkUsb</c>) so the enumeration/claim/teardown lives once.
/// </summary>
internal sealed class UsbBulkInterface : IDisposable
{
    private readonly UsbContext _context;
    private readonly IUsbDevice _device;
    private readonly int _number;

    public string? Serial { get; }
    public UsbInterfaceInfo Info { get; }

    private UsbBulkInterface(UsbContext context, IUsbDevice device, UsbInterfaceInfo info, string? serial)
    {
        _context = context;
        _device = device;
        Info = info;
        _number = info.Number;
        Serial = serial;
    }

    /// <summary>Finds and claims the first interface accepted by <paramref name="select"/>.</summary>
    public static UsbBulkInterface Claim(ushort vendorId, IReadOnlyList<ushort> productIds, string? serial,
        Func<UsbInterfaceInfo, bool> select, string description, ILogger logger)
    {
        var context = new UsbContext();
        try
        {
            var device = context.Find(d => d.VendorId == vendorId && productIds.Contains((ushort)d.ProductId)
                    && (serial == null || SerialOf(d) == serial))
                ?? throw new InvalidOperationException(
                    $"No {description} found - is it connected" + (serial != null ? $" (serial {serial})?" : "?"));
            device.Open();

            foreach (var cfg in device.Configs)
            {
                foreach (var iface in cfg.Interfaces)
                {
                    if (!select(iface))
                        continue;

                    device.SetConfiguration(cfg.ConfigurationValue);
                    device.ClaimInterface(iface.Number);
                    device.SetAltInterface(iface.AlternateSetting);
                    logger.LogInformation("{Description} on USB {Vid:x4}:{Pid:x4}, interface {If}",
                        description, vendorId, device.ProductId, iface.Number);
                    return new UsbBulkInterface(context, device, iface, SerialOf(device));
                }
            }

            throw new InvalidOperationException($"{description} has no matching USB interface");
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public UsbEndpointReader OpenReader(int bufferSize)
        => _device.OpenEndpointReader((ReadEndpointID)Endpoint(input: true), bufferSize, EndpointType.Bulk);

    public UsbEndpointWriter OpenWriter()
        => _device.OpenEndpointWriter((WriteEndpointID)Endpoint(input: false), EndpointType.Bulk);

    public static bool HasBulkIn(UsbInterfaceInfo info) => info.Endpoints.Any(e => IsIn(e) && IsBulk(e));
    public static bool HasBulkOut(UsbInterfaceInfo info) => info.Endpoints.Any(e => !IsIn(e) && IsBulk(e));

    private byte Endpoint(bool input) => Info.Endpoints.First(e => IsIn(e) == input && IsBulk(e)).EndpointAddress;

    private static bool IsIn(UsbEndpointInfo e) => (e.EndpointAddress & 0x80) != 0;
    private static bool IsBulk(UsbEndpointInfo e) => (e.Attributes & 0x3) == (byte)EndpointType.Bulk;

    private static string? SerialOf(IUsbDevice device)
    {
        try
        {
            var wasOpen = device.IsOpen;
            if (!wasOpen)
                device.Open();
            var serial = device.Info.SerialNumber;
            if (!wasOpen)
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
        try { _device.ReleaseInterface(_number); } catch { /* already gone */ }
        _device.Dispose();
        _context.Dispose();
    }
}
