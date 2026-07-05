using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// An in-process GDB Remote Serial Protocol server: gdb connects to a TCP port
/// and its packets are translated into <see cref="IDebugTarget"/> operations.
/// The port of the extension's <c>gdb-server/internal.ts</c> - the same idea
/// that lets a probe act as a GDB server without an external process.
/// Little-endian, ASCII/binary payloads handled as Latin1 (one char = one byte).
/// </summary>
public abstract class InternalGdbServer
{
    private const byte Ack = (byte)'+';
    private const byte Nak = (byte)'-';
    private const byte Stx = (byte)'$';
    private const byte Etx = (byte)'#';
    private const byte Interrupt = 3;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();

    protected ILogger Logger { get; }

    public string Address { get; private set; } = "";

    protected InternalGdbServer(ILogger logger) => Logger = logger;

    /// <summary>Attaches to a target (called for <c>vAttach</c>).</summary>
    protected abstract Task<IDebugTarget> GetTargetAsync(int pid, CancellationToken cancellationToken);

    protected Task StartListenerAsync()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Address = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        Logger.LogInformation("Internal GDB server listening on {Address}", Address);

        _ = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts!.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var socket = await _listener!.AcceptTcpClientAsync(token);
                var connection = new Connection(this, socket, Logger).RunAsync();
                lock (_connectionsLock)
                    _connections.Add(connection);
                // Drop finished connections so a long-lived server doesn't leak
                // completed tasks across reconnects.
                _ = connection.ContinueWith(t =>
                {
                    lock (_connectionsLock)
                        _connections.Remove(t);
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) { Logger.LogDebug(ex, "GDB accept loop ended"); }
    }

    protected async Task StopListenerAsync()
    {
        if (_cts != null)
            await _cts.CancelAsync();
        _listener?.Stop();
        Task[] pending;
        lock (_connectionsLock)
            pending = [.. _connections];
        try { await Task.WhenAll(pending); } catch { /* best effort */ }
        _cts?.Dispose();
    }

    private sealed class Connection(InternalGdbServer owner, TcpClient client, ILogger logger)
    {
        private readonly NetworkStream _stream = client.GetStream();
        private bool _noAck;
        private IDebugTarget? _target;

        public async Task RunAsync()
        {
            var address = client.Client.RemoteEndPoint?.ToString() ?? "?";
            logger.LogInformation("GDB connection from {Address}", address);
            try
            {
                await ReceiveLoopAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GDB connection {Address} failed", address);
            }
            finally
            {
                client.Dispose();
                logger.LogInformation("GDB connection from {Address} closed", address);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];
            var packet = new List<byte>();
            var inPacket = false;
            var trailer = 0; // remaining checksum bytes after '#'

            while (true)
            {
                var n = await _stream.ReadAsync(buffer);
                if (n == 0)
                    return;

                for (var i = 0; i < n; i++)
                {
                    var b = buffer[i];
                    if (!inPacket)
                    {
                        if (b == Stx)
                        {
                            inPacket = true;
                            packet.Clear();
                        }
                        else if (b == Interrupt)
                        {
                            await SendPacketAsync(await HandleInterruptAsync());
                        }
                        // '+'/'-' acks and stray bytes are ignored.
                        continue;
                    }

                    if (trailer > 0)
                    {
                        packet.Add(b);
                        if (--trailer == 0)
                        {
                            inPacket = false;
                            await HandlePacketAsync(packet);
                        }
                        continue;
                    }

                    if (b == Etx)
                        trailer = 2; // two checksum hex digits follow
                    packet.Add(b);
                }
            }
        }

        private async Task HandlePacketAsync(List<byte> raw)
        {
            // raw = payload... '#' cc
            var hashIndex = raw.Count - 3;
            var payloadBytes = raw.GetRange(0, hashIndex).ToArray();
            var expected = Convert.ToInt32(Encoding.ASCII.GetString(raw.ToArray(), hashIndex + 1, 2), 16);
            if (Checksum(payloadBytes) != expected)
            {
                logger.LogWarning("GDB checksum mismatch");
                await WriteAsync([Nak]);
                return;
            }

            if (!_noAck)
                await WriteAsync([Ack]);

            var payload = Encoding.Latin1.GetString(Unescape(payloadBytes));
            string? reply;
            try
            {
                reply = await HandleAsync(payload);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GDB command '{Command}' failed", payload);
                reply = "E." + ex.Message;
            }
            if (reply != null)
                await SendPacketAsync(reply);
        }

        // #region command dispatch

        private async Task<string?> HandleAsync(string packet)
        {
            if (packet.Length == 0)
                return "";
            return packet[0] switch
            {
                'q' => await HandleQueryAsync(packet),
                'Q' => HandleSet(packet),
                'v' => await HandleControlAsync(packet),
                '!' => "OK",
                '?' => StopReason(),
                'D' => await HandleDetachAsync(),
                'g' => await ReadRegistersAsync(),
                'H' => "OK",
                'm' => await ReadMemoryAsync(packet),
                'M' => await WriteMemoryAsync(packet),
                'p' => await ReadRegisterAsync(packet),
                'P' => await WriteRegisterAsync(packet),
                'R' => await ResetAsync(),
                'z' or 'Z' => await BreakpointAsync(packet),
                _ => "",
            };
        }

        private Task<string> HandleQueryAsync(string packet)
        {
            var (query, args) = SplitFirst(packet, ':', ',');
            return query switch
            {
                "qSupported" => Task.FromResult("PacketSize=10000;vContSupported+;QStartNoAckMode+;qXfer:features:read+"),
                "qfThreadInfo" => Task.FromResult(_target?.Threads.Count > 0
                    ? "m" + string.Join(',', _target.Threads.Select(t => t.Id.ToString("x"))) : "l"),
                "qsThreadInfo" => Task.FromResult("l"),
                "qAttached" => Task.FromResult("1"),
                "qC" => Task.FromResult("QC1"),
                "qSymbol" => Task.FromResult("OK"),
                "qXfer" => Task.FromResult(HandleXfer(args)),
                _ => Task.FromResult(""),
            };
        }

        private string HandleSet(string packet)
        {
            if (SplitFirst(packet, ':').Head == "QStartNoAckMode")
            {
                _noAck = true;
                return "OK";
            }
            return "E.unknown command";
        }

        private string HandleXfer(string args)
        {
            var parts = args.Split(':');
            if (parts is ["features", "read", "target.xml", var range])
            {
                if (_target == null)
                    return "E99";
                var data = FeaturesXml.Generate(_target);
                var span = range.Split(',');
                var offset = Convert.ToInt32(span[0], 16);
                var length = Convert.ToInt32(span[1], 16);
                var end = Math.Min(offset + length, data.Length);
                var slice = Encoding.Latin1.GetString(data, offset, Math.Max(0, end - offset));
                return (end >= data.Length ? "l" : "m") + slice;
            }
            return "E00";
        }

        private async Task<string?> HandleControlAsync(string packet)
        {
            var (cmd, arg) = SplitFirst(packet, ':', ';');
            return cmd switch
            {
                "vMustReplyEmpty" => "",
                "vAttach" => await AttachAsync(arg),
                "vCont" => await ContinueAsync(arg),
                "vCont?" => "vCont;c;C;s;S",
                _ => "",
            };
        }

        private async Task<string> AttachAsync(string arg)
        {
            _target = await owner.GetTargetAsync(ParseInt(arg), CancellationToken.None);
            await _target.StopAsync();
            return StopReason();
        }

        private async Task<string?> ContinueAsync(string arg)
        {
            if (_target == null)
                return "E99";
            switch (arg.Length > 0 ? arg[0] : ' ')
            {
                case 'c' or 'C':
                    await _target.ContinueAsync();
                    return null;
                case 's' or 'S':
                    await _target.StepAsync();
                    return StopReason();
                default:
                    return "E.unknown vCont";
            }
        }

        private async Task<string> HandleDetachAsync()
        {
            if (_target == null)
                return "E99";
            await _target.ContinueAsync();
            _target = null;
            return "OK";
        }

        private async Task<string> ReadRegistersAsync()
        {
            if (_target == null)
                return "E99";
            var values = await _target.ReadAllRegistersAsync();
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
                sb.Append(values[i] is { } v ? Convert.ToHexString(v).ToLowerInvariant() : Placeholder(i));
            return sb.ToString();
        }

        private async Task<string> ReadMemoryAsync(string packet)
        {
            if (_target == null)
                return "E99";
            var parts = packet[1..].Split(',');
            try
            {
                var data = await _target.ReadMemoryAsync(ParseHexU(parts[0]), Convert.ToInt32(parts[1], 16));
                return Convert.ToHexString(data).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GDB memory read failed");
                return "E01";
            }
        }

        private async Task<string> WriteMemoryAsync(string packet)
        {
            if (_target == null)
                return "E99";
            var (header, dataHex) = SplitFirst(packet[1..], ':');
            var parts = header.Split(',');
            var data = Convert.FromHexString(dataHex);
            await _target.WriteMemoryAsync(ParseHexU(parts[0]), data);
            return "OK";
        }

        private async Task<string> ReadRegisterAsync(string packet)
        {
            if (_target == null)
                return "E99";
            var index = Convert.ToInt32(packet[1..], 16);
            var value = await _target.ReadRegisterAsync(index);
            return value is { } v ? Convert.ToHexString(v).ToLowerInvariant() : Placeholder(index);
        }

        private async Task<string> WriteRegisterAsync(string packet)
        {
            if (_target == null)
                return "E99";
            var eq = packet.IndexOf('=');
            var index = Convert.ToInt32(packet[1..eq], 16);
            await _target.WriteRegisterAsync(index, Convert.FromHexString(packet[(eq + 1)..]));
            return "OK";
        }

        private async Task<string> ResetAsync()
        {
            if (_target == null)
                return "E99";
            await _target.ResetAsync();
            return "OK";
        }

        private async Task<string> BreakpointAsync(string packet)
        {
            if (_target == null)
                return "E99";
            var set = packet[0] == 'Z';
            var fields = packet[1..].Split(',', ';');
            if (fields[0] != "1")
                throw new NotSupportedException($"Unsupported breakpoint type {fields[0]}");
            await _target.BreakpointAsync(set, ParseHexU(fields[1]), Convert.ToInt32(fields[2], 16));
            return "OK";
        }

        private async Task<string?> HandleInterruptAsync()
        {
            if (_target == null)
                return "E99";
            await _target.StopAsync();
            return StopReason();
        }

        private string StopReason()
        {
            if (_target == null)
                return "W00";
            if (_target.Threads.Count == 0)
                return "N";
            foreach (var t in _target.Threads)
                if (t.StopReason is { } reason)
                    return "S" + ((byte)reason).ToString("x2");
            return "";
        }

        // GDB expects a value (or 'x' padding) for every register slot.
        private string Placeholder(int index)
        {
            var bits = _target?.RegisterInfo.ElementAtOrDefault(index)?.Bits ?? 0;
            return new string('x', ((bits + 7) / 8) * 2);
        }

        // #endregion

        private async Task SendPacketAsync(string? reply)
        {
            if (reply == null)
                return;
            await WriteAsync(EncodePacket(reply));
        }

        private async Task WriteAsync(byte[] data) => await _stream.WriteAsync(data);
    }

    // #region protocol helpers

    private static int Checksum(byte[] payload)
    {
        var sum = 0;
        foreach (var b in payload)
            sum = (sum + b) & 0xFF;
        return sum;
    }

    private static byte[] Unescape(byte[] payload)
    {
        if (Array.IndexOf(payload, (byte)'}') < 0)
            return payload;
        var result = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '}' && i + 1 < payload.Length)
                result.Add((byte)(payload[++i] ^ 0x20));
            else
                result.Add(payload[i]);
        }
        return result.ToArray();
    }

    private static byte[] EncodePacket(string reply)
    {
        var payload = Encoding.Latin1.GetBytes(reply);
        var body = new List<byte>(payload.Length + 4) { Stx };
        foreach (var b in payload)
        {
            if (b is (byte)'#' or (byte)'$' or (byte)'}' or (byte)'*')
            {
                body.Add((byte)'}');
                body.Add((byte)(b ^ 0x20));
            }
            else
            {
                body.Add(b);
            }
        }
        var checksum = Checksum(body.GetRange(1, body.Count - 1).ToArray());
        body.Add(Etx);
        body.AddRange(Encoding.ASCII.GetBytes(checksum.ToString("x2")));
        return body.ToArray();
    }

    private static (string Head, string Tail) SplitFirst(string s, params char[] seps)
    {
        var i = s.IndexOfAny(seps);
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : Convert.ToInt32(s, 16);
    private static uint ParseHexU(string s) => Convert.ToUInt32(s, 16);

    // #endregion
}
