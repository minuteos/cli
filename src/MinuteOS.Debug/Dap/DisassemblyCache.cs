using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug.Dap;

public sealed record DisassemblySource(string? File, string? FullName, int Line)
{
    public int? EndLine { get; set; }
}

public sealed record DisassembledInstruction(long Start, long End, string Mnemonic)
{
    public string? Bytes { get; init; }
    public DisassemblySource? Source { get; init; }
    public string? Function { get; init; }
    public long Offset { get; init; }
}

/// <summary>
/// Address-range cache over <c>-data-disassemble</c> - the port of the
/// extension's <c>DisassemblyCache</c>. Keeps a sorted list of contiguous,
/// non-overlapping ranges covering the whole address space; unknown ranges
/// are disassembled on demand and split around the result, so scrolling the
/// disassembly view up/down reuses everything already decoded.
/// </summary>
public sealed class DisassemblyCache(Func<long, CancellationToken, Task<JsonArray>> disassemble, ILogger logger)
{
    private abstract record Range(long Start, long End);
    private sealed record UnknownRange(long Start, long End) : Range(Start, End)
    {
        public Task? Loading { get; set; }
    }
    private sealed record KnownRange(long Start, long End, List<DisassembledInstruction> Instructions) : Range(Start, End);

    private readonly List<Range> _ranges =
    [
        ErrorRange(long.MinValue, 0, "Out of bounds"),
        new UnknownRange(0, long.MaxValue),
    ];

    private static KnownRange ErrorRange(long start, long end, string message)
        => new(start, end, [new DisassembledInstruction(start, end, message)]);

    private static (T Range, int Index) FindRange<T>(List<T> ranges, long address, Func<T, (long S, long E)> bounds)
    {
        int low = 0, high = ranges.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var (s, e) = bounds(ranges[mid]);
            if (address < s)
                high = mid - 1;
            else if (address >= e)
                low = mid + 1;
            else
                return (ranges[mid], mid);
        }
        throw new InvalidOperationException($"No disassembly range for 0x{address:x} - cache corrupted");
    }

    private async Task<KnownRange> ResolveRangeAsync(long address, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (range, _) = FindRange(_ranges, address, r => (r.Start, r.End));
            if (range is KnownRange known)
                return known;

            var unknown = (UnknownRange)range;
            if (unknown.Loading is { } loading)
            {
                await loading.WaitAsync(cancellationToken);
                continue;
            }
            await (unknown.Loading = LoadSectionAsync(unknown, address, cancellationToken));
        }
    }

    /// <summary>Disassembles part of an unknown range; never throws (errors become an error range).</summary>
    private async Task LoadSectionAsync(UnknownRange range, long address, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("Requesting disassembly @ 0x{Address:x}", address);
            var asmInsns = await disassemble(address, cancellationToken);

            DisassemblySource? source = null;
            var instructions = new List<DisassembledInstruction>();

            void Add(JsonObject ins)
            {
                var start = MiClient.ParseNumberLong(ins["address"]);
                var opcodes = ins["opcodes"]?.ToString() ?? "";
                var end = start + (opcodes.Length + 1) / 3; // "aa bb cc" -> byte count

                if (start >= range.Start && end <= range.End)
                {
                    instructions.Add(new DisassembledInstruction(start, end, ins["inst"]?.ToString() ?? "?")
                    {
                        Bytes = opcodes,
                        Source = source,
                        Function = ins["func-name"]?.ToString(),
                        Offset = MiClient.ParseNumberLong(ins["offset"]),
                    });
                }
            }

            foreach (var node in asmInsns)
            {
                if (node is not JsonObject item)
                    continue;
                if (item["$type"]?.GetValue<string>() == "src_and_asm_line")
                {
                    var line = MiClient.ParseNumber(item["line"]);
                    if (source != null)
                        source.EndLine = line;
                    else
                        source = new DisassemblySource(
                            item["file"]?.ToString(), item["fullname"]?.ToString(), line);

                    if (item["line_asm_insn"] is JsonArray subs && subs.Count > 0)
                    {
                        foreach (var sub in subs.OfType<JsonObject>())
                            Add(sub);
                        source = null;
                    }
                }
                else
                {
                    source = null;
                    Add(item);
                }
            }

            if (instructions.Count > 0)
            {
                logger.LogDebug("Disassembled {Count} instructions between 0x{Start:x} and 0x{End:x}",
                    instructions.Count, instructions[0].Start, instructions[^1].End);
                ReplaceRange(range, new KnownRange(instructions[0].Start, instructions[^1].End, instructions));
            }
            else
            {
                logger.LogDebug("Failed to disassemble any instructions at 0x{Address:x}", address);
                ReplaceRange(range, ErrorRange(address, address + 1, "??"));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disassembling at 0x{Address:x}", address);
            ReplaceRange(range, ErrorRange(address, address + 1, ex.Message));
        }
    }

    private void ReplaceRange(UnknownRange outer, KnownRange inner)
    {
        var (range, index) = FindRange(_ranges, outer.Start, r => (r.Start, r.End));
        if (!ReferenceEquals(range, outer))
        {
            logger.LogWarning("Disassembly range lost");
            return;
        }

        var replacement = new List<Range>();
        if (inner.Start > outer.Start)
            replacement.Add(new UnknownRange(outer.Start, inner.Start));
        replacement.Add(inner);
        if (inner.End < outer.End)
            replacement.Add(new UnknownRange(inner.End, outer.End));
        _ranges.RemoveAt(index);
        _ranges.InsertRange(index, replacement);
    }

    /// <summary>
    /// Returns <paramref name="count"/> instructions starting
    /// <paramref name="offset"/> instructions away from the one containing
    /// <paramref name="address"/> (offset may be negative).
    /// </summary>
    public async Task<List<DisassembledInstruction>> FillAsync(long address, int offset, int count,
        CancellationToken cancellationToken = default)
    {
        var range = await ResolveRangeAsync(address, cancellationToken);
        var (_, i) = FindRange(range.Instructions, address, ins => (ins.Start, ins.End));
        i += offset; // instruction index relative to the start of the current range

        while (i < 0)
        {
            // need previous ranges
            range = await ResolveRangeAsync(range.Start - 1, cancellationToken);
            if (range.Instructions.Count == 0)
            {
                i = 0;
                break;
            }
            i += range.Instructions.Count;
        }

        var result = new List<DisassembledInstruction>();
        while (true)
        {
            var instructions = range.Instructions;
            while (i < instructions.Count)
            {
                result.Add(instructions[i++]);
                if (result.Count >= count)
                    return result;
            }
            i -= instructions.Count;
            range = await ResolveRangeAsync(range.End, cancellationToken);
        }
    }
}
