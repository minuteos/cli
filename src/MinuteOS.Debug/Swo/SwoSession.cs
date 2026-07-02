using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Swo;

public sealed record SwoConfig
{
    /// <summary>Target CPU frequency in Hz. 0 keeps the probe default.</summary>
    public int CpuFrequency { get; init; }
    /// <summary>SWO/SWV bitrate in Hz.</summary>
    public int SwvFrequency { get; init; }
    /// <summary>SWV encoding (1 = Manchester, 2 = UART).</summary>
    public SwvFormat Format { get; init; } = SwvFormat.Uart;
}

/// <summary>
/// A running SWO capture: configures the target's trace bits (DWT/ITM/TPIU via
/// <see cref="Cortex"/>) and decodes the byte stream into source packets - the
/// port of the extension's <c>SwoSession</c>.
/// </summary>
public sealed class SwoSession(SwoConfig config, Cortex cortex, Stream stream, Action<SwoPacket> sourcePacket,
    ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Configure trace bits on the target, then start draining the stream.
        await cortex.SetupTraceAsync(new CortexTraceOptions
        {
            CpuFrequency = config.CpuFrequency,
            SwvFrequency = config.SwvFrequency,
            Format = config.Format,
        }, cancellationToken);

        _reader = Task.Run(ReaderAsync, CancellationToken.None);
    }

    private async Task ReaderAsync()
    {
        var parser = new SwoParser();
        parser.SourcePacket += sourcePacket;
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, _cts.Token);
                if (n == 0)
                    break;
                parser.Feed(buffer.AsSpan(0, n));
            }
            logger.LogDebug("SWO reader complete");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SWO reader failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_reader != null)
            await _reader;
        _cts.Dispose();
    }
}
