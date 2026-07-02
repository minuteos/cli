using System.Text.Json.Nodes;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Build.Tests;

public class MiParserTests
{
    [Theory]
    [InlineData("(gdb) ")]
    [InlineData("(gdb)")]
    public void Prompt_IsRecognized(string line)
    {
        var record = MiParser.Parse(line);
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.Prompt, record.Type);
    }

    [Fact]
    public void ConsoleStream_UnescapesText()
    {
        var record = MiParser.Parse("~\"GNU gdb (GDB) 13.2\\n\"");
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.ConsoleStream, record.Type);
        Assert.Equal("GNU gdb (GDB) 13.2\n", record.Text);
    }

    [Fact]
    public void TargetStream_OctalEscapes_Decode()
    {
        // BMP monitor replies arrive as @target output with octal escapes.
        var record = MiParser.Parse("@\"Target voltage: 3.3V\\012\"");
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.TargetStream, record.Type);
        Assert.Equal("Target voltage: 3.3V\n", record.Text);
    }

    [Fact]
    public void Result_Done_WithToken()
    {
        var record = MiParser.Parse("42^done");
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.Result, record.Type);
        Assert.Equal(42, record.Token);
        Assert.Equal("done", record.Class);
        Assert.Empty(record.Results!);
    }

    [Fact]
    public void Result_Error_HasMessage()
    {
        var record = MiParser.Parse("7^error,msg=\"Undefined command.\"");
        Assert.NotNull(record);
        Assert.Equal("error", record.Class);
        Assert.Equal("Undefined command.", record.Results!["msg"]!.GetValue<string>());
    }

    [Fact]
    public void BreakInsert_Result_ParsesTuple()
    {
        var record = MiParser.Parse(
            "3^done,bkpt={number=\"1\",type=\"breakpoint\",disp=\"keep\",enabled=\"y\"," +
            "addr=\"0x00000178\",func=\"main\",file=\"main.c\",fullname=\"/src/main.c\",line=\"12\",times=\"0\"}");
        Assert.NotNull(record);
        var bkpt = record.Results!["bkpt"] as JsonObject;
        Assert.NotNull(bkpt);
        Assert.Equal(1, MiClient.ParseNumber(bkpt["number"]));
        Assert.Equal("main", bkpt["func"]!.GetValue<string>());
        Assert.Equal("0x00000178", bkpt["addr"]!.GetValue<string>());
        Assert.Equal(12, MiClient.ParseNumber(bkpt["line"]));
    }

    [Fact]
    public void ExecStopped_ParsesReasonAndThread()
    {
        var record = MiParser.Parse(
            "*stopped,reason=\"breakpoint-hit\",disp=\"keep\",bkptno=\"1\",frame={addr=\"0x00000178\"," +
            "func=\"main\",args=[],file=\"main.c\",line=\"12\"},thread-id=\"1\",stopped-threads=\"all\"");
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.Exec, record.Type);
        Assert.Equal("stopped", record.Class);
        Assert.Equal("breakpoint-hit", record.Results!["reason"]!.GetValue<string>());
        // kebab-case names are preserved
        Assert.Equal(1, MiClient.ParseNumber(record.Results["thread-id"]));
        Assert.Equal("main", (record.Results["frame"] as JsonObject)!["func"]!.GetValue<string>());
    }

    [Fact]
    public void NotifyThreadCreated_Parses()
    {
        var record = MiParser.Parse("=thread-created,id=\"1\",group-id=\"i1\"");
        Assert.NotNull(record);
        Assert.Equal(MiRecordType.Notify, record.Type);
        Assert.Equal("thread-created", record.Class);
        Assert.Equal(1, MiClient.ParseNumber(record.Results!["id"]));
    }

    [Fact]
    public void StackListFrames_NamedListElements_GetTypeTag()
    {
        var record = MiParser.Parse(
            "^done,stack=[frame={level=\"0\",addr=\"0x100\",func=\"inner\"}," +
            "frame={level=\"1\",addr=\"0x200\",func=\"main\"}]");
        Assert.NotNull(record);
        var stack = record.Results!["stack"] as JsonArray;
        Assert.NotNull(stack);
        Assert.Equal(2, stack.Count);
        var top = stack[0] as JsonObject;
        Assert.NotNull(top);
        Assert.Equal("frame", top["$type"]!.GetValue<string>());
        Assert.Equal("inner", top["func"]!.GetValue<string>());
        Assert.Equal(0, MiClient.ParseNumber(top["level"]));
    }

    [Fact]
    public void ThreadInfo_ListOfTuples_Parses()
    {
        var record = MiParser.Parse(
            "^done,threads=[{id=\"1\",target-id=\"Thread 1\",frame={func=\"main\"},state=\"stopped\"}]," +
            "current-thread-id=\"1\"");
        Assert.NotNull(record);
        var threads = record.Results!["threads"] as JsonArray;
        Assert.NotNull(threads);
        var thread = Assert.IsType<JsonObject>(threads[0]);
        Assert.Equal("stopped", thread["state"]!.GetValue<string>());
        Assert.Equal(1, MiClient.ParseNumber(record.Results["current-thread-id"]));
    }

    [Fact]
    public void NumericConstants_BecomeNumbers()
    {
        var record = MiParser.Parse("^done,value=\"42\"");
        Assert.Equal(42.0, record!.Results!["value"]!.GetValue<double>());
    }

    [Fact]
    public void NonRoundTrippingNumbers_StayStrings()
    {
        // "0x100" and "012" don't round-trip through double formatting.
        var record = MiParser.Parse("^done,a=\"0x100\",b=\"012\"");
        Assert.Equal("0x100", record!.Results!["a"]!.GetValue<string>());
        Assert.Equal("012", record.Results["b"]!.GetValue<string>());
        Assert.Equal(0x100, MiClient.ParseNumber(record.Results["a"]));
    }

    [Fact]
    public void EmptyListAndTuple_Parse()
    {
        var record = MiParser.Parse("^done,args=[],dict={}");
        Assert.Empty((JsonArray)record!.Results!["args"]!);
        Assert.Empty((JsonObject)record.Results["dict"]!);
    }

    [Fact]
    public void EscapedQuotesAndBackslashes_Unescape()
    {
        var record = MiParser.Parse("~\"say \\\"hi\\\" c:\\\\dir\"");
        Assert.Equal("say \"hi\" c:\\dir", record!.Text);
    }

    [Fact]
    public void RegisterValues_ListParses()
    {
        var record = MiParser.Parse(
            "^done,register-values=[{number=\"0\",value=\"0x20001ff0\"},{number=\"1\",value=\"0\"}]");
        var values = record!.Results!["register-values"] as JsonArray;
        Assert.Equal(2, values!.Count);
        Assert.Equal("0x20001ff0", (values[0] as JsonObject)!["value"]!.GetValue<string>());
    }

    [Fact]
    public void GarbageLine_ReturnsNull()
    {
        Assert.Null(MiParser.Parse("some plain text"));
    }

    [Fact]
    public void MiQuote_QuotesWhenNeeded()
    {
        Assert.Equal("simple", MiClient.Quote("simple"));
        Assert.Equal("\"with space\"", MiClient.Quote("with space"));
        Assert.Equal("\"a\\\"b\"", MiClient.Quote("a\"b"));
        Assert.Equal("\"\"", MiClient.Quote(""));
    }
}
