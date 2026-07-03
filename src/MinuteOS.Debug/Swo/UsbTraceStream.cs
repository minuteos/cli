using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Swo;

/// <summary>
/// Reads a raw SWO/trace byte stream from a USB bulk IN endpoint via libusb -
/// the port of the extension's <c>services/usb.ts</c> trace-interface claim, so
/// the Black Magic Probe's SWO works the same way, and on the same platforms
/// (Windows included), without needing a device path. The probe is found by
/// VID/PID, the trace interface by the name in its string descriptor, and its
/// bulk IN endpoint is claimed and pumped into a <see cref="Stream"/>.
/// </summary>
public sealed class UsbTraceStream : IAsyncDisposable
{
    private readonly UsbContext _context;
    private readonly IUsbDevice _device;
    private readonly int _interfaceNumber;
    private readonly CancellationTokenSource _cts = new();
    private readonly Pipe _pipe = new();
    private readonly Task _pump;

    public Stream Stream => _pipe.Reader.AsStream();

    private UsbTraceStream(UsbContext context, IUsbDevice device, int interfaceNumber,
        UsbEndpointReader reader, ILogger logger)
    {
        _context = context;
        _device = device;
        _interfaceNumber = interfaceNumber;
        _pump = Task.Run(() => PumpAsync(reader, logger));
    }

    /// <summary>
    /// Opens the first bulk IN endpoint of the interface whose name contains
    /// <paramref name="interfaceName"/> on the USB device matching
    /// <paramref name="vendorId"/>/<paramref name="productId"/>.
    /// </summary>
    public static UsbTraceStream Open(ushort vendorId, ushort productId, string interfaceName, ILogger logger)
    {
        var context = new UsbContext();
        try
        {
            var device = context.Find(d => d.VendorId == vendorId && d.ProductId == productId)
                ?? throw new InvalidOperationException(
                    $"No USB device {vendorId:x4}:{productId:x4} found - is the probe connected?");
            device.Open();

            foreach (var cfg in device.Configs)
            {
                foreach (var iface in cfg.Interfaces)
                {
                    var name = iface.Interface;
                    if (string.IsNullOrEmpty(name) || !name.Contains(interfaceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ep = iface.Endpoints.FirstOrDefault(e =>
                        (e.EndpointAddress & 0x80) != 0 && (e.Attributes & 0x3) == (byte)EndpointType.Bulk);
                    if (ep == null)
                        continue;

                    logger.LogInformation("SWO from USB {Vid:x4}:{Pid:x4}, interface '{Name}', endpoint 0x{Ep:x2}",
                        vendorId, productId, name, ep.EndpointAddress);
                    device.SetConfiguration(cfg.ConfigurationValue);
                    device.ClaimInterface(iface.Number);
                    device.SetAltInterface(iface.AlternateSetting);
                    var reader = device.OpenEndpointReader((ReadEndpointID)ep.EndpointAddress, 4096, EndpointType.Bulk);
                    return new UsbTraceStream(context, device, iface.Number, reader, logger);
                }
            }

            throw new InvalidOperationException(
                $"USB device {vendorId:x4}:{productId:x4} has no '{interfaceName}' interface with a bulk IN endpoint");
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Parses a `vid`/`pid` config value: a JSON number or a hex/decimal string.</summary>
    public static ushort? ParseId(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return (ushort)node.GetValue<int>();
        }
        catch
        {
            var text = node.ToString().Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ushort.TryParse(hex ? text[2..] : text,
                hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : null;
        }
    }

    private async Task PumpAsync(UsbEndpointReader reader, ILogger logger)
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var error = reader.Read(buffer, 100, out var count);
                if (error == Error.Success)
                {
                    if (count > 0)
                        await _pipe.Writer.WriteAsync(buffer.AsMemory(0, count), _cts.Token);
                }
                else if (error != Error.Timeout)
                {
                    logger.LogDebug("USB trace read ended: {Error}", error);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "USB trace pump error");
        }
        finally
        {
            await _pipe.Writer.CompleteAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _pump; } catch { /* best effort */ }
        try { _device.ReleaseInterface(_interfaceNumber); } catch { /* may already be gone */ }
        _device.Dispose();
        _context.Dispose();
        _cts.Dispose();
    }
}
