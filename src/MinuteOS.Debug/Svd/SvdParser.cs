using System.Xml.Linq;

namespace MinuteOS.Debug.Svd;

/// <summary>
/// CMSIS-SVD XML parser - the port of the extension's <c>svdFromXml</c>:
/// resolves <c>derivedFrom</c> peripherals (base registers merged, own
/// registers override by name), inherits register size from
/// device/peripheral, and normalizes the three field bit-range notations.
/// </summary>
public static class SvdParser
{
    public static SvdDevice Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root
            ?? throw new InvalidDataException("Empty SVD document");

        var device = new SvdDevice
        {
            Name = (string?)root.Element("name") ?? "device",
            Description = Normalize((string?)root.Element("description")),
            AddressUnitBits = (int?)ParseNumber(root.Element("addressUnitBits")) ?? 8,
            BigEndian = (string?)root.Element("cpu")?.Element("endian") == "big",
        };

        var defaultSize = (int?)ParseNumber(root.Element("size")) ?? 32;

        var sources = root.Element("peripherals")?.Elements("peripheral").ToList() ?? [];
        var byName = sources
            .Select(p => ((string?)p.Element("name"), p))
            .Where(x => x.Item1 != null)
            .DistinctBy(x => x.Item1)
            .ToDictionary(x => x.Item1!, x => x.p);

        var seen = new HashSet<XElement>();

        SvdPeripheral ParsePeripheral(XElement p)
        {
            if (!seen.Add(p))
                throw new InvalidDataException("Circular derivedFrom chain in SVD");

            var result = (string?)p.Attribute("derivedFrom") is { } baseName
                && byName.TryGetValue(baseName, out var baseElement)
                ? ParsePeripheral(baseElement).Clone()
                : new SvdPeripheral { Name = "" };
            seen.Remove(p);

            result.Name = (string?)p.Element("name") ?? result.Name;
            result.Description = Normalize((string?)p.Element("description")) ?? result.Description;
            result.GroupName = (string?)p.Element("groupName") ?? result.GroupName;
            if (ParseNumber(p.Element("baseAddress")) is { } baseAddress)
                result.BaseAddress = (ulong)baseAddress;

            var blocks = p.Elements("addressBlock").ToList();
            if (blocks.Count > 0)
            {
                // If defined, override anything from the base.
                result.AddressBlocks = blocks.Select(b => new SvdAddressBlock(
                    ParseNumber(b.Element("offset")) ?? 0,
                    ParseNumber(b.Element("size")) ?? 0)).ToList();
            }

            var peripheralSize = (int?)ParseNumber(p.Element("size")) ?? defaultSize;
            var registers = p.Element("registers")?.Elements("register").Select(r => ParseRegister(r, peripheralSize)).ToList();
            if (registers is { Count: > 0 })
            {
                // Patch the base's register list: own registers win by name.
                var newNames = registers.Select(r => r.Name).ToHashSet();
                result.Registers = [.. result.Registers.Where(r => !newNames.Contains(r.Name)), .. registers];
            }

            return result;
        }

        device.Peripherals = sources.Select(ParsePeripheral).ToList();
        return device;
    }

    private static SvdRegister ParseRegister(XElement r, int defaultSize) => new()
    {
        Name = (string?)r.Element("name") ?? "?",
        Description = Normalize((string?)r.Element("description")),
        AddressOffset = ParseNumber(r.Element("addressOffset")) ?? 0,
        Size = (int?)ParseNumber(r.Element("size")) ?? defaultSize,
        Fields = r.Element("fields")?.Elements("field").Select(ParseField).ToList(),
    };

    private static SvdField ParseField(XElement f)
    {
        // Three equivalent notations: bitOffset+bitWidth, lsb+msb, bitRange [msb:lsb].
        int offset, width;
        if (f.Element("bitOffset") != null)
        {
            offset = (int?)ParseNumber(f.Element("bitOffset")) ?? 0;
            width = (int?)ParseNumber(f.Element("bitWidth")) ?? 1;
        }
        else if (f.Element("lsb") != null)
        {
            offset = (int?)ParseNumber(f.Element("lsb")) ?? 0;
            width = ((int?)ParseNumber(f.Element("msb")) ?? offset) - offset + 1;
        }
        else if ((string?)f.Element("bitRange") is { } range && range.Trim('[', ']').Split(':') is [var msb, var lsb])
        {
            offset = int.Parse(lsb);
            width = int.Parse(msb) - offset + 1;
        }
        else
        {
            offset = 0;
            width = 1;
        }

        return new SvdField(
            (string?)f.Element("name") ?? "?",
            Normalize((string?)f.Element("description")),
            offset, width);
    }

    /// <summary>SVD numbers: decimal, 0x hex, or # binary.</summary>
    private static long? ParseNumber(XElement? element)
    {
        var s = ((string?)element)?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt64(s[2..], 16);
        if (s.StartsWith('#'))
            return Convert.ToInt64(s[1..].Replace("x", "0"), 2);
        return long.TryParse(s, out var n) ? n : null;
    }

    /// <summary>Collapses the whitespace runs SVD descriptions are full of.</summary>
    private static string? Normalize(string? text)
        => text == null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
