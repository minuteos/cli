using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug.Servers;

/// <summary>What the server learned about the target while attaching.</summary>
public sealed record TargetInfo(string? Model = null);

/// <summary>
/// A GDB server the debug session connects to - the port of the extension's
/// <c>GdbServer</c> contract. <see cref="StartAsync"/> makes <see cref="Address"/>
/// available; <see cref="AttachAsync"/> runs after gdb selects the target
/// (monitor commands etc.).
/// </summary>
public interface IGdbServer : IAsyncDisposable
{
    /// <summary>The `target extended-remote` address (host:port or a serial device).</summary>
    string Address { get; }

    /// <summary>True when the server loads the program itself (qemu -kernel).</summary>
    bool SkipLoad { get; }

    /// <summary>Something uniquely identifying the target device (for smart-load).</summary>
    string? Identity { get; }

    /// <summary>Server console output, line by line (forwarded to the DAP console).</summary>
    event Action<string>? Output;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<TargetInfo> AttachAsync(MiClient mi, CancellationToken cancellationToken = default);
}

public static class GdbServerFactory
{
    /// <summary>
    /// Creates a server from the launch configuration's <c>server</c> value -
    /// either a preset name ("qemu", "bmp") or an inline object with a
    /// <c>type</c> property, matching the extension's configuration shape.
    /// </summary>
    public static IGdbServer Create(JsonNode server, string program, string? cwd, ILogger logger)
    {
        var config = server as JsonObject ?? new JsonObject { ["type"] = server.GetValue<string>() };
        var type = config["type"]?.GetValue<string>() ?? "";
        return type.ToLowerInvariant() switch
        {
            "qemu" => new QemuGdbServer(config, program, cwd, logger),
            "bmp" => new BmpGdbServer(config, logger),
            "renode" => new RenodeGdbServer(config, cwd, logger),
            _ => throw new NotSupportedException(
                $"GDB server type '{type}' is not supported by `minuteos dap` (supported: qemu, bmp, renode)"),
        };
    }
}

/// <summary>Base for servers that run as a child process.</summary>
public abstract class ProcessGdbServer(ILogger logger) : IGdbServer
{
    private Process? _process;
    protected ILogger Logger { get; } = logger;

    public abstract string Address { get; }
    public virtual bool SkipLoad => false;
    public virtual string? Identity => null;

    public event Action<string>? Output;

    protected abstract (string Executable, List<string> Args) GetCommandLine();
    protected virtual string? WorkingDirectory => null;

    protected bool ProcessRunning => _process is { HasExited: false };

    public virtual Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartProcess();
        return Task.CompletedTask;
    }

    protected void StartProcess()
    {
        var (executable, args) = GetCommandLine();
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Logger.LogDebug("Starting GDB server: {Exe} {Args}", executable, string.Join(' ', args));
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable}");
        Forward(_process.StandardOutput);
        Forward(_process.StandardError);

        void Forward(StreamReader reader) => _ = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync() is { } line)
                Output?.Invoke(line);
        }, CancellationToken.None);
    }

    public virtual Task<TargetInfo> AttachAsync(MiClient mi, CancellationToken cancellationToken = default)
        => Task.FromResult(new TargetInfo());

    /// <summary>Runs before the server process is killed (graceful-shutdown hook).</summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync();
        var process = _process;
        _process = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            process.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}

internal static class TcpPort
{
    public static int Allocate()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// QEMU as a GDB server: <c>qemu-system-arm -machine ... -semihosting
/// -nographic -gdb tcp:... -kernel program -S</c>. The program is loaded by
/// qemu itself (<see cref="SkipLoad"/>).
/// </summary>
public sealed class QemuGdbServer : ProcessGdbServer
{
    private const string DefaultMachine = "netduinoplus2";

    private readonly JsonObject _config;
    private readonly string _program;
    private readonly string? _cwd;
    private readonly string _address;

    public QemuGdbServer(JsonObject config, string program, string? cwd, ILogger logger)
        : base(logger)
    {
        _config = config;
        _program = program;
        _cwd = cwd;
        _address = $"127.0.0.1:{TcpPort.Allocate()}";
    }

    public override string Address => _address;
    public override bool SkipLoad => true;
    protected override string? WorkingDirectory => _cwd;

    protected override (string, List<string>) GetCommandLine()
    {
        var args = new List<string>
        {
            "-machine", _config["machine"]?.GetValue<string>() ?? DefaultMachine,
        };
        if (_config["cpu"]?.GetValue<string>() is { } cpu)
            args.AddRange(["-cpu", cpu]);
        args.AddRange(["-semihosting", "-nographic", "-gdb", $"tcp:{_address}", "-kernel", _program, "-S"]);
        return (_config["executable"]?.GetValue<string>() ?? "qemu-system-arm", args);
    }
}

/// <summary>
/// The Black Magic Probe: the probe *is* the GDB server on a serial port.
/// Attach = optional <c>monitor tpwr</c>, <c>monitor swdp_scan</c>,
/// <c>attach 1</c>, <c>monitor uid</c> - the extension's flow.
/// </summary>
public sealed class BmpGdbServer(JsonObject config, ILogger logger) : IGdbServer
{
    private string _port = "";
    private string? _uid;

    public string Address => _port;
    public bool SkipLoad => false;
    public string Identity => _uid ?? _port;

    public event Action<string>? Output;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _port = config["port"]?.GetValue<string>()
            ?? MinuteOS.Build.Bmp.FindPort()
            ?? throw new InvalidOperationException(
                "Failed to autodetect BMP port.\n\nAre you sure you have a BMP connected?");
        logger.LogInformation("Using serial port {Port}", _port);
        return Task.CompletedTask;
    }

    public async Task<TargetInfo> AttachAsync(MiClient mi, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Scanning targets...");

        if (config["power"] is { } power)
            await mi.MonitorAsync("tpwr " + (power.GetValue<bool>() ? "enable" : "disable"), cancellationToken);

        var scan = await mi.MonitorAsync("swdp_scan", cancellationToken);
        var lines = (scan.Output ?? "").Split('\n');
        if (lines.Length < 2 || lines[1].Trim() != "Available Targets:")
            throw new InvalidOperationException($"BMP swdp_scan failed:\n\n{scan.Output}");

        Output?.Invoke(lines[0]);
        var targets = lines.Skip(3).Where(l => l.Length > 0).ToList();
        logger.LogInformation("Detected targets: {Targets}", string.Join("; ", targets));
        var model = targets.FirstOrDefault()
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);

        await mi.ExecuteAsync("target-attach 1", cancellationToken);
        _uid = (await mi.MonitorAsync("uid", cancellationToken)).Output?.Trim();

        return new TargetInfo(model);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
