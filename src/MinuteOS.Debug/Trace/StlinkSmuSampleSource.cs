using Microsoft.Extensions.Logging;
using MinuteOS.Build;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Streams STLINK-V3PWR current measurements as <see cref="SmuSample"/>s in
/// ascii_dec mode (one <c>mantissa×10^±exp</c> line per sample). It drives the
/// SessionSmu's existing control connection - switching it into streaming and
/// draining lines on a background reader - so power control and measurement
/// share the one VCP.
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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _smu.Power(_output, true); // current only flows to a powered output
        _smu.ConfigureStream(_frequencyHz);
        _smu.StartStream();
        _reader = Task.Run(ReadLoop, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync();
        if (_reader != null)
        {
            try { await _reader; } catch { /* best effort */ }
        }
        try
        {
            _smu.StopStream();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Stopping the SMU stream failed: {Message}", ex.Message);
        }
    }

    private void ReadLoop()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var samples = 0L;
        var warned = false;

        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = _smu.ReadLine(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SMU stream ended: {Message}", ex.Message);
                break;
            }

            if (line != null && StlinkSmu.TryParseAmps(line, out var amps))
            {
                samples++;
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
            else if (!warned && samples == 0
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3))
            {
                warned = true;
                _logger.LogWarning("SMU: no current samples after 3s - is the output powered and streaming?");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
