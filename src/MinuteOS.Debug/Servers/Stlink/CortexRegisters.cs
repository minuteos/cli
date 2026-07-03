namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// The Cortex-M GDB register layout (indices, features, and the xPSR/CONTROL
/// bitfield types) - the port of the register tables in <c>target/cortex.ts</c>.
/// </summary>
internal static class CortexRegisters
{
    private const string MProfile = "org.gnu.gdb.arm.m-profile";
    private const string MSystem = "org.gnu.gdb.arm.m-system";

    /// <summary>Number of core registers (indices 0x00..0x14) loaded as one batch.</summary>
    public const int CoreCount = 0x15;

    public static readonly IReadOnlyList<RegisterInfo?> All = Build();
    public static readonly IReadOnlyList<RegisterTypeInfo> Types = BuildTypes();

    private static RegisterInfo?[] Build()
    {
        var regs = new List<RegisterInfo?>();
        for (var n = 0; n < 13; n++)
            regs.Add(new RegisterInfo(MProfile, "general", $"r{n}", 32));
        regs.Add(new RegisterInfo(MProfile, "general", "sp", 32, "data_ptr"));
        regs.Add(new RegisterInfo(MProfile, "core", "lr", 32, "code_ptr"));
        regs.Add(new RegisterInfo(MProfile, "core", "pc", 32, "code_ptr"));
        regs.Add(new RegisterInfo(MProfile, "system", "xpsr", 32, "xpsr"));   // 0x10
        regs.Add(new RegisterInfo(MSystem, "system", "msp", 32, "data_ptr")); // 0x11
        regs.Add(new RegisterInfo(MSystem, "system", "psp", 32, "data_ptr")); // 0x12
        regs.Add(null);                                                        // 0x13
        regs.Add(new RegisterInfo(MSystem, "system", "spr", 32, "spr"));       // 0x14

        while (regs.Count < 0x1F)
            regs.Add(null);                                                    // 0x15..0x1E
        regs.Add(new RegisterInfo(MProfile, "float", "fpscr", 32));           // 0x1F
        for (var n = 0; n < 32; n++)
            regs.Add(new RegisterInfo(MProfile, "float", $"s{n}", 32, "float"));

        return regs.ToArray();
    }

    private static RegisterTypeInfo[] BuildTypes() =>
    [
        new("apsr", "flags", 4,
        [
            new RegisterTypeField("n", 31, 31),
            new RegisterTypeField("z", 30, 30),
            new RegisterTypeField("c", 29, 29),
            new RegisterTypeField("v", 28, 28),
            new RegisterTypeField("q", 27, 27),
            new RegisterTypeField("ge", 16, 19),
        ]),
        new("ipsr", "flags", 4, [new RegisterTypeField("exception", 0, 8)]),
        new("epsr", "flags", 4,
        [
            new RegisterTypeField("thumb", 24, 24),
            new RegisterTypeField("b", 21, 21),
            new RegisterTypeField("it", 10, 15),
            new RegisterTypeField("it2", 25, 27),
        ]),
        new("xpsr", "union", null,
        [
            new RegisterTypeField("apsr", null, null, "apsr"),
            new RegisterTypeField("ipsr", null, null, "ipsr"),
            new RegisterTypeField("epsr", null, null, "epsr"),
        ]),
        new("control", "flags", 1,
        [
            new RegisterTypeField("npriv", 0, 0),
            new RegisterTypeField("spsel", 1, 1),
            new RegisterTypeField("fpca", 2, 2),
            new RegisterTypeField("sfpa", 3, 3),
        ]),
        new("spr", "struct", 4,
        [
            new RegisterTypeField("control", 24, 31, "control"),
            new RegisterTypeField("basepri", 0, 7, "uint"),
            new RegisterTypeField("primask", 0, 0),
            new RegisterTypeField("faultmask", 8, 8),
        ]),
    ];
}
