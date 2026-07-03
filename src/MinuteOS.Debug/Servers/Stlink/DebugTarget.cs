namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// A debug access port: the raw memory/register/run-control operations a probe
/// exposes. The port of the extension's <c>DebugAccessPort</c> interface.
/// </summary>
public interface IDebugAccessPort : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<byte[]> ReadCpuIdAsync(CancellationToken cancellationToken = default);
    Task<byte[]?> ReadRegisterAsync(int index, CancellationToken cancellationToken = default);
    Task WriteRegisterAsync(int index, byte[] value, CancellationToken cancellationToken = default);
    Task<byte[]?[]> ReadCoreRegistersAsync(CancellationToken cancellationToken = default);
    Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default);
    Task WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task StepAsync(CancellationToken cancellationToken = default);
    Task ContinueAsync(CancellationToken cancellationToken = default);
}

/// <summary>GDB signal numbers reported as the stop reason.</summary>
public enum StopSignal : byte
{
    Interrupt = 2,
    IllegalInstruction = 4,
    Trap = 5,
    FpException = 8,
    BusError = 10,
    SegmentationFault = 11,
    Terminated = 15,
}

public sealed record DebugTargetInfo(
    string Architecture, string Device, string? Revision = null, string? Uid = null, int FpbComparators = 0);

public sealed class DebugThread
{
    public required int Id { get; init; }
    public StopSignal? StopReason { get; set; }
    public string? ExtraInfo { get; init; }
}

/// <summary>Register metadata for the GDB target description (target.xml).</summary>
public sealed record RegisterInfo(string GdbFeature, string Group, string Name, int Bits, string? Type = null);

public sealed record RegisterTypeField(string Name, int? BitLo, int? BitHi, string? Type = null);

public sealed record RegisterTypeInfo(string Id, string Kind, int? Size, IReadOnlyList<RegisterTypeField> Fields);

/// <summary>
/// A live debug target - run control, threads, registers and memory - the port
/// of the extension's <c>DebugTarget</c>. Backs the internal GDB server.
/// </summary>
public interface IDebugTarget
{
    DebugTargetInfo Info { get; }
    IReadOnlyList<DebugThread> Threads { get; }

    /// <summary>Register metadata; may contain holes (null) but indices must line up with GDB register numbers.</summary>
    IReadOnlyList<RegisterInfo?> RegisterInfo { get; }
    IReadOnlyList<RegisterTypeInfo> RegisterTypes { get; }

    Task StopAsync(CancellationToken cancellationToken = default);
    Task StepAsync(CancellationToken cancellationToken = default);
    Task ContinueAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task<bool> BreakpointAsync(bool set, uint address, int kind, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadRegisterAsync(int index, CancellationToken cancellationToken = default);
    Task WriteRegisterAsync(int index, byte[] value, CancellationToken cancellationToken = default);
    Task<byte[]?[]> ReadAllRegistersAsync(CancellationToken cancellationToken = default);

    Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default);
    Task WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default);
}
