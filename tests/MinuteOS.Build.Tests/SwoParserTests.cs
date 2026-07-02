using MinuteOS.Debug.Swo;

namespace MinuteOS.Build.Tests;

public class SwoParserTests
{
    private static List<SwoPacket> Parse(params byte[][] chunks)
    {
        var parser = new SwoParser();
        var packets = new List<SwoPacket>();
        parser.SourcePacket += packets.Add;
        foreach (var chunk in chunks)
            parser.Feed(chunk);
        return packets;
    }

    [Fact]
    public void ItmByteWrite_Channel0()
    {
        // header 0x01 = channel 0, 1-byte payload
        var packets = Parse([0x01, (byte)'A']);
        var packet = Assert.Single(packets);
        Assert.False(packet.Dwt);
        Assert.Equal(0, packet.Channel);
        Assert.Equal([(byte)'A'], packet.Data);
    }

    [Fact]
    public void ItmWordWrite_Channel1()
    {
        // header: ch 1 << 3 | size code 3 (= 4 bytes) = 0x0B
        var packets = Parse([0x0B, 0x78, 0x56, 0x34, 0x12]);
        var packet = Assert.Single(packets);
        Assert.Equal(1, packet.Channel);
        Assert.Equal([0x78, 0x56, 0x34, 0x12], packet.Data);
    }

    [Fact]
    public void DwtPacket_Flagged()
    {
        // header 0x0E = dwt bit (4) | ch 1, 2-byte payload
        var packets = Parse([0x0E, 0xAA, 0xBB]);
        var packet = Assert.Single(packets);
        Assert.True(packet.Dwt);
    }

    [Fact]
    public void SyncFrame_IsSkipped()
    {
        // five zero bytes + 0x80, then a source packet
        var packets = Parse([0, 0, 0, 0, 0, 0x80, 0x01, 0x42]);
        var packet = Assert.Single(packets);
        Assert.Equal([0x42], packet.Data);
    }

    [Fact]
    public void OverflowAndTimestamps_AreSkipped()
    {
        var packets = Parse([
            0x70,             // overflow
            0xC0, 0x81, 0x01, // local timestamp (continuation byte then terminator)
            0x01, 0x55,       // source packet
        ]);
        var packet = Assert.Single(packets);
        Assert.Equal([0x55], packet.Data);
    }

    [Fact]
    public void PacketsSplitAcrossChunks_Reassemble()
    {
        var packets = Parse(
            [0x0B, 0x78],        // word packet: header + 1 payload byte
            [0x56, 0x34],        // ...two more
            [0x12, 0x01],        // last byte + next packet's header
            [(byte)'x']);        // its payload
        Assert.Equal(2, packets.Count);
        Assert.Equal([0x78, 0x56, 0x34, 0x12], packets[0].Data);
        Assert.Equal([(byte)'x'], packets[1].Data);
    }

    [Fact]
    public void ConsoleText_ReassemblesFromStimulusPort0()
    {
        var text = "ok\n";
        var bytes = text.SelectMany(c => new byte[] { 0x01, (byte)c }).ToArray();
        var packets = Parse(bytes);
        Assert.Equal(text, string.Concat(packets.Select(p => (char)p.Data[0])));
    }
}
