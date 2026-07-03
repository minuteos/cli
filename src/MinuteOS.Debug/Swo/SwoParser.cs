namespace MinuteOS.Debug.Swo;

/// <summary>An ITM/DWT source packet decoded from the SWO stream.</summary>
public sealed record SwoPacket(bool Dwt, int Channel, byte[] Data);

/// <summary>What a decoded SWO packet means to the profiler/recorder/timeline.</summary>
public enum SwoSampleKind
{
    /// <summary>Not a PC sample or log (e.g. other DWT source) - ignore.</summary>
    Ignore,
    PcSample,
    PcSleep,
    Log,
}

/// <summary>A classified SWO packet: the shared interpretation of PC samples and ITM logs.</summary>
public readonly record struct SwoSample(SwoSampleKind Kind, uint Pc, int Port, byte[] Data)
{
    /// <summary>
    /// Classifies a packet the way every consumer needs: DWT PC-sample source
    /// packets become PC samples (or the sleep form), ITM stimulus-port writes
    /// become logs, anything else is ignored. Keeps the DWT discriminator and
    /// Thumb-bit handling in one place.
    /// </summary>
    public static SwoSample Classify(SwoPacket packet)
    {
        if (!packet.Dwt)
            return new SwoSample(SwoSampleKind.Log, 0, packet.Channel, packet.Data);
        if (packet.Channel != SwoProfiler.PcSampleDiscriminator)
            return new SwoSample(SwoSampleKind.Ignore, 0, 0, []);
        if (packet.Data.Length == 1 && packet.Data[0] == 0)
            return new SwoSample(SwoSampleKind.PcSleep, 0, 0, []);
        if (packet.Data.Length == 4)
            return new SwoSample(SwoSampleKind.PcSample, BitConverter.ToUInt32(packet.Data) & ~1u, 0, []);
        return new SwoSample(SwoSampleKind.Ignore, 0, 0, []);
    }
}

/// <summary>
/// Incremental SWO/SWV (ITM trace) stream decoder - the port of the
/// extension's reader loop in <c>gdb/swo.ts</c>, restructured as a push-based
/// parser: feed it chunks as they arrive; it emits source packets and skips
/// sync/overflow/protocol (timestamp) frames, carrying partial frames across
/// chunk boundaries.
/// </summary>
public sealed class SwoParser
{
    private byte[] _buffer = [];

    /// <summary>Called for every ITM (stimulus port) / DWT source packet.</summary>
    public event Action<SwoPacket>? SourcePacket;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        _buffer = _buffer.Length == 0 ? chunk.ToArray() : [.. _buffer, .. chunk];

        var pos = 0;
        while (TryParseFrame(ref pos))
        {
        }
        _buffer = pos == 0 ? _buffer : _buffer[pos..];
    }

    /// <summary>Parses one frame at <paramref name="pos"/>; false when more data is needed.</summary>
    private bool TryParseFrame(ref int pos)
    {
        var available = _buffer.Length - pos;
        if (available <= 0)
            return false;

        var t = _buffer[pos];
        var len = t & 3;

        if (len != 0)
        {
            // Source frame (ITM or DWT): 1/2/4-byte payload after the header.
            if (len == 3)
                len = 4;
            if (available < len + 1)
                return false;
            SourcePacket?.Invoke(new SwoPacket(
                Dwt: (t & 4) != 0,
                Channel: t >> 3,
                Data: _buffer[(pos + 1)..(pos + 1 + len)]));
            pos += len + 1;
            return true;
        }

        if (t == 0)
        {
            // Sync frame: at least five 0x00 bytes followed by 0x80.
            var count = 1;
            while (true)
            {
                if (available <= count)
                    return false; // need more
                var b = _buffer[pos + count];
                if (b == 0)
                {
                    count++;
                    continue;
                }
                // 0x80 terminates a valid sync; anything else is a framing error -
                // skip the zeros and resynchronize at the offending byte.
                pos += b == 0x80 && count >= 5 ? count + 1 : count;
                return true;
            }
        }

        if (t == 0x70)
        {
            // Overflow.
            pos++;
            return true;
        }

        // Protocol frame (timestamps etc.): the top bit is the continuation
        // bit - the frame ends at the first byte with bit 7 clear.
        var length = 1;
        while ((_buffer[pos + length - 1] & 0x80) != 0)
        {
            if (available <= length)
                return false; // need more
            length++;
        }
        pos += length;
        return true;
    }
}
