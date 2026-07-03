using System.Buffers.Binary;
using System.Text;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Writes the <see cref="TraceFormat"/> stream. Stateful and thread-safe: every
/// record is time-delta-coded against the previous one, PC samples against the
/// previous PC, and measurements against the channel's previous value, so
/// concurrent producers must serialize through the lock here (they do - the
/// critical section is a few varints). Does not own the underlying stream.
/// </summary>
public sealed class TraceWriter
{
    private readonly Stream _stream;
    private readonly object _sync = new();
    private readonly Dictionary<int, long> _lastValue = [];
    private long _lastNs;
    private uint _lastPc;

    public TraceWriter(Stream stream, long startUnixNanos)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[TraceFormat.HeaderSize];
        TraceFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], TraceFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], startUnixNanos);
        lock (_sync)
            _stream.Write(header);
    }

    public void WritePc(long timeNs, uint pc)
    {
        lock (_sync)
        {
            Prologue(timeNs, TraceTag.PcSample);
            Varint.WriteSigned(_stream, pc - (long)_lastPc);
            _lastPc = pc;
        }
    }

    public void WritePcSleep(long timeNs)
    {
        lock (_sync)
            Prologue(timeNs, TraceTag.PcSleep);
    }

    public void WriteLog(long timeNs, int port, ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            Prologue(timeNs, TraceTag.Log);
            Varint.WriteUnsigned(_stream, (uint)port);
            Varint.WriteUnsigned(_stream, (ulong)data.Length);
            _stream.Write(data);
        }
    }

    public void DefineChannel(long timeNs, int channel, ChannelKind kind, double scale, string name, string unit)
    {
        lock (_sync)
        {
            Prologue(timeNs, TraceTag.ChannelDef);
            Varint.WriteUnsigned(_stream, (uint)channel);
            _stream.WriteByte((byte)kind);
            Span<byte> scaleBytes = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(scaleBytes, scale);
            _stream.Write(scaleBytes);
            WriteString(name);
            WriteString(unit);
            _lastValue[channel] = 0;
        }
    }

    public void WriteMeasurement(long timeNs, int channel, long raw)
    {
        lock (_sync)
        {
            Prologue(timeNs, TraceTag.Measurement);
            Varint.WriteUnsigned(_stream, (uint)channel);
            _lastValue.TryGetValue(channel, out var last);
            Varint.WriteSigned(_stream, raw - last);
            _lastValue[channel] = raw;
        }
    }

    public void WriteMark(long timeNs, MarkKind kind, string text = "")
    {
        lock (_sync)
        {
            Prologue(timeNs, TraceTag.Mark);
            _stream.WriteByte((byte)kind);
            WriteString(text);
        }
    }

    public void Flush()
    {
        lock (_sync)
            _stream.Flush();
    }

    private void Prologue(long timeNs, TraceTag tag)
    {
        // Monotonic host clock; clamp defensively so a rare backward blip can
        // never produce a negative (unrepresentable) delta.
        var delta = timeNs - _lastNs;
        Varint.WriteUnsigned(_stream, (ulong)Math.Max(0, delta));
        _lastNs = Math.Max(_lastNs, timeNs);
        _stream.WriteByte((byte)tag);
    }

    private void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Varint.WriteUnsigned(_stream, (ulong)bytes.Length);
        _stream.Write(bytes);
    }
}
