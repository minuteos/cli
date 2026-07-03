namespace MinuteOS.Debug.Trace;

/// <summary>A decoded timeline event. <see cref="TimeNs"/> is absolute nanoseconds since the recording started.</summary>
public abstract record TraceEvent(long TimeNs);

/// <summary>A DWT PC sample. <see cref="Sleep"/> samples carry no address (the core was idle).</summary>
public sealed record PcSampleEvent(long TimeNs, uint Pc, bool Sleep) : TraceEvent(TimeNs);

/// <summary>ITM stimulus-port output (port 0 is the debug console).</summary>
public sealed record LogEvent(long TimeNs, int Port, byte[] Data) : TraceEvent(TimeNs);

/// <summary>
/// A measurement on a channel defined by an earlier <see cref="ChannelDefEvent"/>.
/// The SI value is <c>Raw * scale</c>; <see cref="Raw"/> is kept exactly.
/// </summary>
public sealed record MeasurementEvent(long TimeNs, int Channel, long Raw) : TraceEvent(TimeNs);

/// <summary>Declares a measurement channel before its first sample.</summary>
public sealed record ChannelDefEvent(long TimeNs, int Channel, ChannelKind Kind, double Scale, string Name, string Unit)
    : TraceEvent(TimeNs);

/// <summary>A debug-session event (stop/continue/breakpoint/reset/note) placed on the timeline.</summary>
public sealed record MarkEvent(long TimeNs, MarkKind Kind, string Text) : TraceEvent(TimeNs);

/// <summary>Metadata from a trace stream's header.</summary>
public sealed record TraceHeader(ushort Version, ushort Flags, long StartUnixNanos);
