using System.Buffers.Binary;
using System.Text;

namespace MinuteOS.Debug.Dwarf;

/// <summary>
/// Reads ELF sections by name (little-endian ELF32/ELF64) - enough to reach the
/// DWARF sections. Separate from <see cref="ElfSymbols"/>, which finds .symtab
/// by type; here we need name lookup (.debug_line, .debug_line_str, .debug_str).
/// </summary>
internal sealed class ElfImage
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (long Offset, long Size)> _sections = new(StringComparer.Ordinal);

    public bool Is64 { get; }

    public ElfImage(byte[] data)
    {
        _data = data;
        if (data.Length < 0x34 || data[0] != 0x7F || data[1] != 'E' || data[2] != 'L' || data[3] != 'F')
            throw new InvalidDataException("Not an ELF file");
        Is64 = data[4] == 2;
        if (data[5] != 1)
            throw new NotSupportedException("Big-endian ELF is not supported");

        var is64 = Is64;
        var shoff = is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x28)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x20));
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(is64 ? 0x3A : 0x2E));
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(is64 ? 0x3C : 0x30));
        var shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(is64 ? 0x3E : 0x32));

        static (uint Name, long Offset, long Size) Section(byte[] d, bool is64, long shoff, int shentsize, int i)
        {
            var sh = d.AsSpan((int)(shoff + (long)i * shentsize));
            return (BinaryPrimitives.ReadUInt32LittleEndian(sh),
                is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(sh[0x18..]) : BinaryPrimitives.ReadUInt32LittleEndian(sh[0x10..]),
                is64 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(sh[0x20..]) : BinaryPrimitives.ReadUInt32LittleEndian(sh[0x14..]));
        }

        var strTab = Section(data, is64, shoff, shentsize, shstrndx).Offset;
        for (var i = 0; i < shnum; i++)
        {
            var (name, offset, size) = Section(data, is64, shoff, shentsize, i);
            _sections[ReadCString(strTab + name)] = (offset, size);
        }
    }

    public ReadOnlyMemory<byte>? Section(string name)
        => _sections.TryGetValue(name, out var s) ? _data.AsMemory((int)s.Offset, (int)s.Size) : null;

    private string ReadCString(long offset)
    {
        var end = (int)offset;
        while (end < _data.Length && _data[end] != 0)
            end++;
        return Encoding.UTF8.GetString(_data, (int)offset, end - (int)offset);
    }
}
