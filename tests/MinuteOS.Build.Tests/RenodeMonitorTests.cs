using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Debug.Servers;

namespace MinuteOS.Build.Tests;

public class TelnetFilterTests
{
    [Fact]
    public void PlainText_PassesThrough()
    {
        var filter = new TelnetFilter(_ => { });
        Assert.Equal("hello"u8.ToArray(), filter.Process("hello"u8));
    }

    [Fact]
    public void IacCommands_AreStripped_AndNegotiationRefused()
    {
        var replies = new List<byte[]>();
        var filter = new TelnetFilter(replies.Add);
        // IAC WILL 1, IAC DO 3, IAC GA interleaved with text
        var clean = filter.Process([(byte)'a', 255, 251, 1, (byte)'b', 255, 253, 3, 255, 249, (byte)'c']);
        Assert.Equal("abc"u8.ToArray(), clean);
        Assert.Equal(2, replies.Count);
        Assert.Equal([255, 254, 1], replies[0]); // DONT 1 (refuse WILL)
        Assert.Equal([255, 252, 3], replies[1]); // WONT 3 (refuse DO)
    }

    [Fact]
    public void EscapedIac_Unescapes()
    {
        var filter = new TelnetFilter(_ => { });
        Assert.Equal([0xFF], filter.Process([255, 255]));
    }

    [Fact]
    public void PartialSequences_CarryOverChunks()
    {
        var filter = new TelnetFilter(_ => { });
        Assert.Equal("a"u8.ToArray(), filter.Process([(byte)'a', 255]));       // dangling IAC
        Assert.Equal("b"u8.ToArray(), filter.Process([253, 5, (byte)'b']));    // completes DO 5
    }

    [Fact]
    public void Subnegotiation_IsSkipped()
    {
        var filter = new TelnetFilter(_ => { });
        // IAC SB 31 ... IAC SE around text
        var clean = filter.Process([(byte)'x', 255, 250, 31, 1, 2, 3, 255, 240, (byte)'y']);
        Assert.Equal("xy"u8.ToArray(), clean);
    }
}

/// <summary>Drives RenodeMonitor against an in-process fake telnet server.</summary>
public class RenodeMonitorTests
{
    private sealed class FakeRenode : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Context = "monitor";

        public FakeRenode()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                // Telnet negotiation + banner + first prompt
                await stream.WriteAsync(new byte[] { 255, 251, 1 });
                await Write(stream, $"Renode, version 0.0\n({Context}) ");
                // detectEncodingFromByteOrderMarks: false - the client's telnet
                // negotiation replies (IAC DONT x = FF FE 01) would otherwise be
                // mistaken for a UTF-16 BOM. Real Renode has a telnet layer; this
                // fake just strips non-printable bytes off the line instead.
                var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                while (await reader.ReadLineAsync() is { } line)
                {
                    line = new string(line.Where(c => c >= ' ' && c < 127).ToArray());
                    if (line == "quit")
                        break;
                    if (line == "mach set \"test\"")
                        Context = "test";
                    var reply = line switch
                    {
                        "version" => "Renode v0.0.0\n",
                        "bad" => "There was an error executing command 'bad'\n",
                        _ => "",
                    };
                    // echo + reply + prompt (as renode does)
                    await Write(stream, $"{line}\n{reply}({Context}) ");
                }
                client.Close();
            });
        }

        private static Task Write(Stream stream, string text)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Connect_Execute_StripsEchoAndPrompt()
    {
        using var fake = new FakeRenode();
        await using var monitor = new RenodeMonitor("127.0.0.1", fake.Port, NullLogger.Instance);
        await monitor.ConnectAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("monitor", monitor.CurrentContext);

        var version = await monitor.ExecuteAsync("version");
        Assert.Equal("Renode v0.0.0", version);
    }

    [Fact]
    public async Task ContextTracks_MachineSelection()
    {
        using var fake = new FakeRenode();
        await using var monitor = new RenodeMonitor("127.0.0.1", fake.Port, NullLogger.Instance);
        await monitor.ConnectAsync(TimeSpan.FromSeconds(10));

        await monitor.ExecuteAsync("mach set \"test\"");
        Assert.Equal("test", monitor.CurrentContext);
    }

    [Fact]
    public async Task ErrorOutput_IsReturnedVerbatim()
    {
        using var fake = new FakeRenode();
        await using var monitor = new RenodeMonitor("127.0.0.1", fake.Port, NullLogger.Instance);
        await monitor.ConnectAsync(TimeSpan.FromSeconds(10));

        var result = await monitor.ExecuteAsync("bad");
        Assert.StartsWith("There was an error", result);
    }
}
