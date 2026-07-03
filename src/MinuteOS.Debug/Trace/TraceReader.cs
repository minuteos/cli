using System.Buffers.Binary;
using System.Text;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Decodes a <see cref="TraceFormat"/> stream into absolute-timestamped events,
/// undoing the time/PC/value delta-coding. Unknown extension records (tag &gt;=
/// <see cref="TraceFormat.ExtensionTag"/>) are skipped via their length prefix;
/// an unknown core tag is a hard error (version mismatch).
/// </summary>
public sealed class TraceReader
{
    private readonly Stream _stream;

    public TraceHeader Header { get; }

    /// <summary>
    /// True once <see cref="Events"/> has stopped on a partial trailing record -
    /// the hallmark of a recorder killed mid-write. Everything up to it is still
    /// yielded; consumers can surface this to explain a short trace.
    /// </summary>
    public bool Truncated { get; private set; }

    public TraceReader(Stream stream)
    {
        _stream = stream;

        Span<byte> header = stackalloc byte[TraceFormat.HeaderSize];
        _stream.ReadExactly(header);
        if (!header[..4].SequenceEqual(TraceFormat.Magic))
            throw new InvalidDataException("Not an mtrace stream (bad magic)");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != TraceFormat.Version)
            throw new InvalidDataException($"Unsupported mtrace version {version} (expected {TraceFormat.Version})");

        Header = new TraceHeader(version,
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[8..]));
    }

    public IEnumerable<TraceEvent> Events()
    {
        var state = new DecodeState();
        while (true)
        {
            TraceEvent? evt;
            try
            {
                if (!TryReadRecord(state, out evt))
                    yield break; // clean end of stream at a record boundary
            }
            catch (EndOfStreamException)
            {
                // A recorder killed mid-write leaves a partial trailing record.
                // Delta-coding makes anything past it undecodable anyway, so stop
                // cleanly and let the caller see what was recorded up to here.
                Truncated = true;
                yield break;
            }

            if (evt != null)
                yield return evt;
        }
    }

    private sealed class DecodeState
    {
        public long TimeNs;
        public uint LastPc;
        public readonly Dictionary<int, long> LastValue = [];
    }

    /// <summary>
    /// Reads one record, advancing <paramref name="state"/>. Returns false at a
    /// clean record boundary EOF; sets <paramref name="evt"/> to null for a
    /// skipped extension record. Throws <see cref="EndOfStreamException"/> on a
    /// truncated record and <see cref="InvalidDataException"/> on an unknown tag.
    /// </summary>
    private bool TryReadRecord(DecodeState state, out TraceEvent? evt)
    {
        evt = null;
        if (!Varint.TryReadUnsigned(_stream, out var delta))
            return false;

        state.TimeNs += (long)delta;
        var tagByte = _stream.ReadByte();
        if (tagByte < 0)
            throw new EndOfStreamException("Truncated record (missing tag)");

        if (tagByte >= TraceFormat.ExtensionTag)
        {
            Skip((long)Varint.ReadUnsigned(_stream));
            return true;
        }

        switch ((TraceTag)tagByte)
        {
            case TraceTag.PcSample:
                state.LastPc = (uint)(state.LastPc + Varint.ReadSigned(_stream));
                evt = new PcSampleEvent(state.TimeNs, state.LastPc, Sleep: false);
                break;

            case TraceTag.PcSleep:
                evt = new PcSampleEvent(state.TimeNs, 0, Sleep: true);
                break;

            case TraceTag.Log:
            {
                var port = (int)Varint.ReadUnsigned(_stream);
                var data = ReadBytes((int)Varint.ReadUnsigned(_stream));
                evt = new LogEvent(state.TimeNs, port, data);
                break;
            }

            case TraceTag.Measurement:
            {
                var channel = (int)Varint.ReadUnsigned(_stream);
                state.LastValue.TryGetValue(channel, out var last);
                var value = last + Varint.ReadSigned(_stream);
                state.LastValue[channel] = value;
                evt = new MeasurementEvent(state.TimeNs, channel, value);
                break;
            }

            case TraceTag.ChannelDef:
            {
                var channel = (int)Varint.ReadUnsigned(_stream);
                var kind = (ChannelKind)ReadByteChecked();
                Span<byte> scaleBytes = stackalloc byte[8];
                _stream.ReadExactly(scaleBytes);
                var scale = BinaryPrimitives.ReadDoubleLittleEndian(scaleBytes);
                var name = ReadString();
                var unit = ReadString();
                state.LastValue[channel] = 0;
                evt = new ChannelDefEvent(state.TimeNs, channel, kind, scale, name, unit);
                break;
            }

            case TraceTag.Mark:
                evt = new MarkEvent(state.TimeNs, (MarkKind)ReadByteChecked(), ReadString());
                break;

            default:
                throw new InvalidDataException($"Unknown core trace tag 0x{tagByte:x2}");
        }

        return true;
    }

    private byte ReadByteChecked()
    {
        var b = _stream.ReadByte();
        if (b < 0)
            throw new EndOfStreamException("Truncated record");
        return (byte)b;
    }

    private byte[] ReadBytes(int count)
    {
        var buffer = new byte[count];
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private string ReadString() => Encoding.UTF8.GetString(ReadBytes((int)Varint.ReadUnsigned(_stream)));

    private void Skip(long count)
    {
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            var chunk = (int)Math.Min(count, scratch.Length);
            _stream.ReadExactly(scratch[..chunk]);
            count -= chunk;
        }
    }
}
