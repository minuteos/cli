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
        long timeNs = 0;
        uint lastPc = 0;
        var lastValue = new Dictionary<int, long>();

        while (Varint.TryReadUnsigned(_stream, out var delta))
        {
            timeNs += (long)delta;
            var tagByte = _stream.ReadByte();
            if (tagByte < 0)
                throw new EndOfStreamException("Truncated record (missing tag)");

            if (tagByte >= TraceFormat.ExtensionTag)
            {
                Skip((long)Varint.ReadUnsigned(_stream));
                continue;
            }

            switch ((TraceTag)tagByte)
            {
                case TraceTag.PcSample:
                    lastPc = (uint)(lastPc + Varint.ReadSigned(_stream));
                    yield return new PcSampleEvent(timeNs, lastPc, Sleep: false);
                    break;

                case TraceTag.PcSleep:
                    yield return new PcSampleEvent(timeNs, 0, Sleep: true);
                    break;

                case TraceTag.Log:
                {
                    var port = (int)Varint.ReadUnsigned(_stream);
                    var data = ReadBytes((int)Varint.ReadUnsigned(_stream));
                    yield return new LogEvent(timeNs, port, data);
                    break;
                }

                case TraceTag.Measurement:
                {
                    var channel = (int)Varint.ReadUnsigned(_stream);
                    lastValue.TryGetValue(channel, out var last);
                    var value = last + Varint.ReadSigned(_stream);
                    lastValue[channel] = value;
                    yield return new MeasurementEvent(timeNs, channel, value);
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
                    lastValue[channel] = 0;
                    yield return new ChannelDefEvent(timeNs, channel, kind, scale, name, unit);
                    break;
                }

                case TraceTag.Mark:
                    yield return new MarkEvent(timeNs, (MarkKind)ReadByteChecked(), ReadString());
                    break;

                default:
                    throw new InvalidDataException($"Unknown core trace tag 0x{tagByte:x2}");
            }
        }
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
