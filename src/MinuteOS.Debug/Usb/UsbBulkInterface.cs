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
        // Prefer the libusb we ship embedded (single-file / AOT builds), so USB
        // access works with no system libusb install; no-ops (system libusb takes
        // over) when there's no embedded copy for this platform.
        EmbeddedNativeLibrary.Ensure("libusb-1.0", logger);

        var context = new UsbContext();
        IUsbDevice? device = null;
        try
        {
            device = context.Find(d => d.VendorId == vendorId && productIds.Contains((ushort)d.ProductId)
                    && (serial == null || SerialOf(d) == serial))
                ?? throw new InvalidOperationException(
                    $"No {description} found - is it connected" + (serial != null ? $" (serial {serial})?" : "?"));
            device.Open();

            // Let libusb take an interface back from a kernel driver (e.g. Linux
            // cdc_acm binding a CDC serial function). No-op / unsupported on
            // Windows, where WinUSB owns the interface outright.
            try { (device as UsbDevice)?.SetAutoDetachKernelDriver(true); }
            catch { /* unsupported on this platform - the claim below will tell us */ }

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
            // The opened device is separately owned (see Dispose), so close it too
            // - disposing only the context would leak the handle / claimed interface.
            device?.Dispose();
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// CDC SET_CONTROL_LINE_STATE - asserts DTR/RTS so a CDC-ACM device starts
    /// transmitting (the kernel driver would normally do this on tty open).
    /// Best-effort; some devices/stacks don't require it.
    /// </summary>
    public void SetControlLineState(bool dtr, bool rts)
    {
        // SET_CONTROL_LINE_STATE (bmRequestType 0x21, bRequest 0x22) is a
        // Communications-interface request. We claim the CDC-Data interface (it
        // owns the bulk endpoints), and in the standard CDC-ACM function the paired
        // control interface immediately precedes it, so target _number - 1.
        var commsInterface = _number > 0 ? _number - 1 : _number;
        var value = (short)((dtr ? 1 : 0) | (rts ? 2 : 0));
        var setup = new UsbSetupPacket(0x21, 0x22, value, (short)commsInterface, 0);
        _device.ControlTransfer(setup, Array.Empty<byte>(), 0, 0);
    }

    /// <summary>
    /// Runs the context's libusb event loop (one shared background thread) so
    /// asynchronous transfers on this device complete. Required before any
    /// <c>*Async</c> transfer; a no-op-to-start for the synchronous transfer API.
    /// </summary>
    public void StartEventHandling() => _context.StartHandlingEvents();

    public void StopEventHandling() => _context.StopHandlingEvents();

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
        var wasOpen = device.IsOpen;
        try
        {
            if (!wasOpen)
                device.Open();
            var serial = device.Info.SerialNumber;
            return string.IsNullOrEmpty(serial) ? null : serial;
        }
        catch
        {
            return null;
        }
        finally
        {
            // Close only if we opened it, even when reading the serial threw -
            // otherwise enumeration leaks the handle of every probed device.
            if (!wasOpen && device.IsOpen)
                device.Close();
        }
    }

    public void Dispose()
    {
        try { _device.ReleaseInterface(_number); } catch { /* already gone */ }
        _device.Dispose();
        _context.Dispose();
    }
}
