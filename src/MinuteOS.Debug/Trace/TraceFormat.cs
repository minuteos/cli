namespace MinuteOS.Debug.Trace;

/// <summary>
/// The `mtrace` timeline format: a compact, lossless, streaming binary log that
/// unifies heterogeneous profiling sources - DWT PC samples, SMU power/current
/// measurements, and ITM log output - on one host clock.
///
/// Layout: a fixed <see cref="HeaderSize"/> header (magic, version,
/// <c>startUnixNanos</c>), then a stream of records. Each record is
/// <c>[deltaNs uvarint][tag u8][payload]</c>, where <c>deltaNs</c> is the
/// nanoseconds since the previous record (the host clock is monotonic, so this
/// is always &gt;= 0 and delta-coding keeps it tiny). Timestamps, PC deltas and
/// signed values are LEB128 varints; PC and measurement values are delta-coded
/// against the previous same-kind value. Tags &lt; 0x80 are core records a
/// reader of the same major version must understand; tags &gt;= 0x80 are
/// length-prefixed extension records an older reader can skip.
/// </summary>
public static class TraceFormat
{
    /// <summary>File magic: "mtrc".</summary>
    public static readonly byte[] Magic = "mtrc"u8.ToArray();

    public const ushort Version = 1;

    /// <summary>magic(4) + version(2) + flags(2) + startUnixNanos(8).</summary>
    public const int HeaderSize = 16;

    /// <summary>Extension records (tag &gt;= this) carry a uvarint length so old readers can skip them.</summary>
    public const byte ExtensionTag = 0x80;
}

/// <summary>Record type tags (see <see cref="TraceFormat"/>).</summary>
public enum TraceTag : byte
{
    /// <summary>DWT PC sample: payload is the PC delta (zigzag varint) from the previous PC.</summary>
    PcSample = 0x01,

    /// <summary>DWT PC sample taken while the core was sleeping: no payload.</summary>
    PcSleep = 0x02,

    /// <summary>ITM log output: <c>port uvarint, length uvarint, bytes</c>.</summary>
    Log = 0x03,

    /// <summary>A channel measurement: <c>channel uvarint, value</c> (zigzag-varint delta from the channel's previous value).</summary>
    Measurement = 0x04,

    /// <summary>Channel definition: <c>channel uvarint, kind u8, scale f64, name (uvarint len + bytes), unit (uvarint len + bytes)</c>.</summary>
    ChannelDef = 0x05,

    /// <summary>Timeline marker: <c>kind u8, text (uvarint len + bytes)</c>.</summary>
    Mark = 0x06,
}

/// <summary>What a measurement channel carries.</summary>
public enum ChannelKind : byte
{
    Current = 0,
    Voltage = 1,
    Other = 2,
}

/// <summary>A debug-session event placed on the timeline for context.</summary>
public enum MarkKind : byte
{
    SessionStart = 0,
    Stopped = 1,
    Continued = 2,
    Breakpoint = 3,
    Reset = 4,
    Note = 5,
}

/// <summary>LEB128 varint helpers shared by the writer and reader.</summary>
public static class Varint
{
    public static void WriteUnsigned(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    /// <summary>Zigzag-encodes a signed value so small magnitudes (of either sign) stay short.</summary>
    public static void WriteSigned(Stream stream, long value)
        => WriteUnsigned(stream, (ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Reads a varint; returns false at a clean end of stream (no bytes left).</summary>
    public static bool TryReadUnsigned(Stream stream, out ulong value)
    {
        value = 0;
        var shift = 0;
        var first = stream.ReadByte();
        if (first < 0)
            return false;

        var b = first;
        while (true)
        {
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return true;
            shift += 7;
            b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Truncated varint in trace stream");
        }
    }

    public static ulong ReadUnsigned(Stream stream)
        => TryReadUnsigned(stream, out var value) ? value : throw new EndOfStreamException("Expected a varint");

    public static long ReadSigned(Stream stream)
    {
        var raw = ReadUnsigned(stream);
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }
}
