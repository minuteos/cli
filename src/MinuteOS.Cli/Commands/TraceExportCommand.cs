using System.Text;
using System.Text.Json.Nodes;
using MinuteOS.Debug;
using MinuteOS.Debug.Trace;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Exports an <c>mtrace</c> timeline (the unified PC-sample / SMU / log format
/// written by <c>minuteos dap</c>) into a display-friendly format. Today that is
/// JSONL - one event per line - which any timeline tool can ingest; the binary
/// format is the lossless source these views are derived from.
/// </summary>
[Command("trace", "export", Description = "Export an mtrace timeline to a display format (JSONL)")]
public class TraceExportCommand : LoggingCommand
{
    [Argument(Description = "The .mtrace file to export")]
    public string File { get; set; } = "";

    [Option("--format", Description = "Output format (jsonl)")]
    public string Format { get; set; } = "jsonl";

    [Option("--elf", Description = "Program ELF used to symbolicate PC samples")]
    public string? Elf { get; set; }

    [Option("--output", "-o", Description = "Output file (default: stdout)")]
    public string? Output { get; set; }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(File))
        {
            Logger.LogError("No such trace file: {File}", File);
            return 1;
        }
        if (Format != "jsonl")
        {
            Logger.LogError("Unsupported format '{Format}' (supported: jsonl)", Format);
            return 1;
        }

        Func<uint, FunctionSymbol?> resolve = _ => null;
        if (Elf != null)
        {
            try
            {
                var symbols = ElfSymbols.Load(Elf);
                resolve = pc => symbols.Resolve(pc);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Cannot load symbols from {Elf}: {Message}", Elf, ex.Message);
            }
        }

        await using var input = System.IO.File.OpenRead(File);
        var reader = new TraceReader(input);
        var start = reader.Header.StartUnixNanos;
        var channels = new Dictionary<int, ChannelDefEvent>();

        TextWriter output = Output != null ? new StreamWriter(Output) : Console.Out;
        try
        {
            foreach (var e in reader.Events())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (e is ChannelDefEvent def)
                    channels[def.Channel] = def;
                await output.WriteLineAsync(ToJson(e, start, channels, resolve).ToJsonString());
            }
        }
        finally
        {
            if (Output != null)
                await output.DisposeAsync();
        }
        return 0;
    }

    private static JsonObject ToJson(TraceEvent e, long start,
        IReadOnlyDictionary<int, ChannelDefEvent> channels, Func<uint, FunctionSymbol?> resolve)
    {
        var obj = new JsonObject
        {
            ["t"] = start + e.TimeNs, // absolute host time, ns since the Unix epoch
            ["dt"] = e.TimeNs,        // ns since the recording started
        };

        switch (e)
        {
            case PcSampleEvent pc:
                obj["kind"] = "pc";
                if (pc.Sleep)
                {
                    obj["sleep"] = true;
                }
                else
                {
                    obj["pc"] = $"0x{pc.Pc:x8}";
                    if (resolve(pc.Pc) is { } fn)
                        obj["fn"] = fn.Name;
                }
                break;

            case LogEvent log:
                obj["kind"] = "log";
                obj["port"] = log.Port;
                obj["text"] = Encoding.UTF8.GetString(log.Data);
                break;

            case MeasurementEvent m:
                obj["kind"] = "sample";
                obj["ch"] = m.Channel;
                obj["raw"] = m.Raw;
                if (channels.TryGetValue(m.Channel, out var channel))
                {
                    obj["value"] = m.Raw * channel.Scale;
                    obj["unit"] = channel.Unit;
                    obj["name"] = channel.Name;
                }
                break;

            case ChannelDefEvent def:
                obj["kind"] = "channel";
                obj["ch"] = def.Channel;
                obj["name"] = def.Name;
                obj["unit"] = def.Unit;
                obj["scale"] = def.Scale;
                obj["type"] = def.Kind.ToString().ToLowerInvariant();
                break;

            case MarkEvent mark:
                obj["kind"] = "mark";
                obj["mark"] = mark.Kind.ToString().ToLowerInvariant();
                if (mark.Text.Length > 0)
                    obj["text"] = mark.Text;
                break;
        }

        return obj;
    }
}
