using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Usb;

/// <summary>
/// An async <see cref="Stream"/> over a claimed USB interface's bulk endpoints,
/// backed by libusb's asynchronous transfer API (submit + native completion
/// callback resolving a Task) - so a read or write awaits without a thread
/// blocked on the transfer.
///
/// libusb drives those completions from one event-loop thread per USB context,
/// started here and shared by every endpoint on the context. So this is not
/// zero threads: it is one shared pump instead of one blocking thread per
/// stream - the standard libusb async model, which scales to many endpoints on
/// a single background thread.
///
/// Reusable by any bulk USB consumer (the SMU control channel, SWO trace
/// capture, ...). It owns the underlying <see cref="UsbBulkInterface"/>. The
/// stream is async-only: the synchronous <see cref="Read"/>/<see cref="Write"/>
/// throw, so nothing accidentally blocks a thread with sync-over-async.
/// </summary>
public sealed class UsbBulkStream : Stream
{
    private readonly UsbBulkInterface _usb;
    private readonly UsbEndpointReader? _reader;
    private readonly UsbEndpointWriter? _writer;
    private readonly int _timeoutMs;

    private UsbBulkStream(UsbBulkInterface usb, int readBufferSize, int timeoutMs)
    {
        _usb = usb;
        _timeoutMs = timeoutMs;
        if (UsbBulkInterface.HasBulkIn(usb.Info))
            _reader = usb.OpenReader(readBufferSize);
        if (UsbBulkInterface.HasBulkOut(usb.Info))
            _writer = usb.OpenWriter();
        usb.StartEventHandling(); // async transfers only complete while the event loop runs
    }

    /// <summary>
    /// Finds and claims the first interface accepted by <paramref name="select"/>
    /// on the matching device and opens its bulk endpoints as an async stream.
    /// <paramref name="timeoutMs"/> bounds each underlying transfer and sets the
    /// cancellation-poll granularity of <see cref="ReadAsync"/>.
    /// </summary>
    public static UsbBulkStream Claim(ushort vendorId, IReadOnlyList<ushort> productIds, string? serial,
        Func<UsbInterfaceInfo, bool> select, string description, ILogger logger,
        int readBufferSize = 4096, int timeoutMs = 100)
    {
        var usb = UsbBulkInterface.Claim(vendorId, productIds, serial, select, description, logger);
        try
        {
            return new UsbBulkStream(usb, readBufferSize, timeoutMs);
        }
        catch
        {
            usb.Dispose();
            throw;
        }
    }

    /// <summary>The claimed interface, for control transfers and identity (serial).</summary>
    internal UsbBulkInterface Interface => _usb;

    public override bool CanRead => _reader != null;
    public override bool CanWrite => _writer != null;
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_reader == null)
            throw new NotSupportedException("This USB interface has no bulk IN endpoint");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (error, count) = await _reader.ReadAsync(buffer, _timeoutMs);
            if (error == Error.Timeout)
                continue; // no data in the poll window - re-check cancellation and resubmit
            if (error != Error.Success)
                throw new IOException($"USB bulk read failed: {error}");
            return count;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_writer == null)
            throw new NotSupportedException("This USB interface has no bulk OUT endpoint");
        cancellationToken.ThrowIfCancellationRequested();
        var (error, _) = await _writer.WriteAsync(buffer, _timeoutMs);
        if (error != Error.Success)
            throw new IOException($"USB bulk write failed: {error}");
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Async-only. The byte[] async overloads forward to the Memory ones above on
    // modern .NET; only the synchronous entry points are refused.
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("UsbBulkStream is async-only; use ReadAsync");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("UsbBulkStream is async-only; use WriteAsync");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _usb.StopEventHandling(); } catch { /* best effort */ }
            _usb.Dispose();
        }
        base.Dispose(disposing);
    }
}
