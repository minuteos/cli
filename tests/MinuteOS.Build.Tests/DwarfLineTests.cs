using System.Diagnostics;
using MinuteOS.Debug;
using MinuteOS.Debug.Dwarf;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The DWARF .debug_line reader must map addresses to the right source lines.
/// Gated on arm-none-eabi-gcc (as the dap-e2e is); a no-op when it is absent.
/// </summary>
public class DwarfLineTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _gcc = Which("arm-none-eabi-gcc");

    public DwarfLineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"minuteos-dwarf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Resolve_MapsAddressesToSourceLines()
    {
        if (_gcc == null)
            return; // toolchain unavailable

        var source = Path.Combine(_dir, "t.c");
        File.WriteAllText(source,
            "int add(int a, int b) {\n" +   // 1
            "    int s = a + b;\n" +          // 2
            "    return s;\n" +               // 3
            "}\n" +                           // 4
            "int mul(int a, int b) {\n" +     // 5
            "    int p = a * b;\n" +          // 6
            "    return p;\n" +               // 7
            "}\n" +                           // 8
            "int main(void) {\n" +            // 9
            "    return add(2, 3) + mul(4, 5);\n" + // 10
            "}\n");                           // 11

        var elf = Path.Combine(_dir, "t.elf");
        Run(_gcc, $"-g -O0 -ffreestanding -nostdlib -e main -o \"{elf}\" \"{source}\"");

        var symbols = ElfSymbols.Load(elf);
        var line = DwarfLine.Load(elf);
        Assert.NotNull(line);

        // Each function's body must resolve to t.c within its own line range.
        AssertFunctionLines(symbols, line!, "add", 1, 4);
        AssertFunctionLines(symbols, line!, "mul", 5, 8);
        AssertFunctionLines(symbols, line!, "main", 9, 11);
    }

    private static void AssertFunctionLines(ElfSymbols symbols, DwarfLine line, string name, int lo, int hi)
    {
        var fn = symbols.Functions.FirstOrDefault(f => f.Name == name);
        Assert.NotNull(fn);

        var resolved = line.Resolve(fn!.Address + 4); // a bit past the entry
        Assert.NotNull(resolved);
        Assert.Equal("t.c", Path.GetFileName(resolved!.File));
        Assert.InRange(resolved.Line, lo, hi);
    }

    private static string? Which(string tool)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardError = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} failed: {p.StandardError.ReadToEnd()}");
    }
}
