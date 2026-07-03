using MinuteOS.Debug.Dwarf;

namespace MinuteOS.Debug.Trace;

/// <summary>
/// Resolves a PC to a function (ELF .symtab) and, for line-granularity views, a
/// source location (DWARF .debug_line). Both are loaded once from the program
/// ELF; missing debug info just means line resolution returns null.
/// </summary>
public sealed class Symbolizer
{
    private readonly ElfSymbols _symbols;
    private readonly DwarfLine? _lines;

    private Symbolizer(ElfSymbols symbols, DwarfLine? lines)
    {
        _symbols = symbols;
        _lines = lines;
    }

    public static Symbolizer? TryLoad(string? elfPath)
    {
        if (string.IsNullOrEmpty(elfPath) || !File.Exists(elfPath))
            return null;
        try
        {
            return new Symbolizer(ElfSymbols.Load(elfPath), DwarfLine.Load(elfPath));
        }
        catch
        {
            return null;
        }
    }

    public FunctionSymbol? Function(uint pc) => _symbols.Resolve(pc);

    public LineInfo? Line(uint pc) => _lines?.Resolve(pc);
}
