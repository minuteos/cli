using System.Buffers.Binary;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// A Cortex-M debug target over an <see cref="IDebugAccessPort"/>: run control,
/// a cached register file, memory, and FPB hardware breakpoints - the port of
/// the extension's <c>target/cortex.ts</c>. EXPERIMENTAL / unverified (see
/// <see cref="StlinkDap"/>).
/// </summary>
public sealed class CortexTarget : IDebugTarget
{
    private const uint FpComp0 = 0xE0002008;

    private readonly IDebugAccessPort _dap;
    private byte[]?[] _registerCache = [];

    public DebugTargetInfo Info { get; }
    public IReadOnlyList<DebugThread> Threads { get; } =
        [new DebugThread { Id = 1, ExtraInfo = "Core Thread" }];

    public IReadOnlyList<RegisterInfo?> RegisterInfo => CortexRegisters.All;
    public IReadOnlyList<RegisterTypeInfo> RegisterTypes => CortexRegisters.Types;

    public CortexTarget(IDebugAccessPort dap, DebugTargetInfo info)
    {
        _dap = dap;
        Info = info;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _registerCache = [];
        await _dap.StopAsync(cancellationToken);
        Threads[0].StopReason = StopSignal.Interrupt;
    }

    public async Task StepAsync(CancellationToken cancellationToken = default)
    {
        _registerCache = [];
        await _dap.StepAsync(cancellationToken);
        Threads[0].StopReason = StopSignal.Trap;
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        _registerCache = [];
        await _dap.ContinueAsync(cancellationToken);
        Threads[0].StopReason = null;
    }

    public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask; // TODO

    public async Task<bool> BreakpointAsync(bool set, uint address, int kind, CancellationToken cancellationToken = default)
    {
        var comparators = Info.FpbComparators;
        if (comparators == 0)
        {
            if (set)
                throw new InvalidOperationException("No FPB comparators available for a hardware breakpoint");
            return false;
        }

        var fpb = await _dap.ReadMemoryAsync(FpComp0, comparators * 4, cancellationToken);
        var baseAddr = address & 0x3FFFFFFC;
        var halfWord = ((address & 2) != 0 ? 2u : 1u) << 30;
        int? match = null, empty = null;
        var matchCmp = 0u;

        for (var i = 0; i < fpb.Length; i += 4)
        {
            var cmp = BinaryPrimitives.ReadUInt32LittleEndian(fpb.AsSpan(i));
            if ((cmp & 1) != 0)
            {
                empty ??= i;
                BinaryPrimitives.WriteUInt32LittleEndian(fpb.AsSpan(i), 0);
            }
            else if ((cmp & 0x3FFFFFFC) == baseAddr)
            {
                match = i;
                matchCmp = cmp;
                break;
            }
        }

        int wr;
        if (set)
        {
            wr = match ?? empty ?? throw new InvalidOperationException($"All {comparators} FPB comparators are in use");
            BinaryPrimitives.WriteUInt32LittleEndian(fpb.AsSpan(wr), matchCmp | baseAddr | halfWord | 1);
        }
        else
        {
            if (match is not { } m || (matchCmp & halfWord) == 0)
                return false;
            wr = m;
            matchCmp &= ~halfWord;
            BinaryPrimitives.WriteUInt32LittleEndian(fpb.AsSpan(wr), (matchCmp >> 30) != 0 ? matchCmp : 0);
        }

        await _dap.WriteMemoryAsync(FpComp0 + (uint)wr, fpb[wr..(wr + 4)], cancellationToken);
        return true;
    }

    public async Task<byte[]?> ReadRegisterAsync(int index, CancellationToken cancellationToken = default)
        => (await RequireRegistersAsync(index, cancellationToken))[index];

    public Task WriteRegisterAsync(int index, byte[] value, CancellationToken cancellationToken = default)
        => _dap.WriteRegisterAsync(index, value, cancellationToken);

    public Task<byte[]?[]> ReadAllRegistersAsync(CancellationToken cancellationToken = default)
        => RequireRegistersAsync(null, cancellationToken);

    public Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default)
        => _dap.ReadMemoryAsync(address, length, cancellationToken);

    public Task WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default)
        => _dap.WriteMemoryAsync(address, data, cancellationToken);

    private async Task<byte[]?[]> RequireRegistersAsync(int? single, CancellationToken cancellationToken)
    {
        if (_registerCache.Length != CortexRegisters.All.Count)
            _registerCache = new byte[]?[CortexRegisters.All.Count];

        if (single is { } one && _registerCache[one] != null)
            return _registerCache;

        // Load the core register file in one shot on the first miss below its range.
        if ((single is null || single < CortexRegisters.CoreCount) && _registerCache[0] == null)
        {
            var core = await _dap.ReadCoreRegistersAsync(cancellationToken);
            for (var w = 0; w < core.Length; w++)
            {
                var gdb = w <= 18 ? w : 20; // ST-Link's cfbp word maps to the 'spr' register (index 0x14)
                if (gdb < _registerCache.Length)
                    _registerCache[gdb] = core[w];
            }
        }

        if (single is { } idx && idx >= CortexRegisters.CoreCount)
        {
            _registerCache[idx] ??= await _dap.ReadRegisterAsync(idx, cancellationToken);
        }
        else if (single is null)
        {
            for (var i = 0; i < _registerCache.Length; i++)
                if (_registerCache[i] == null && CortexRegisters.All[i] != null)
                    _registerCache[i] = await _dap.ReadRegisterAsync(i, cancellationToken);
        }

        return _registerCache;
    }
}

/// <summary>
/// Probes a target over the DAP (CPUID + DBGMCU IDCODE) and builds a
/// <see cref="CortexTarget"/>. The port of <c>dap/probe.ts</c>; the device
/// table is intentionally tiny (WIP).
/// </summary>
public static class CortexProbe
{
    private const uint FpCtrl = 0xE0002000;

    private static readonly (string Pattern, string Device)[] IdcodeMap =
    [
        ("415", "STM32L47x"),
        ("461", "STM32L49x"),
    ];

    public static async Task<IDebugTarget> ProbeAsync(IDebugAccessPort dap, CancellationToken cancellationToken = default)
    {
        _ = await dap.ReadCpuIdAsync(cancellationToken);
        var idcode = (BinaryPrimitives.ReadUInt32LittleEndian(await dap.ReadMemoryAsync(0xE0042000, 4, cancellationToken)))
            .ToString("x8");
        var uid = Convert.ToHexString(await dap.ReadMemoryAsync(0x1FFF7590, 12, cancellationToken)).ToLowerInvariant();
        var fpCtrl = BinaryPrimitives.ReadUInt32LittleEndian(await dap.ReadMemoryAsync(FpCtrl, 4, cancellationToken));
        var fpbComparators = (int)(((fpCtrl >> 4) & 0xF) | ((fpCtrl >> 8) & 0x30));

        var device = IdcodeMap.FirstOrDefault(m => idcode.Contains(m.Pattern)).Device
            ?? throw new InvalidOperationException($"Unknown ID code: {idcode}");

        return new CortexTarget(dap, new DebugTargetInfo(
            // GDB doesn't understand armv7-m for CM3/4; armv7e-m works.
            Architecture: "armv7e-m", Device: device, Uid: uid, FpbComparators: fpbComparators));
    }
}
