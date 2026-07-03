using System.IO.Pipelines;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Usb;

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
    private readonly UsbBulkInterface _usb;
    private readonly CancellationTokenSource _cts = new();
    private readonly Pipe _pipe = new();
    private readonly Task _pump;

    public Stream Stream => _pipe.Reader.AsStream();

    private UsbTraceStream(UsbBulkInterface usb, UsbEndpointReader reader, ILogger logger)
    {
        _usb = usb;
        _pump = Task.Run(() => PumpAsync(reader, logger));
    }

    /// <summary>
    /// Opens the bulk IN endpoint of the interface whose name contains
    /// <paramref name="interfaceName"/> on the device matching
    /// <paramref name="vendorId"/>/<paramref name="productId"/>.
    /// </summary>
    public static UsbTraceStream Open(ushort vendorId, ushort productId, string interfaceName, ILogger logger)
    {
        var usb = UsbBulkInterface.Claim(vendorId, [productId], serial: null,
            i => i.Interface?.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) == true && UsbBulkInterface.HasBulkIn(i),
            $"SWO trace interface '{interfaceName}'", logger);
        try
        {
            return new UsbTraceStream(usb, usb.OpenReader(4096), logger);
        }
        catch
        {
            usb.Dispose();
            throw;
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
        _usb.Dispose();
        _cts.Dispose();
    }
}
