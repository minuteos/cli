using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Dap;

/// <summary>
/// A Debug Adapter Protocol wire connection: Content-Length framed JSON
/// messages over a stream pair (typically stdin/stdout). This is the piece
/// that crosses the in-process boundary of the VS Code extension - the same
/// protocol, but over a pipe.
/// </summary>
public sealed class DapConnection(Stream input, Stream output) : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextSeq = 1;

    /// <summary>Reads one message; null on end of stream.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken);
            if (line == null)
                return null;
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(colon + 1)..].Trim());
        }
        if (contentLength < 0)
            throw new InvalidDataException("DAP message without Content-Length");

        var buffer = new byte[contentLength];
        await input.ReadExactlyAsync(buffer, cancellationToken);
        return JsonNode.Parse(buffer) as JsonObject;
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await input.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (n == 0)
                return sb.Length > 0 ? sb.ToString() : null;
            var c = (char)one[0];
            if (c == '\n')
                return sb.ToString().TrimEnd('\r');
            sb.Append(c);
        }
    }

    /// <summary>Sends a message, assigning its seq. Thread-safe.</summary>
    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            message["seq"] = _nextSeq++;
            var body = Encoding.UTF8.GetBytes(message.ToJsonString(JsonSerializerOptions.Default));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(body, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendEventAsync(string name, JsonObject? body = null, CancellationToken cancellationToken = default)
    {
        var evt = new JsonObject { ["type"] = "event", ["event"] = name };
        if (body != null)
            evt["body"] = body;
        return SendAsync(evt, cancellationToken);
    }

    public Task SendResponseAsync(JsonObject request, bool success, JsonObject? body = null, string? message = null,
        CancellationToken cancellationToken = default)
    {
        var response = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.DeepClone() ?? 0,
            ["command"] = request["command"]?.DeepClone() ?? "",
            ["success"] = success,
        };
        if (message != null)
            response["message"] = message;
        if (body != null)
            response["body"] = body;
        return SendAsync(response, cancellationToken);
    }

    public void Dispose() => _writeLock.Dispose();
}
