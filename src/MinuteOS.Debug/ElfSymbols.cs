using System.Buffers.Binary;

namespace MinuteOS.Debug;

/// <summary>A function symbol: [Address, Address+Size).</summary>
public sealed record FunctionSymbol(ulong Address, ulong Size, string Name);

/// <summary>
/// Minimal ELF symbol-table reader for PC symbolication: loads STT_FUNC
/// symbols from .symtab (or .dynsym) once, sorted by address, and resolves
/// program-counter values with a binary search - O(log n) per unique PC.
/// Handles little-endian ELF32 (the ARM targets) and ELF64 (host builds);
/// Thumb bit is cleared on both symbol addresses and lookups.
/// </summary>
public sealed class ElfSymbols
{
    private const ushort EmArm = 0x28;

    private readonly FunctionSymbol[] _functions; // sorted by Address
    private readonly bool _thumb;

    public IReadOnlyList<FunctionSymbol> Functions => _functions;

    private ElfSymbols(FunctionSymbol[] functions, bool thumb)
    {
        _functions = functions;
        _thumb = thumb;
    }

    /// <summary>Finds the function containing <paramref name="pc"/>, or null.</summary>
    public FunctionSymbol? Resolve(ulong pc)
    {
        if (_thumb)
            pc &= ~1ul; // thumb bit
        int low = 0, high = _functions.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var f = _functions[mid];
            if (pc < f.Address)
                high = mid - 1;
            else if (pc >= f.Address + f.Size)
                low = mid + 1;
            else
                return f;
        }
        return null;
    }

    public static ElfSymbols Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 0x34 || data[0] != 0x7F || data[1] != 'E' || data[2] != 'L' || data[3] != 'F')
            throw new InvalidDataException($"Not an ELF file: {path}");
        var is64 = data[4] == 2;
        if (data[5] != 1)
            throw new NotSupportedException("Big-endian ELF is not supported");

        var span = data.AsSpan();
        // On 32-bit ARM, Thumb function addresses carry bit 0 in st_value;
        // elsewhere (e.g. host x86-64) odd addresses are legitimate.
        var thumb = BinaryPrimitives.ReadUInt16LittleEndian(span[0x12..]) == EmArm && !is64;
        var shoff = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(span[0x28..]) : BinaryPrimitives.ReadUInt32LittleEndian(span[0x20..]);
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(span[(is64 ? 0x3A : 0x2E)..]);
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(span[(is64 ? 0x3C : 0x30)..]);

        // Find .symtab (SHT_SYMTAB=2), falling back to .dynsym (SHT_DYNSYM=11).
        (long Offset, long Size, long EntSize, int StrTab)? symtab = null;
        for (var i = 0; i < shnum; i++)
        {
            var sh = span[(int)(shoff + i * shentsize)..];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(sh[4..]);
            if (type is not (2 or 11))
                continue;
            var section = is64
                ? ((long)BinaryPrimitives.ReadUInt64LittleEndian(sh[0x18..]),
                   (long)BinaryPrimitives.ReadUInt64LittleEndian(sh[0x20..]),
                   (long)BinaryPrimitives.ReadUInt64LittleEndian(sh[0x38..]),
                   (int)BinaryPrimitives.ReadUInt32LittleEndian(sh[0x28..]))
                : ((long)BinaryPrimitives.ReadUInt32LittleEndian(sh[0x10..]),
                   (long)BinaryPrimitives.ReadUInt32LittleEndian(sh[0x14..]),
                   (long)BinaryPrimitives.ReadUInt32LittleEndian(sh[0x24..]),
                   (int)BinaryPrimitives.ReadUInt32LittleEndian(sh[0x18..]));
            if (type == 2)
            {
                symtab = section;
                break; // .symtab wins
            }
            symtab ??= section;
        }
        if (symtab is not { } table || table.EntSize == 0)
            return new ElfSymbols([], thumb);

        // The linked string table section.
        var strSh = span[(int)(shoff + table.StrTab * shentsize)..];
        var strOffset = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(strSh[0x18..]) : BinaryPrimitives.ReadUInt32LittleEndian(strSh[0x10..]);
        var strSize = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(strSh[0x20..]) : BinaryPrimitives.ReadUInt32LittleEndian(strSh[0x14..]);

        var functions = new List<FunctionSymbol>();
        for (var offset = table.Offset; offset + table.EntSize <= table.Offset + table.Size; offset += table.EntSize)
        {
            var sym = span[(int)offset..];
            var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(sym);
            byte info;
            ulong value, size;
            ushort shndx;
            if (is64)
            {
                info = sym[4];
                shndx = BinaryPrimitives.ReadUInt16LittleEndian(sym[6..]);
                value = BinaryPrimitives.ReadUInt64LittleEndian(sym[8..]);
                size = BinaryPrimitives.ReadUInt64LittleEndian(sym[16..]);
            }
            else
            {
                value = BinaryPrimitives.ReadUInt32LittleEndian(sym[4..]);
                size = BinaryPrimitives.ReadUInt32LittleEndian(sym[8..]);
                info = sym[12];
                shndx = BinaryPrimitives.ReadUInt16LittleEndian(sym[14..]);
            }

            if ((info & 0xF) != 2 || shndx == 0 || nameIndex == 0 || nameIndex >= strSize)
                continue; // not a defined, named STT_FUNC

            var nameStart = (int)(strOffset + nameIndex);
            var nameEnd = nameStart;
            while (nameEnd < strOffset + strSize && data[nameEnd] != 0)
                nameEnd++;
            if (thumb)
                value &= ~1ul;
            functions.Add(new FunctionSymbol(value, size, System.Text.Encoding.UTF8.GetString(data, nameStart, nameEnd - nameStart)));
        }

        functions.Sort((a, b) => a.Address.CompareTo(b.Address));

        // Zero-size symbols (assembly labels): extend to the next symbol so
        // samples inside them still resolve - but only when they fill a gap. A
        // label sitting inside an already-sized function must not be stretched
        // over it, or it would shadow the real function during lookup.
        var coveredTo = 0ul;
        for (var i = 0; i < functions.Count; i++)
        {
            if (functions[i].Size == 0)
            {
                if (functions[i].Address < coveredTo)
                    continue; // interior label - leave size 0 (filtered out below)
                var end = i + 1 < functions.Count ? functions[i + 1].Address : functions[i].Address + 2;
                functions[i] = functions[i] with { Size = end - functions[i].Address };
            }
            coveredTo = Math.Max(coveredTo, functions[i].Address + functions[i].Size);
        }

        return new ElfSymbols(functions.Where(f => f.Size > 0).ToArray(), thumb);
    }
}
