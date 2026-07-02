using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug.Servers;

/// <summary>
/// Renode as a GDB server - the port of the extension's <c>RenodeGdbServer</c>.
/// Starts <c>renode --disable-gui -p -P &lt;monitor-port&gt;</c>, drives it over
/// the Telnet monitor (script/commands/machine selection,
/// <c>machine StartGdbServer</c>), and exposes the include/overlay hooks the
/// SWO ITM capture and the framebuffer bridge build on.
/// </summary>
public sealed partial class RenodeGdbServer(JsonObject config, string? cwd, ILogger logger) : ProcessGdbServer(logger)
{
    // Renode's monitor has no structured success/failure channel over telnet;
    // detect its standard error line prefixes.
    [GeneratedRegex("^(There was an error|No such command or device:|Internal error|Bad parameters)", RegexOptions.Multiline)]
    private static partial Regex RenodeErrorRegex();

    // Cold-start Mono can be slow; allow generous startup time.
    private static readonly TimeSpan MonitorConnectTimeout = TimeSpan.FromSeconds(30);

    // Long enough to let renode acknowledge but short enough not to stall teardown.
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(2);

    private readonly int _monitorPort = TcpPort.Allocate();
    private readonly int _gdbPort = TcpPort.Allocate();
    private RenodeMonitor? _monitor;

    public override string Address => $"127.0.0.1:{_gdbPort}";
    public override bool SkipLoad => true;
    protected override string? WorkingDirectory => cwd;

    protected override (string, List<string>) GetCommandLine()
    {
        // --disable-gui: headless; -p: plain output (no ANSI on the control
        // port); -P: listen for Monitor commands on the given TCP port.
        var args = new List<string> { "--disable-gui", "-p", "-P", _monitorPort.ToString() };
        foreach (var extra in config["extraArgs"] as JsonArray ?? [])
        {
            if (extra?.GetValue<string>() is { } arg)
                args.Add(arg);
        }
        return (config["executable"]?.GetValue<string>() ?? "renode", args);
    }

    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartProcess();

        _monitor = new RenodeMonitor("127.0.0.1", _monitorPort, Logger);
        await _monitor.ConnectAsync(MonitorConnectTimeout, cancellationToken);

        if (config["script"]?.GetValue<string>() is { } script)
        {
            Logger.LogInformation("Loading Renode script {Script}", script);
            await RunMonitorAsync($"include @{script}", cancellationToken);
        }

        foreach (var command in config["commands"] as JsonArray ?? [])
        {
            if (command?.GetValue<string>() is { } cmd)
                await RunMonitorAsync(cmd, cancellationToken);
        }

        if (config["machine"]?.GetValue<string>() is { } machine)
            await RunMonitorAsync($"mach set \"{machine}\"", cancellationToken);

        Logger.LogInformation("Starting Renode GDB server on port {Port}", _gdbPort);
        try
        {
            await RunMonitorAsync($"machine StartGdbServer {_gdbPort}", cancellationToken);
        }
        catch (Exception ex)
        {
            // The usual cause is no current machine: unlike QEMU, Renode can't
            // infer a platform - the user must create and select one.
            throw new InvalidOperationException(
                "Could not start the Renode GDB server. Renode needs a machine before debugging: "
                + "point the server's \"script\" at a .resc that runs `mach create`, loads a platform "
                + "description and your program, or supply equivalent \"commands\".\n\n" + ex.Message);
        }

        if (config["display"] is JsonObject display)
            await SetupDisplayAsync(display, cancellationToken);
    }

    /// <summary>
    /// The framebuffer bridge: overlays the MinuteFramebuffer tap onto the
    /// named video peripheral, streaming frames
    /// (<c>[width u32][height u32][byteLength u32]</c> LE + RGBA8888 pixels)
    /// into a local relay hub. A frontend connects to <see cref="DisplayPort"/>
    /// (announced via the <c>minuteos.display</c> DAP event) to receive the
    /// stream - the same socket side-channel the extension uses in-process.
    /// </summary>
    private async Task SetupDisplayAsync(JsonObject display, CancellationToken cancellationToken)
    {
        _displayHub = new TcpRelayHub(Logger);

        // .repl references a peripheral by variable name, not its sysbus path.
        var videoRef = (display["peripheral"]?.GetValue<string>() ?? "sysbus.lcd")
            .Replace("sysbus.", "", StringComparison.Ordinal);

        var dir = Directory.CreateTempSubdirectory("minute-renode-fb-").FullName;
        var cs = Path.Combine(dir, "MinuteFramebuffer.cs");
        var repl = Path.Combine(dir, "overlay.repl");
        await File.WriteAllTextAsync(cs, GetPluginSource("MinuteFramebuffer.cs"), cancellationToken);
        await File.WriteAllTextAsync(repl,
            $"fbBridge: Miscellaneous.MinuteFramebuffer @ none\n" +
            $"    video: {videoRef}\n" +
            $"    port: {_displayHub.Port}\n", cancellationToken);

        await IncludeFileAsync(cs, cancellationToken);
        await LoadPlatformOverlayAsync(repl, cancellationToken);
    }

    private TcpRelayHub? _displayHub;

    /// <summary>The framebuffer stream port, when a display is configured.</summary>
    public int? DisplayPort => _displayHub?.Port;

    public override Task<TargetInfo> AttachAsync(MiClient mi, CancellationToken cancellationToken = default)
        // After StartGdbServer the monitor's context is the machine name - the
        // closest thing to a "model" identifier Renode exposes.
        => Task.FromResult(new TargetInfo(_monitor?.CurrentContext));

    /// <summary>
    /// Compiles and loads a C# peripheral source file into the running
    /// emulator so its types become referenceable from a `.repl`.
    /// </summary>
    public Task IncludeFileAsync(string sourcePath, CancellationToken cancellationToken = default)
        => RunMonitorAsync($"i @{sourcePath}", cancellationToken);

    /// <summary>
    /// Overlays extra peripherals onto the running machine from a `.repl`
    /// file - used to graft the ITM capture / framebuffer peripherals on top
    /// of the user's platform without touching their `.resc`.
    /// </summary>
    public Task LoadPlatformOverlayAsync(string replPath, CancellationToken cancellationToken = default)
        => RunMonitorAsync($"machine LoadPlatformDescription @{replPath}", cancellationToken);

    /// <summary>Reads an embedded Renode plugin peripheral source (e.g. "MinuteItmCapture.cs").</summary>
    public static string GetPluginSource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded Renode plugin '{name}' not found");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return new StreamReader(stream).ReadToEnd();
    }

    private async Task<string> RunMonitorAsync(string command, CancellationToken cancellationToken)
    {
        var monitor = _monitor ?? throw new InvalidOperationException("Renode monitor not initialized");
        var result = await monitor.ExecuteAsync(command, cancellationToken: cancellationToken);
        if (result.Length > 0)
            Logger.LogDebug("renode: {Command} -> {Result}", command, result);
        if (RenodeErrorRegex().IsMatch(result))
            throw new InvalidOperationException($"Renode command failed: {command}\n{result}");
        return result;
    }

    protected override async ValueTask DisposeCoreAsync()
    {
        _displayHub?.Dispose();
        _displayHub = null;
        if (_monitor is { } monitor)
        {
            _monitor = null;
            // Graceful quit first; the base class SIGTERM is the fallback.
            if (ProcessRunning)
                await monitor.QuitAsync(QuitTimeout);
            await monitor.DisposeAsync();
        }
    }
}

/// <summary>
/// A tiny TCP relay: every byte received from any connection is forwarded to
/// all other connections. The Renode framebuffer peripheral connects and
/// writes; any number of frontends connect and read.
/// </summary>
public sealed class TcpRelayHub : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly List<System.Net.Sockets.TcpClient> _clients = [];
    private readonly object _sync = new();
    private bool _done;

    public int Port { get; }

    public TcpRelayHub(ILogger logger)
    {
        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_sync)
                        _clients.Add(client);
                    _ = Task.Run(() => PumpAsync(client, logger));
                }
            }
            catch (Exception ex)
            {
                if (!_done)
                    logger.LogDebug(ex, "Relay hub accept loop finished");
            }
        });
    }

    private async Task PumpAsync(System.Net.Sockets.TcpClient client, ILogger logger)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var n = await stream.ReadAsync(buffer);
                if (n == 0)
                    break;
                List<System.Net.Sockets.TcpClient> others;
                lock (_sync)
                    others = _clients.Where(c => c != client).ToList();
                foreach (var other in others)
                {
                    try
                    {
                        await other.GetStream().WriteAsync(buffer.AsMemory(0, n));
                    }
                    catch
                    {
                        lock (_sync)
                            _clients.Remove(other);
                        other.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Relay hub connection closed");
        }
        finally
        {
            lock (_sync)
                _clients.Remove(client);
            client.Dispose();
        }
    }

    public void Dispose()
    {
        _done = true;
        _listener.Dispose();
        lock (_sync)
        {
            foreach (var client in _clients)
                client.Dispose();
            _clients.Clear();
        }
    }
}
