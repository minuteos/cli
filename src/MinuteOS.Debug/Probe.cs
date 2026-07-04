using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;
using MinuteOS.Debug.Servers;

namespace MinuteOS.Debug;

/// <summary>
/// A debug probe: a GDB instance attached to a GDB server - the port of the
/// extension's <c>Probe</c>. Setup (gdb + server start in parallel,
/// target-select, attach) and the smart-load-aware program download are shared
/// by the DAP session and one-shot operations.
/// </summary>
public sealed class Probe(ProbeConfig config, ILogger logger) : IAsyncDisposable
{
    // Smart-load state: last program hash per target identity (per-process, like the extension).
    private static readonly Dictionary<string, string> LastProgram = [];

    private MiClient? _gdb;
    private IGdbServer? _server;
    private SessionSmu? _smu;

    public MiClient Gdb => _gdb ?? throw new InvalidOperationException("GDB not started");
    public IGdbServer Server => _server ?? throw new InvalidOperationException("Not connected");
    public TargetInfo Target { get; private set; } = new();

    /// <summary>The session SMU connection, when an <c>smu</c> is configured (for power + measurement).</summary>
    public SessionSmu? Smu => _smu;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Target power comes up before gdb attaches so swdp_scan can see the
        // device; it is cut on teardown (see DisposeAsync).
        _smu = await SessionSmu.CreateAsync(config.Smu, logger, cancellationToken);

        _gdb = new MiClient(logger);
        _server = GdbServerFactory.Create(config.Server, config.Program, config.Cwd, logger);
        if (config.ServerOutput is { } serverOutput)
            _server.Output += serverOutput;

        await Task.WhenAll(
            _gdb.StartAsync(config.Gdb, config.Program, config.Cwd, cancellationToken),
            _server.StartAsync(cancellationToken));

        await _gdb.ExecuteAsync("gdb-set mi-async 1", cancellationToken);
        await _gdb.ExecuteAsync("gdb-set mem inaccessible-by-default 0", cancellationToken);
        await _gdb.ExecuteAsync($"target-select extended-remote {MiClient.Quote(Server.Address)}", cancellationToken);

        Target = await _server.AttachAsync(_gdb, cancellationToken);

        await _gdb.WaitThreadsStoppedAsync();
    }

    /// <summary>
    /// Downloads the program, honoring the server's skipLoad and smart-load
    /// (skip when the same binary is already on the same target). Returns true
    /// when the program was actually loaded.
    /// </summary>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Server.SkipLoad)
            return false;

        if (config.SmartLoad && Server.Identity is { } identity && SmartLoadSkip(identity))
        {
            logger.LogInformation("SmartLoad: program already loaded");
            return false;
        }

        await Gdb.ExecuteAsync("target-download", cancellationToken);
        return true;
    }

    private bool SmartLoadSkip(string identity)
    {
        string hash;
        try
        {
            var path = Path.Combine(config.Cwd ?? ".", config.Program);
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            lock (LastProgram)
                LastProgram.Remove(identity);
            return false;
        }

        lock (LastProgram)
        {
            if (LastProgram.TryGetValue(identity, out var last) && last == hash)
                return true;
            LastProgram[identity] = hash;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_gdb != null)
            await _gdb.DisposeAsync();
        if (_server != null)
            await _server.DisposeAsync();
        // Cut target power last, once gdb has detached and the server is gone.
        if (_smu != null)
            await _smu.DisposeAsync();
    }
}

/// <summary>The resolved subset of the launch configuration the probe needs.</summary>
public sealed class ProbeConfig
{
    /// <summary>The program (ELF) path, absolute or relative to <see cref="Cwd"/>.</summary>
    public required string Program { get; init; }
    /// <summary>The gdb executable (e.g. arm-none-eabi-gdb).</summary>
    public required string Gdb { get; init; }
    /// <summary>The `server` config value: a preset name or an inline object.</summary>
    public required JsonNode Server { get; init; }
    /// <summary>The `smu` config value (a preset name or an inline object), or null.</summary>
    public JsonNode? Smu { get; init; }
    public string? Cwd { get; init; }
    public bool SmartLoad { get; init; } = true;
    /// <summary>Receives gdb-server console lines.</summary>
    public Action<string>? ServerOutput { get; init; }
}
