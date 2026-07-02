namespace MinuteOS.Debug.Svd;

/// <summary>A parsed CMSIS-SVD device description (the subset the debugger uses).</summary>
public sealed class SvdDevice
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Bits per address unit (8 for byte-addressable devices).</summary>
    public int AddressUnitBits { get; set; } = 8;
    public bool BigEndian { get; set; }
    public List<SvdPeripheral> Peripherals { get; set; } = [];
}

public sealed class SvdPeripheral
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? GroupName { get; set; }
    public ulong BaseAddress { get; set; }
    public List<SvdAddressBlock> AddressBlocks { get; set; } = [];
    public List<SvdRegister> Registers { get; set; } = [];

    public SvdPeripheral Clone() => new()
    {
        Name = Name,
        Description = Description,
        GroupName = GroupName,
        BaseAddress = BaseAddress,
        AddressBlocks = [.. AddressBlocks],
        Registers = [.. Registers],
    };
}

public sealed record SvdAddressBlock(long Offset, long Size);

public sealed class SvdRegister
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public long AddressOffset { get; set; }
    /// <summary>Register size in bits.</summary>
    public int Size { get; set; } = 32;
    public List<SvdField>? Fields { get; set; }
}

public sealed record SvdField(string Name, string? Description, int BitOffset, int BitWidth);
