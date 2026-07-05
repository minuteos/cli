using System.Buffers.Binary;
using System.Text;

namespace MinuteOS.Debug.Dwarf;

/// <summary>A source location: a file path and 1-based line number.</summary>
public sealed record LineInfo(string File, int Line);

/// <summary>
/// A DWARF <c>.debug_line</c> reader: runs each compilation unit's line-number
/// program to build address→(file, line) rows, then resolves a PC to its source
/// location with a binary search. Supports DWARF v2-v5 (the v5 directory/file
/// table with <c>.debug_line_str</c>/<c>.debug_str</c> forms) and 32/64-bit
/// DWARF, little-endian - what arm-none-eabi-gcc emits.
/// </summary>
public sealed class DwarfLine
{
    private readonly ulong[] _addresses;   // sorted
    private readonly LineInfo?[] _rows;     // parallel; null marks an end-of-sequence gap

    private DwarfLine(ulong[] addresses, LineInfo?[] rows)
    {
        _addresses = addresses;
        _rows = rows;
    }

    /// <summary>The source location of <paramref name="pc"/>, or null when not covered.</summary>
    public LineInfo? Resolve(ulong pc)
    {
        int lo = 0, hi = _addresses.Length - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_addresses[mid] <= pc)
            {
                idx = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return idx >= 0 ? _rows[idx] : null;
    }

    /// <summary>Loads the line table from an ELF, or null when it has no <c>.debug_line</c>.</summary>
    public static DwarfLine? Load(string path)
    {
        var elf = new ElfImage(File.ReadAllBytes(path));
        if (elf.Section(".debug_line") is not { } debugLine)
            return null;

        var lineStr = elf.Section(".debug_line_str") ?? ReadOnlyMemory<byte>.Empty;
        var str = elf.Section(".debug_str") ?? ReadOnlyMemory<byte>.Empty;

        var rows = new List<(ulong Address, LineInfo? Row)>();
        var c = new Cursor(debugLine);
        while (c.Remaining > 0)
            ParseUnit(c, lineStr, str, rows);

        // Sort by address; at an equal address (an end_sequence marker landing on
        // the next unit's first real row after linking) put the null marker first
        // so Resolve - which returns the last row <= pc - keeps the real line.
        rows.Sort((a, b) =>
        {
            var byAddress = a.Address.CompareTo(b.Address);
            return byAddress != 0
                ? byAddress
                : (a.Row == null ? 0 : 1).CompareTo(b.Row == null ? 0 : 1);
        });
        return new DwarfLine(rows.Select(r => r.Address).ToArray(), rows.Select(r => r.Row).ToArray());
    }

    private static void ParseUnit(Cursor c, ReadOnlyMemory<byte> lineStr, ReadOnlyMemory<byte> str,
        List<(ulong, LineInfo?)> rows)
    {
        var unitLength = c.InitialLength(out var is64);
        var unitEnd = c.Pos + (int)unitLength;
        var version = c.U16();

        if (version >= 5)
        {
            c.U8(); // address_size
            c.U8(); // segment_selector_size
        }

        var headerLength = c.Offset(is64);
        var programStart = c.Pos + (int)headerLength;
        var minInstLen = c.U8();
        if (version >= 4)
            c.U8(); // maximum_operations_per_instruction (assumed 1 for ARM)
        var defaultIsStmt = c.U8() != 0;
        var lineBase = (sbyte)c.U8();
        var lineRange = c.U8();
        var opcodeBase = c.U8();
        var standardOpcodeLengths = new byte[opcodeBase];
        for (var i = 1; i < opcodeBase; i++)
            standardOpcodeLengths[i] = c.U8();

        var (dirs, files) = version >= 5
            ? ReadV5Tables(c, is64, lineStr, str)
            : ReadLegacyTables(c);

        string ResolvePath(int file)
        {
            if (file < 0 || file >= files.Count)
                return "?";
            var (name, dir) = files[file];
            if (Path.IsPathRooted(name) || dir < 0 || dir >= dirs.Count || dirs[dir].Length == 0)
                return name;
            return dirs[dir] + "/" + name;
        }

        // Line-number program state machine.
        c.Pos = programStart;
        ulong address = 0;
        int file = version >= 5 ? 1 : 1, line = 1;
        var isStmt = defaultIsStmt;
        _ = isStmt;

        void Reset()
        {
            address = 0;
            file = 1;
            line = 1;
            isStmt = defaultIsStmt;
        }

        while (c.Pos < unitEnd)
        {
            var opcode = c.U8();
            if (opcode == 0)
            {
                // Extended opcode.
                var length = (int)c.Uleb();
                var next = c.Pos + length;
                var sub = c.U8();
                switch (sub)
                {
                    case 1: // DW_LNE_end_sequence
                        rows.Add((address, null));
                        Reset();
                        break;
                    case 2: // DW_LNE_set_address
                        address = length - 1 >= 8 ? c.U64() : c.U32();
                        break;
                    default: // define_file / set_discriminator / vendor: skip
                        break;
                }
                c.Pos = next;
            }
            else if (opcode < opcodeBase)
            {
                switch (opcode)
                {
                    case 1: // DW_LNS_copy
                        rows.Add((address, new LineInfo(ResolvePath(file), line)));
                        break;
                    case 2: // DW_LNS_advance_pc
                        address += minInstLen * c.Uleb();
                        break;
                    case 3: // DW_LNS_advance_line
                        line += (int)c.Sleb();
                        break;
                    case 4: // DW_LNS_set_file
                        file = (int)c.Uleb();
                        break;
                    case 5: // DW_LNS_set_column
                        c.Uleb();
                        break;
                    case 6: // DW_LNS_negate_stmt
                        isStmt = !isStmt;
                        break;
                    case 7: // DW_LNS_set_basic_block
                        break;
                    case 8: // DW_LNS_const_add_pc
                        address += (ulong)(minInstLen * ((255 - opcodeBase) / lineRange));
                        break;
                    case 9: // DW_LNS_fixed_advance_pc
                        address += c.U16();
                        break;
                    case 10: // DW_LNS_set_prologue_end
                    case 11: // DW_LNS_set_epilogue_begin
                        break;
                    case 12: // DW_LNS_set_isa
                        c.Uleb();
                        break;
                    default: // unknown standard opcode: skip its ULEB operands
                        for (var k = 0; k < standardOpcodeLengths[opcode]; k++)
                            c.Uleb();
                        break;
                }
            }
            else
            {
                // Special opcode.
                var adjusted = opcode - opcodeBase;
                address += (ulong)(minInstLen * (adjusted / lineRange));
                line += lineBase + adjusted % lineRange;
                rows.Add((address, new LineInfo(ResolvePath(file), line)));
            }
        }

        c.Pos = unitEnd;
    }

    // DWARF v2-v4: NUL-terminated directory list then file entries, each list
    // empty-terminated. Directory 0 and file 0 are placeholders (1-based).
    private static (List<string> Dirs, List<(string Name, int Dir)> Files) ReadLegacyTables(Cursor c)
    {
        var dirs = new List<string> { "" };
        while (c.PeekU8() != 0)
            dirs.Add(c.CString());
        c.U8();

        var files = new List<(string, int)> { ("", 0) };
        while (c.PeekU8() != 0)
        {
            var name = c.CString();
            var dir = (int)c.Uleb();
            c.Uleb(); // mtime
            c.Uleb(); // size
            files.Add((name, dir));
        }
        c.U8();
        return (dirs, files);
    }

    // DWARF v5: format-described directory and file tables (0-based).
    private static (List<string> Dirs, List<(string Name, int Dir)> Files) ReadV5Tables(
        Cursor c, bool is64, ReadOnlyMemory<byte> lineStr, ReadOnlyMemory<byte> str)
    {
        var dirs = new List<string>();
        foreach (var e in ReadV5Entries(c, is64, lineStr, str))
            dirs.Add(e.Path ?? "");

        var files = new List<(string, int)>();
        foreach (var e in ReadV5Entries(c, is64, lineStr, str))
            files.Add((e.Path ?? "", e.DirIndex));

        return (dirs, files);
    }

    private static List<(string? Path, int DirIndex)> ReadV5Entries(
        Cursor c, bool is64, ReadOnlyMemory<byte> lineStr, ReadOnlyMemory<byte> str)
    {
        var formatCount = c.U8();
        var formats = new (ulong Content, ulong Form)[formatCount];
        for (var i = 0; i < formatCount; i++)
            formats[i] = (c.Uleb(), c.Uleb());

        var count = (int)c.Uleb();
        var entries = new List<(string?, int)>(count);
        for (var i = 0; i < count; i++)
        {
            string? path = null;
            var dirIndex = 0;
            foreach (var (content, form) in formats)
            {
                var (text, number) = ReadForm(c, form, is64, lineStr, str);
                switch (content)
                {
                    case 1: // DW_LNCT_path
                        path = text;
                        break;
                    case 2: // DW_LNCT_directory_index
                        dirIndex = (int)number;
                        break;
                }
            }
            entries.Add((path, dirIndex));
        }
        return entries;
    }

    private static (string? Text, ulong Number) ReadForm(
        Cursor c, ulong form, bool is64, ReadOnlyMemory<byte> lineStr, ReadOnlyMemory<byte> str)
    {
        switch (form)
        {
            case 0x08: return (c.CString(), 0);                                  // DW_FORM_string
            case 0x1f: return (ReadStr(lineStr, c.Offset(is64)), 0);             // DW_FORM_line_strp
            case 0x0e: return (ReadStr(str, c.Offset(is64)), 0);                 // DW_FORM_strp
            case 0x0b: return (null, c.U8());                                    // DW_FORM_data1
            case 0x05: return (null, c.U16());                                   // DW_FORM_data2
            case 0x06: return (null, c.U32());                                   // DW_FORM_data4
            case 0x07: return (null, c.U64());                                   // DW_FORM_data8
            case 0x0f: return (null, c.Uleb());                                  // DW_FORM_udata
            case 0x1e: c.Skip(16); return (null, 0);                             // DW_FORM_data16 (MD5)
            default: throw new NotSupportedException($"Unsupported DWARF line form 0x{form:x}");
        }
    }

    private static string ReadStr(ReadOnlyMemory<byte> section, ulong offset)
    {
        var span = section.Span;
        var start = (int)offset;
        var end = start;
        while (end < span.Length && span[end] != 0)
            end++;
        return Encoding.UTF8.GetString(span[start..end]);
    }

    /// <summary>A little-endian cursor over a DWARF section with LEB128 and initial-length helpers.</summary>
    private sealed class Cursor(ReadOnlyMemory<byte> memory)
    {
        private readonly ReadOnlyMemory<byte> _memory = memory;
        public int Pos { get; set; }

        public int Remaining => _memory.Length - Pos;
        private ReadOnlySpan<byte> Span => _memory.Span;

        public byte U8() => Span[Pos++];
        public byte PeekU8() => Span[Pos];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(Span[Pos..]); Pos += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(Span[Pos..]); Pos += 4; return v; }
        public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(Span[Pos..]); Pos += 8; return v; }
        public void Skip(int n) => Pos += n;

        public ulong InitialLength(out bool is64)
        {
            var length = U32();
            if (length == 0xffffffff)
            {
                is64 = true;
                return U64();
            }
            is64 = false;
            return length;
        }

        public ulong Offset(bool is64) => is64 ? U64() : U32();

        public ulong Uleb()
        {
            ulong result = 0;
            var shift = 0;
            while (true)
            {
                var b = U8();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
        }

        public long Sleb()
        {
            long result = 0;
            var shift = 0;
            byte b;
            do
            {
                b = U8();
                result |= (long)(b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);
            if (shift < 64 && (b & 0x40) != 0)
                result |= -1L << shift;
            return result;
        }

        public string CString()
        {
            var span = Span;
            var end = Pos;
            while (end < span.Length && span[end] != 0)
                end++;
            var s = Encoding.UTF8.GetString(span[Pos..end]);
            Pos = end + 1;
            return s;
        }
    }
}
