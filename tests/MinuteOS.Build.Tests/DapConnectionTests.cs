using System.Text;
using System.Text.Json.Nodes;
using MinuteOS.Debug.Dap;

namespace MinuteOS.Build.Tests;

public class DapConnectionTests
{
    [Fact]
    public async Task Read_ParsesContentLengthFramedMessage()
    {
        var body = """{"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"minute-debug"}}""";
        var wire = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        using var connection = new DapConnection(new MemoryStream(wire), Stream.Null);

        var message = await connection.ReadAsync();
        Assert.NotNull(message);
        Assert.Equal("request", message["type"]!.GetValue<string>());
        Assert.Equal("initialize", message["command"]!.GetValue<string>());

        Assert.Null(await connection.ReadAsync()); // EOF
    }

    [Fact]
    public async Task Read_HandlesMultipleMessagesAndExtraHeaders()
    {
        var m1 = """{"seq":1,"type":"request","command":"a"}""";
        var m2 = """{"seq":2,"type":"request","command":"b"}""";
        var wire = Encoding.UTF8.GetBytes(
            $"Content-Length: {m1.Length}\r\nContent-Type: application/json\r\n\r\n{m1}" +
            $"Content-Length: {m2.Length}\r\n\r\n{m2}");
        using var connection = new DapConnection(new MemoryStream(wire), Stream.Null);

        Assert.Equal("a", (await connection.ReadAsync())!["command"]!.GetValue<string>());
        Assert.Equal("b", (await connection.ReadAsync())!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task Send_FramesAndAssignsSeq()
    {
        var output = new MemoryStream();
        using var connection = new DapConnection(Stream.Null, output);

        await connection.SendEventAsync("initialized");
        await connection.SendResponseAsync(
            new JsonObject { ["seq"] = 5, ["command"] = "launch" }, success: true);

        // Re-read what was written through another connection.
        output.Position = 0;
        using var reader = new DapConnection(output, Stream.Null);

        var evt = await reader.ReadAsync();
        Assert.Equal(1, evt!["seq"]!.GetValue<int>());
        Assert.Equal("event", evt["type"]!.GetValue<string>());
        Assert.Equal("initialized", evt["event"]!.GetValue<string>());

        var response = await reader.ReadAsync();
        Assert.Equal(2, response!["seq"]!.GetValue<int>());
        Assert.Equal("response", response["type"]!.GetValue<string>());
        Assert.Equal(5, response["request_seq"]!.GetValue<int>());
        Assert.Equal("launch", response["command"]!.GetValue<string>());
        Assert.True(response["success"]!.GetValue<bool>());
    }
}
