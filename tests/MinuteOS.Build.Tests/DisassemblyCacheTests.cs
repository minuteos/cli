using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Debug.Dap;

namespace MinuteOS.Build.Tests;

public class DisassemblyCacheTests
{
    /// <summary>
    /// A fake -data-disassemble: 2-byte instructions, 8 per request, wrapped
    /// in src_and_asm_line groups like gdb's --source output.
    /// </summary>
    private sealed class FakeDisassembler
    {
        public int Requests;

        public Task<JsonArray> DisassembleAsync(long address, CancellationToken _)
        {
            Requests++;
            var start = address & ~1L; // align
            var group = new JsonObject
            {
                ["$type"] = "src_and_asm_line",
                ["line"] = 10,
                ["file"] = "main.c",
                ["fullname"] = "/src/main.c",
                ["line_asm_insn"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode)new JsonObject
                {
                    ["address"] = $"0x{start + i * 2:x}",
                    ["inst"] = $"insn_{start + i * 2:x}",
                    ["opcodes"] = "aa bb",
                    ["func-name"] = "main",
                    ["offset"] = start + i * 2 - 0x100,
                }).ToArray()),
            };
            return Task.FromResult(new JsonArray(group));
        }
    }

    [Fact]
    public async Task Fill_ReturnsRequestedWindow()
    {
        var fake = new FakeDisassembler();
        var cache = new DisassemblyCache(fake.DisassembleAsync, NullLogger.Instance);

        var instructions = await cache.FillAsync(0x100, 0, 4);
        Assert.Equal(4, instructions.Count);
        Assert.Equal(0x100, instructions[0].Start);
        Assert.Equal("insn_100", instructions[0].Mnemonic);
        Assert.Equal("main.c", instructions[0].Source?.File);
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task Fill_CachesResolvedRanges()
    {
        var fake = new FakeDisassembler();
        var cache = new DisassemblyCache(fake.DisassembleAsync, NullLogger.Instance);

        await cache.FillAsync(0x100, 0, 4);
        await cache.FillAsync(0x102, 0, 2); // fully inside the cached range
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task Fill_NegativeOffset_WalksBackwards()
    {
        var fake = new FakeDisassembler();
        var cache = new DisassemblyCache(fake.DisassembleAsync, NullLogger.Instance);

        // instructions before 0x100 require resolving the previous range
        var instructions = await cache.FillAsync(0x100, -2, 4);
        Assert.Equal(4, instructions.Count);
        Assert.Equal(0x100 - 4, instructions[0].Start);
        Assert.True(fake.Requests >= 2);
    }

    [Fact]
    public async Task Fill_SpansForwardAcrossRanges()
    {
        var fake = new FakeDisassembler();
        var cache = new DisassemblyCache(fake.DisassembleAsync, NullLogger.Instance);

        // 8 per request, ask for 12 -> two ranges stitched together
        var instructions = await cache.FillAsync(0x100, 0, 12);
        Assert.Equal(12, instructions.Count);
        Assert.Equal(0x100, instructions[0].Start);
        Assert.Equal(0x100 + 11 * 2, instructions[11].Start);
    }

    [Fact]
    public async Task Fill_ErrorsBecomeErrorInstructions()
    {
        var cache = new DisassemblyCache(
            (_, _) => throw new InvalidOperationException("no memory there"),
            NullLogger.Instance);

        var instructions = await cache.FillAsync(0x100, 0, 1);
        Assert.Contains("no memory there", instructions[0].Mnemonic);
    }
}
