using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Servers;

/// <summary>
/// Telnet client for Renode's monitor (the <c>-P</c> control endpoint) - the
/// port of the extension's <c>RenodeMonitor</c>. Handles Telnet option
/// negotiation transparently and exposes the line-oriented command protocol
/// with command serialization and prompt detection, so callers get one clean
/// text response per command.
/// </summary>
public sealed partial class RenodeMonitor(string host, int port, ILogger logger) : IAsyncDisposable
{
    // Each reply ends with a prompt "(<context>) " with no trailing newline,
    // where <context> is "monitor" or the active machine name.
    [GeneratedRegex(@"\(([^)\r\n]+)\) *$")]
    private static partial Regex PromptRegex();

    // Bounded window for spotting the first prompt in the connect-time banner.
    private const int BannerWindow = 4096;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _receiver;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _sync = new();
    private PendingCommand? _pending;
    private TaskCompletionSource? _readySignal;
    private string _banner = "";
    private bool _done;

    private sealed class PendingCommand
    {
        public required string Command { get; init; }
        public required TaskCompletionSource<string> Tcs { get; init; }
        public string Buffer = "";
    }

    /// <summary>Current monitor context (e.g. "monitor" or the active machine name).</summary>
    public string CurrentContext { get; private set; } = "monitor";

    /// <summary>
    /// Opens the TCP connection, retrying until Renode's control port accepts,
    /// then waits for the first prompt.
    /// </summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (_client == null)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, cancellationToken);
                _client = client;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Failed to connect to Renode monitor at {host}:{port}: {ex.Message}");
                await Task.Delay(100, cancellationToken);
            }
        }

        logger.LogDebug("Connected to Renode monitor at {Host}:{Port}", host, port);
        _stream = _client.GetStream();
        lock (_sync)
            _readySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver = Task.Run(ReceiverAsync, CancellationToken.None);

        // Wait for the initial banner + prompt before letting commands through.
        var remaining = deadline - DateTime.UtcNow;
        try
        {
            await _readySignal!.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Timed out waiting for Renode monitor prompt");
        }
    }

    /// <summary>
    /// Sends a command and resolves with its textual response, stripped of the
    /// echoed command line and the trailing prompt.
    /// </summary>
    public async Task<string> ExecuteAsync(string command, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Renode monitor not connected");

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var pending = new PendingCommand
            {
                Command = command,
                Tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            lock (_sync)
                _pending = pending;

            logger.LogTrace("renode> {Command}", command);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), cancellationToken);

            try
            {
                return await pending.Tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Renode monitor command timed out: {command}");
            }
        }
        finally
        {
            lock (_sync)
                _pending = null;
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Best-effort graceful shutdown: tells Renode to exit and swallows the
    /// resulting timeout/disconnect, since both indicate the process is gone.
    /// </summary>
    public async Task QuitAsync(TimeSpan timeout)
    {
        if (_stream == null || _done)
            return;
        try
        {
            await ExecuteAsync("quit", timeout);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Quit command did not complete cleanly: {Message}", ex.Message);
        }
    }

    private async Task ReceiverAsync()
    {
        var telnet = new TelnetFilter(reply =>
        {
            try { _stream?.Write(reply); }
            catch { /* socket gone */ }
        });
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await _stream!.ReadAsync(buffer);
                if (n == 0)
                    break;
                var clean = telnet.Process(buffer.AsSpan(0, n));
                if (clean.Length == 0)
                    continue;
                var text = Encoding.UTF8.GetString(clean);
                logger.LogTrace("renode< {Text}", text);
                Consume(text);
            }
            logger.LogDebug("Renode monitor receiver finished");
        }
        catch (Exception ex)
        {
            if (!_done)
                logger.LogDebug(ex, "Renode monitor receiver failed");
        }
        finally
        {
            lock (_sync)
            {
                _pending?.Tcs.TrySetException(new InvalidOperationException("Renode monitor disconnected"));
                _readySignal?.TrySetException(new InvalidOperationException("Renode monitor disconnected"));
            }
        }
    }

    private void Consume(string text)
    {
        lock (_sync)
        {
            if (_pending is { } pending)
            {
                pending.Buffer += text;
                var m = PromptRegex().Match(pending.Buffer);
                if (m.Success)
                {
                    CurrentContext = m.Groups[1].Value;
                    var body = pending.Buffer[..m.Index];
                    pending.Tcs.TrySetResult(StripEcho(body, pending.Command));
                }
                return;
            }

            // No command in flight - the connect-time banner. Keep a bounded
            // window so a prompt split across chunks is still detected.
            _banner = (_banner + text).Length <= BannerWindow ? _banner + text : (_banner + text)[^BannerWindow..];
            var banner = PromptRegex().Match(_banner);
            if (banner.Success)
            {
                CurrentContext = banner.Groups[1].Value;
                _banner = "";
                _readySignal?.TrySetResult();
            }
        }
    }

    private static string StripEcho(string body, string command)
    {
        // Renode echoes the command back as the first line of the response.
        var lines = body.Split('\n').ToList();
        if (lines.Count > 0 && lines[0].TrimEnd() == command)
            lines.RemoveAt(0);
        return string.Join('\n', lines).TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        _done = true;
        _client?.Dispose();
        _client = null;
        _stream = null;
        if (_receiver != null)
            await _receiver;
    }
}

/// <summary>
/// Minimal Telnet stream filter: strips IAC command sequences (including the
/// GA Renode emits after each prompt), refuses all option negotiation so the
/// server stops asking, and unescapes literal 0xFF. Partial sequences carry
/// over across chunk boundaries.
/// </summary>
public sealed class TelnetFilter(Action<byte[]> respond)
{
    private const byte Se = 240, Sb = 250, Will = 251, Wont = 252, Do = 253, Dont = 254, Iac = 255;

    private byte[] _leftover = [];

    public byte[] Process(ReadOnlySpan<byte> input)
    {
        var bytes = _leftover.Length > 0 ? [.. _leftover, .. input] : input.ToArray();
        _leftover = [];

        var output = new List<byte>(bytes.Length);
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b != Iac)
            {
                output.Add(b);
                i++;
                continue;
            }

            if (i + 1 >= bytes.Length)
            {
                _leftover = bytes[i..];
                break;
            }

            var cmd = bytes[i + 1];
            if (cmd == Iac)
            {
                output.Add(Iac); // escaped literal 0xFF
                i += 2;
            }
            else if (cmd == Sb)
            {
                // Subnegotiation: skip until IAC SE.
                var j = i + 2;
                while (j + 1 < bytes.Length && !(bytes[j] == Iac && bytes[j + 1] == Se))
                    j++;
                if (j + 1 >= bytes.Length)
                {
                    _leftover = bytes[i..];
                    break;
                }
                i = j + 2;
            }
            else if (cmd is Will or Wont or Do or Dont)
            {
                if (i + 2 >= bytes.Length)
                {
                    _leftover = bytes[i..];
                    break;
                }
                var option = bytes[i + 2];
                // Refuse everything to terminate negotiation without loops.
                if (cmd == Do)
                    respond([Iac, Wont, option]);
                else if (cmd == Will)
                    respond([Iac, Dont, option]);
                i += 3;
            }
            else
            {
                // Other 2-byte commands (GA, NOP, ...) carry no payload.
                i += 2;
            }
        }
        return [.. output];
    }
}
