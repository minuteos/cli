using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Mi;

/// <summary>The result of one MI command (^done/^running/^connected/...).</summary>
public sealed class MiResult
{
    public required string Class { get; init; }
    public required JsonObject Results { get; init; }
    /// <summary>@target stream output captured while the command ran (BMP monitor replies).</summary>
    public string? Output { get; set; }
    /// <summary>~console stream output captured while the command ran.</summary>
    public string? Console { get; set; }
    /// <summary>=notify records captured while the command ran.</summary>
    public List<MiRecord>? Notify { get; set; }
}

/// <summary>An MI ^error result.</summary>
public sealed class MiException(MiResult result) : Exception(
    result.Results["msg"]?.GetValue<string>() ?? "GDB error")
{
    public MiResult Result { get; } = result;
}

/// <summary>Per-thread execution state, tracked from =thread-created/exited and *exec records.</summary>
public sealed record MiThreadState(int ThreadId, bool Stopped, string? Reason, JsonObject? Details);

/// <summary>
/// A GDB/MI session - the port of the minute-debug extension's
/// <c>GdbInstance</c>+<c>GdbMi</c>: spawns <c>gdb --interpreter=mi2</c>, runs
/// one token-prefixed command at a time, and tracks per-thread run state from
/// async records so callers can await "all threads stopped".
/// </summary>
public sealed class MiClient : IAsyncDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private Task? _receiver;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private int _nextToken = 1;

    private readonly object _sync = new();
    private PendingCommand? _pending;
    private bool _receiverDead;
    private TaskCompletionSource<bool>? _idleSignal;
    private readonly List<MiThreadState> _threads = [];
    private readonly List<(Func<IReadOnlyList<MiThreadState>, bool> Predicate, TaskCompletionSource Tcs)> _threadWaiters = [];

    private sealed class PendingCommand
    {
        public required int Token { get; init; }
        public required string Command { get; init; }
        public required TaskCompletionSource<MiResult> Tcs { get; init; }
        public string? Output;
        public string? Console;
        public List<MiRecord>? Notify;
    }

    /// <summary>~console / @target / &amp;log stream text (async, outside commands too).</summary>
    public event Action<MiRecordType, string>? Stream;
    /// <summary>=notify records (thread-created, library-loaded, ...).</summary>
    public event Action<MiRecord>? Notify;
    /// <summary>Fired after every thread-state change with the new state.</summary>
    public event Action<IReadOnlyList<MiThreadState>>? ThreadsChanged;
    /// <summary>Fired when the gdb process exits.</summary>
    public event Action? Exited;

    public MiClient(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MiThreadState> Threads
    {
        get { lock (_sync) return _threads.ToList(); }
    }

    public bool Running => _process is { HasExited: false };

    /// <summary>Spawns gdb and waits for the initial prompt.</summary>
    public async Task StartAsync(string gdb, string program, string? cwd, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(gdb)
        {
            ArgumentList = { "--interpreter=mi2", program },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        _logger.LogDebug("Starting {Gdb} --interpreter=mi2 {Program}", gdb, program);
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {gdb}");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            lock (_sync)
            {
                _pending?.Tcs.TrySetException(new InvalidOperationException("GDB exited"));
                _idleSignal?.TrySetException(new InvalidOperationException("GDB exited"));
                _threads.Clear();
                SignalThreadWaitersLocked();
            }
            Exited?.Invoke();
        };
        var stderr = _process.StandardError; // capture: DisposeAsync nulls _process
        _ = Task.Run(async () =>
        {
            while (await stderr.ReadLineAsync() is { } line)
                _logger.LogWarning("gdb: {Line}", line);
        }, CancellationToken.None);
        _receiver = Task.Run(ReceiverAsync, CancellationToken.None);

        if (!await IdleAsync(TimeSpan.FromSeconds(5), cancellationToken))
            throw new TimeoutException("Timeout waiting for GDB to start");
    }

    private async Task ReceiverAsync()
    {
        var stdout = _process!.StandardOutput;
        Exception? fault = null;
        try
        {
            while (await stdout.ReadLineAsync() is { } line)
            {
                // One malformed line must never kill the receiver: if it did, no
                // response would ever be read again and every command would hang.
                try
                {
                    ProcessLine(line.TrimEnd('\r'));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process MI line: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            fault = ex;
            _logger.LogDebug(ex, "MI receiver terminated");
        }

        // The output stream has closed (or the reader faulted): no further result
        // will arrive, so fail the in-flight command and mark the client dead
        // rather than leaving callers awaiting forever.
        lock (_sync)
        {
            _receiverDead = true;
            var ex = fault ?? new InvalidOperationException("GDB output stream closed");
            _pending?.Tcs.TrySetException(ex);
            _idleSignal?.TrySetException(ex);
        }
    }

    private void ProcessLine(string line)
    {
        _logger.LogTrace("mi< {Line}", line);
        var record = MiParser.Parse(line);
        if (record == null)
        {
            if (line.Length > 0)
                _logger.LogDebug("Unparsed MI output: {Line}", line);
            return;
        }

        switch (record.Type)
        {
            case MiRecordType.Prompt:
                lock (_sync)
                    _idleSignal?.TrySetResult(true);
                return;

            case MiRecordType.ConsoleStream or MiRecordType.TargetStream or MiRecordType.LogStream:
                lock (_sync)
                {
                    if (_pending is { } p)
                    {
                        if (record.Type == MiRecordType.TargetStream)
                            p.Output = (p.Output ?? "") + record.Text;
                        else if (record.Type == MiRecordType.ConsoleStream)
                            p.Console = (p.Console ?? "") + record.Text;
                    }
                }
                Stream?.Invoke(record.Type, record.Text ?? "");
                return;

            case MiRecordType.Result:
            {
                PendingCommand? cmd;
                lock (_sync)
                    cmd = _pending;
                if (cmd == null)
                {
                    _logger.LogDebug("MI result without pending command: {Line}", line);
                    return;
                }
                if (record.Token is { } token && token != cmd.Token)
                {
                    // A stale result from a cancelled/previous command. Applying it
                    // to the current pending command would hand the caller another
                    // command's result - drop it and let the real result arrive.
                    _logger.LogWarning("MI token mismatch: got {Got}, expected {Expected}; ignoring stale result",
                        token, cmd.Token);
                    return;
                }

                var result = new MiResult
                {
                    Class = record.Class ?? "",
                    Results = record.Results ?? [],
                    Output = cmd.Output,
                    Console = cmd.Console,
                    Notify = cmd.Notify,
                };
                if (result.Class == "error")
                    cmd.Tcs.TrySetException(new MiException(result));
                else
                    cmd.Tcs.TrySetResult(result);
                return;
            }

            case MiRecordType.Exec:
                OnExecRecord(record);
                return;

            case MiRecordType.Status:
                return; // progress records (e.g. download) - not used yet

            case MiRecordType.Notify:
                OnNotifyRecord(record);
                lock (_sync)
                {
                    if (_pending is { } pendingCmd)
                        (pendingCmd.Notify ??= []).Add(record);
                }
                Notify?.Invoke(record);
                return;
        }
    }

    // #region Thread state (the port of GdbInstance's threads$ subject)

    private void OnNotifyRecord(MiRecord record)
    {
        switch (record.Class)
        {
            case "thread-created":
                if (TryGetThreadId(record.Results?["id"], out var created))
                {
                    lock (_sync)
                    {
                        _threads.Add(new MiThreadState(created, Stopped: true, Reason: "new", Details: null));
                        SignalThreadWaitersLocked();
                    }
                    ThreadsChanged?.Invoke(Threads);
                }
                break;

            case "thread-exited":
                if (TryGetThreadId(record.Results?["id"], out var exited))
                {
                    lock (_sync)
                    {
                        _threads.RemoveAll(t => t.ThreadId == exited);
                        SignalThreadWaitersLocked();
                    }
                    ThreadsChanged?.Invoke(Threads);
                }
                break;
        }
    }

    private void OnExecRecord(MiRecord record)
    {
        // *running,thread-id="all"|"1"  /  *stopped,reason=...,thread-id="1"
        var stopped = record.Class == "stopped";
        var threadId = record.Results?["thread-id"];
        var all = threadId == null || threadId.GetValueKind() == System.Text.Json.JsonValueKind.String
            && threadId.GetValue<string>() == "all";
        TryGetThreadId(threadId, out var id);

        lock (_sync)
        {
            for (var i = 0; i < _threads.Count; i++)
            {
                if (all || _threads[i].ThreadId == id)
                {
                    _threads[i] = _threads[i] with
                    {
                        Stopped = stopped,
                        Reason = record.Results?["reason"]?.ToString(),
                        Details = record.Results,
                    };
                }
            }
            SignalThreadWaitersLocked();
        }
        ThreadsChanged?.Invoke(Threads);
    }

    private static bool TryGetThreadId(JsonNode? node, out int id)
    {
        id = 0;
        return node != null && node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => (id = (int)node.GetValue<double>()) is var _,
            System.Text.Json.JsonValueKind.String => int.TryParse(node.GetValue<string>(), out id),
            _ => false,
        };
    }

    private void SignalThreadWaitersLocked()
    {
        for (var i = _threadWaiters.Count - 1; i >= 0; i--)
        {
            if (_threadWaiters[i].Predicate(_threads))
            {
                _threadWaiters[i].Tcs.TrySetResult();
                _threadWaiters.RemoveAt(i);
            }
        }
    }

    /// <summary>Completes when no thread is running (or on timeout - like the extension, timeouts are soft).</summary>
    public Task WaitThreadsStoppedAsync(int timeoutMs = 5000)
        => WaitThreadsAsync(threads => threads.All(t => t.Stopped), timeoutMs);

    /// <summary>Completes when at least one thread is running.</summary>
    public Task WaitThreadsNotStoppedAsync(int timeoutMs = 5000)
        => WaitThreadsAsync(threads => threads.Any(t => !t.Stopped), timeoutMs);

    private async Task WaitThreadsAsync(Func<IReadOnlyList<MiThreadState>, bool> predicate, int timeoutMs)
    {
        TaskCompletionSource tcs;
        lock (_sync)
        {
            if (predicate(_threads))
                return;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _threadWaiters.Add((predicate, tcs));
        }
        // Soft timeout, mirroring the extension (lastValueFrom with defaultValue).
        await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        lock (_sync)
            _threadWaiters.RemoveAll(w => w.Tcs == tcs);
    }

    // #endregion

    /// <summary>
    /// Executes one MI command (e.g. <c>"break-insert --source main.c --line 10"</c>;
    /// no leading dash) and returns its ^result. One command runs at a time.
    /// </summary>
    public async Task<MiResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        var token = Interlocked.Increment(ref _nextToken);
        var pending = new PendingCommand
        {
            Token = token,
            Command = command,
            Tcs = new TaskCompletionSource<MiResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                if (_receiverDead)
                    throw new InvalidOperationException("GDB output stream has closed");
                _pending = pending;
            }
            var line = $"{token}-{command}";
            _logger.LogTrace("mi> {Line}", line);
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("GDB is not running");
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            return await pending.Tcs.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
                _pending = null;
            _commandGate.Release();
        }
    }

    /// <summary>Runs a CLI console command via <c>-interpreter-exec console</c>.</summary>
    public Task<MiResult> ConsoleAsync(string command, CancellationToken cancellationToken = default)
        => ExecuteAsync($"interpreter-exec console {Quote(command)}", cancellationToken);

    /// <summary>Runs a gdb-server monitor command; the reply arrives as @target output.</summary>
    public Task<MiResult> MonitorAsync(string command, CancellationToken cancellationToken = default)
        => ConsoleAsync($"monitor {command}", cancellationToken);

    public async Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken cancellationToken = default)
    {
        var res = await ExecuteAsync($"data-read-memory-bytes 0x{address:x} {length}", cancellationToken);
        var buffer = new byte[length];
        foreach (var chunk in res.Results["memory"] as JsonArray ?? [])
        {
            if (chunk is not JsonObject o)
                continue;
            var offset = ParseNumber(o["offset"]);
            var contents = o["contents"]?.GetValue<string>() ?? "";
            for (var i = 0; i + 1 < contents.Length && offset + i / 2 < buffer.Length; i += 2)
                buffer[offset + i / 2] = Convert.ToByte(contents.Substring(i, 2), 16);
        }
        return buffer;
    }

    public Task WriteMemoryAsync(ulong address, byte[] data, CancellationToken cancellationToken = default)
        => ExecuteAsync($"data-write-memory-bytes 0x{address:x} {Convert.ToHexStringLower(data)}", cancellationToken);

    /// <summary>Waits for gdb to report idle - "(gdb) " prompt (used only at startup).</summary>
    public async Task<bool> IdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> signal;
        lock (_sync)
            signal = _idleSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            return await signal.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Formats a value as an MI argument, quoting when needed.</summary>
    public static string Quote(string value)
        => value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'')
            ? value
            : '"' + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

    /// <summary>Reads an MI numeric value that may arrive as a number or a (possibly hex) string.</summary>
    public static int ParseNumber(JsonNode? node) => (int)ParseNumberLong(node);

    public static long ParseNumberLong(JsonNode? node)
    {
        if (node == null)
            return 0;
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var value = node.AsValue();
            if (value.TryGetValue(out long l))
                return l;
            if (value.TryGetValue(out int i))
                return i;
            if (value.TryGetValue(out double d))
                return (long)d;
            return 0;
        }
        var s = node.GetValue<string>();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(s[2..], 16)
            : long.TryParse(s, out var n) ? n : 0;
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process == null)
            return;
        _process = null;

        // Serialize the graceful-exit write against any in-flight ExecuteAsync
        // write on the same (non-thread-safe) stdin. If a command is wedged
        // holding the gate, fall through after a short wait and kill.
        var gated = false;
        try { gated = await _commandGate.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* disposed */ }
        try
        {
            if (!process.HasExited)
            {
                // Ask nicely first (lets gdb detach), then make sure.
                try
                {
                    process.StandardInput.WriteLine("-gdb-exit");
                    process.StandardInput.Flush();
                }
                catch { /* stdin may be gone */ }
                if (!process.WaitForExit(2000))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
        finally
        {
            if (gated)
                _commandGate.Release();
        }
        if (_receiver != null)
            await _receiver;
        process.Dispose();
    }
}
