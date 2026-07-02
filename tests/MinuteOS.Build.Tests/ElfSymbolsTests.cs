using System.Diagnostics;
using MinuteOS.Debug;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Compiles small fixtures with the host and ARM toolchains (each test is
/// skipped when the compiler is missing) and checks PC-to-function
/// resolution against the real symbol tables.
/// </summary>
public class ElfSymbolsTests
{
    private const string Fixture = """
        int global_counter;
        __attribute__((noinline)) int alpha(int x) { return x + ++global_counter; }
        __attribute__((noinline)) int beta(int x) { return alpha(x) * 2; }
        int main(void) { return beta(3); }
        """;

    private static string? Compile(string compiler, params string[] extraArgs)
    {
        var dir = Directory.CreateTempSubdirectory("minuteos-elf-test-").FullName;
        var source = Path.Combine(dir, "fixture.c");
        var elf = Path.Combine(dir, "fixture.elf");
        File.WriteAllText(source, Fixture);
        try
        {
            var psi = new ProcessStartInfo(compiler)
            {
                RedirectStandardError = true,
            };
            foreach (var arg in (string[])["-g", "-O0", .. extraArgs, "-o", elf, source])
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode == 0 ? elf : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // compiler not installed
        }
    }

    private static void AssertResolves(string elf)
    {
        var symbols = ElfSymbols.Load(elf);

        foreach (var name in (string[])["alpha", "beta", "main"])
        {
            var symbol = symbols.Functions.SingleOrDefault(f => f.Name == name);
            Assert.NotNull(symbol);
            Assert.True(symbol.Size > 0);

            // entry, mid-function, and last byte all resolve to the function
            Assert.Equal(name, symbols.Resolve(symbol.Address)?.Name);
            Assert.Equal(name, symbols.Resolve(symbol.Address + symbol.Size / 2)?.Name);
            Assert.Equal(name, symbols.Resolve(symbol.Address + symbol.Size - 1)?.Name);
            // thumb bit is ignored
            Assert.Equal(name, symbols.Resolve(symbol.Address | 1)?.Name);
        }

        // data symbols don't pollute the function table
        Assert.DoesNotContain(symbols.Functions, f => f.Name == "global_counter");
    }

    [Fact]
    public void HostElf_FunctionsResolve()
    {
        if (Compile("cc") is not { } elf)
            return;
        AssertResolves(elf);
        Assert.Null(ElfSymbols.Load(elf).Resolve(2)); // in the void
    }

    [Fact]
    public void ArmElf32_FunctionsResolve()
    {
        if (Compile("arm-none-eabi-gcc", "-mcpu=cortex-m3", "-mthumb", "--specs=nosys.specs") is not { } elf)
            return;
        var symbols = ElfSymbols.Load(elf);
        AssertResolves(elf);

        // Thumb functions: addresses stored with bit 0 set in the symtab must
        // come back cleared.
        Assert.All(symbols.Functions, f => Assert.Equal(0ul, f.Address & 1));
    }

    [Fact]
    public void NonElf_Throws()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not an elf");
        try
        {
            Assert.Throws<InvalidDataException>(() => ElfSymbols.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
