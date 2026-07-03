namespace MinuteOS.Debug.Trace;

/// <summary>A measurement channel an <see cref="ISmuSampleSource"/> will emit.</summary>
public sealed record SmuChannel(int Id, ChannelKind Kind, double Scale, string Name, string Unit);

/// <summary>One raw measurement on a channel (SI value = <c>Raw * channel.Scale</c>).</summary>
public readonly record struct SmuSample(int Channel, long Raw);

/// <summary>
/// A streaming source of SMU measurements (current/voltage), fed into a
/// <see cref="TraceRecorder"/>. This is the seam for real V3PWR acquisition:
/// the STLINK-V3PWR's <c>power_monitor</c> streaming protocol plugs in here
/// once its binary frame format is nailed down; until then
/// <see cref="NullSmuSampleSource"/> stands in and no power track is recorded.
/// </summary>
public interface ISmuSampleSource : IAsyncDisposable
{
    /// <summary>Channels this source emits; defined in the recording before streaming starts.</summary>
    IReadOnlyList<SmuChannel> Channels { get; }

    /// <summary>Raised for each measurement, on the source's own thread.</summary>
    event Action<SmuSample>? Sample;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>The no-op SMU source used until V3PWR streaming acquisition is implemented.</summary>
public sealed class NullSmuSampleSource : ISmuSampleSource
{
    public IReadOnlyList<SmuChannel> Channels => [];

    public event Action<SmuSample>? Sample;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _ = Sample; // keep the compiler happy about the unused event on the stub
        return ValueTask.CompletedTask;
    }
}
