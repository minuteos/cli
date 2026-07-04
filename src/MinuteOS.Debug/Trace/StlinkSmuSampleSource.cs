using Microsoft.Extensions.Logging;
using MinuteOS.Build;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Streams STLINK-V3PWR current measurements as <see cref="SmuSample"/>s in
/// ascii_dec mode (one <c>mantissa×10^±exp</c> line per sample). It drives the
/// SessionSmu's existing control connection - switching it into streaming and
/// draining lines on an async completion-driven loop (no owned thread) - so
/// power control and measurement share the one VCP.
///
/// EXPERIMENTAL / hardware-unverified: the command sequence and ascii line
/// format are the reverse-engineered protocol (LPM01A / PowerShield lineage
/// that the V3PWR reuses); only <see cref="StlinkSmu.TryParseAmps"/> is unit
/// tested. Binary (bin_hexa) mode is a future second decoder.
/// </summary>
public sealed class StlinkSmuSampleSource : ISmuSampleSource
{
    private const int CurrentChannel = 0;

    private readonly StlinkSmu _smu;
    private readonly string _output;
    private readonly int _frequencyHz;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;
    private long _samples;

    public StlinkSmuSampleSource(StlinkSmu smu, string output, int frequencyHz, ILogger logger)
    {
        _smu = smu;
        _output = output;
        _frequencyHz = frequencyHz;
        _logger = logger;
    }

    // raw = nanoamperes, so a long is lossless across the 100 nA .. ~5 A range.
    public IReadOnlyList<SmuChannel> Channels =>
        [new SmuChannel(CurrentChannel, ChannelKind.Current, 1e-9, _output, "A")];

    public event Action<SmuSample>? Sample;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _smu.PowerAsync(_output, true, cancellationToken); // current only flows to a powered output
        await _smu.ConfigureStreamAsync(_frequencyHz, cancellationToken);
        await _smu.StartStreamAsync(cancellationToken);

        // The loop is I/O-completion-driven: it awaits the next line and does its
        // tiny parse/dispatch on the continuation, so no thread is parked on the
        // stream. Not awaited - it runs until cancellation.
        _reader = ReadLoopAsync();
        _ = WarnIfSilentAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync();
        // Halt the byte flow first so a read blocked on the next sample completes,
        // then drain the loop.
        try
        {
            await _smu.StopStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Stopping the SMU stream failed: {Message}", ex.Message);
        }
        if (_reader != null)
        {
            try { await _reader; } catch { /* best effort */ }
        }
    }

    private async Task ReadLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _smu.ReadLineAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SMU stream ended: {Message}", ex.Message);
                break;
            }

            if (line == null)
            {
                _logger.LogWarning("SMU stream closed");
                break;
            }

            if (_cts.IsCancellationRequested || !StlinkSmu.TryParseAmps(line, out var amps))
                continue;

            Interlocked.Increment(ref _samples);
            try
            {
                // A faulting consumer must not kill the reader.
                Sample?.Invoke(new SmuSample(CurrentChannel, (long)Math.Round(amps * 1e9)));
            }
            catch (Exception ex)
            {
                _logger.LogDebug("SMU sample handler threw: {Message}", ex.Message);
            }
        }
    }

    private async Task WarnIfSilentAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (Interlocked.Read(ref _samples) == 0)
            _logger.LogWarning("SMU: no current samples after 3s - is the output powered and streaming?");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
