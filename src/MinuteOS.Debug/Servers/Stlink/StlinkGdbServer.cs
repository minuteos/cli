using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// An <see cref="IGdbServer"/> that *is* the GDB server: it drives an ST-Link
/// over USB and hosts an in-process GDB remote server (<see
/// cref="InternalGdbServer"/>) for gdb to attach to - no external server
/// process. This is the port of the extension's <c>StlinkGdbServer</c>, and a
/// test that a new server type plugs into the factory / Probe / session with no
/// changes to their contracts.
///
/// EXPERIMENTAL: ported from an incomplete WIP branch, not hardware-verified.
/// </summary>
public sealed class StlinkGdbServer : InternalGdbServer, IGdbServer
{
    private readonly JsonObject _config;
    private StlinkDap? _dap;
    private IDebugTarget? _target;

    public StlinkGdbServer(JsonObject config, ILogger logger) : base(logger) => _config = config;

    public bool SkipLoad => false;
    public string? Identity => _target?.Info.Uid;

    public event Action<string>? Output;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var serial = _config["port"]?.GetValue<string>() ?? _config["serial"]?.GetValue<string>();
        var vendorId = Usb.UsbIds.Parse(_config["vid"]) ?? StlinkUsb.VendorId;
        var productIds = Usb.UsbIds.Parse(_config["pid"]) is { } pid ? [pid] : StlinkUsb.ProductIds;

        var usb = StlinkUsb.Open(vendorId, productIds, serial, Logger);
        _dap = new StlinkDap(usb, Logger);
        return StartListenerAsync();
    }

    public async Task<TargetInfo> AttachAsync(MiClient mi, CancellationToken cancellationToken = default)
    {
        await _dap!.ConnectAsync(cancellationToken);
        _target = await CortexProbe.ProbeAsync(_dap, cancellationToken);
        Output?.Invoke($"ST-Link: attached to {_target.Info.Device}");
        await mi.ExecuteAsync("target-attach 1", cancellationToken);
        return new TargetInfo(_target.Info.Device);
    }

    protected override Task<IDebugTarget> GetTargetAsync(int pid, CancellationToken cancellationToken)
        => Task.FromResult(_target ?? throw new InvalidOperationException("ST-Link target not probed yet"));

    public async ValueTask DisposeAsync()
    {
        await StopListenerAsync();
        if (_dap != null)
            await _dap.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
