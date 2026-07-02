using Microsoft.Extensions.Logging;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug;

/// <summary>SWV trace encoding.</summary>
public enum SwvFormat
{
    Manchester = 1,
    Uart = 2,
}

/// <summary>Addresses of the base Cortex-M CoreSight peripherals (from the ROM table).</summary>
public sealed record CortexPeripherals(uint Scs, uint Dwt, uint Fpb, uint Itm, uint Tpiu, uint Etm, uint Cti, uint Mtb);

public sealed record CortexTraceOptions
{
    public required SwvFormat Format { get; init; }
    public required int CpuFrequency { get; init; }
    public required int SwvFrequency { get; init; }
    public bool PcSample { get; init; }
    public bool ExceptionOverhead { get; init; }
    public bool ExceptionTrace { get; init; }
    public int TraceBusId { get; init; } = 1;
}

/// <summary>
/// Cortex-M debug-peripheral access over GDB memory reads/writes - the port of
/// the extension's <c>Cortex</c>: ROM-table discovery, DEMCR vector catch, and
/// the DWT/ITM/TPIU trace setup that makes SWO emit.
/// </summary>
public sealed class Cortex(MiClient mi, ILogger logger)
{
    /// <summary>Fixed address of the table containing addresses of base Cortex peripherals.</summary>
    private const ulong RomTable = 0xE00FF000;

    private const int ScsDemcr = 0xDFC;
    private const uint ScsDemcrTrcena = 1u << 24;

    private const int DwtCtrl = 0;
    private const uint DwtCtrlExcevtena = 1u << 18;      // exception overhead counter
    private const uint DwtCtrlExctrcena = 1u << 16;      // interrupt entry/exit trace
    private const uint DwtCtrlPcsamplena = 1u << 12;     // PC sampling
    private const uint DwtCtrlSynctap16M = 1u << 10;
    private const uint DwtCtrlCyccntena = 1u << 0;

    private const int ItmTer = 0xE00;
    private const int ItmTcr = 0xE80;
    private const uint ItmTcrTxena = 1u << 3;
    private const uint ItmTcrSyncena = 1u << 2;
    private const uint ItmTcrItmena = 1u << 0;

    private const int ItmLar = 0xFB0;
    private const uint ItmLarUnlock = 0xC5ACCE55;

    private const int TpiuAcpr = 0x10;
    private const int TpiuSppr = 0xF0;
    private const int TpiuFfcr = 0x304;

    private CortexPeripherals? _peripherals;

    public async Task<CortexPeripherals> DetectPeripheralsAsync(CancellationToken cancellationToken = default)
    {
        if (_peripherals != null)
            return _peripherals;

        var mem = await mi.ReadMemoryAsync(RomTable, 32, cancellationToken);

        uint GetPeripheral(int offset)
        {
            var entry = BitConverter.ToUInt32(mem, offset);
            return (entry & 1) != 0 ? (uint)(RomTable + (entry & ~3u)) : 0;
        }

        var peripherals = new CortexPeripherals(
            Scs: GetPeripheral(0),
            Dwt: GetPeripheral(4),
            Fpb: GetPeripheral(8),
            Itm: GetPeripheral(12),
            Tpiu: GetPeripheral(16),
            Etm: GetPeripheral(20),
            Cti: GetPeripheral(24),
            Mtb: GetPeripheral(28));
        logger.LogDebug("ROM table peripherals: {Peripherals}", peripherals);
        return _peripherals = peripherals;
    }

    public async Task<uint> Read32Async(ulong address, CancellationToken cancellationToken = default)
        => BitConverter.ToUInt32(await mi.ReadMemoryAsync(address, 4, cancellationToken));

    public Task Write32Async(ulong address, uint value, CancellationToken cancellationToken = default)
        => mi.WriteMemoryAsync(address, BitConverter.GetBytes(value), cancellationToken);

    public async Task Modify32Async(ulong address, Func<uint, uint> modify, CancellationToken cancellationToken = default)
        => await Write32Async(address, modify(await Read32Async(address, cancellationToken)), cancellationToken);

    /// <summary>Configures DWT/ITM/TPIU so the target emits SWV trace.</summary>
    public async Task SetupTraceAsync(CortexTraceOptions options, CancellationToken cancellationToken = default)
    {
        var p = await DetectPeripheralsAsync(cancellationToken);

        // enable ITM access
        await Modify32Async(p.Scs + ScsDemcr, n => n | ScsDemcrTrcena, cancellationToken);
        await Write32Async(p.Itm + ItmLar, ItmLarUnlock, cancellationToken);

        // stop ITM and DWT
        await Write32Async(p.Itm + ItmTcr, 0, cancellationToken);
        await Write32Async(p.Dwt + DwtCtrl, 0, cancellationToken);

        // configure TPIU
        await Write32Async(p.Tpiu + TpiuFfcr, 0x100, cancellationToken);
        await Write32Async(p.Tpiu + TpiuSppr, (uint)options.Format, cancellationToken);
        if (options.SwvFrequency > 0 && options.CpuFrequency > 0)
            await Write32Async(p.Tpiu + TpiuAcpr, (uint)(options.CpuFrequency / options.SwvFrequency), cancellationToken);

        // configure DWT (sync tap + 1k cycle tap, count 16)
        uint CycTap1K(int count) => 1u << 9 | (uint)((count - 1) & 0xF) << 5 | (uint)((count - 1) & 0xF) << 1;
        await Write32Async(p.Dwt + DwtCtrl,
            (options.ExceptionOverhead ? DwtCtrlExcevtena : 0)
            | (options.ExceptionTrace ? DwtCtrlExctrcena : 0)
            | (options.PcSample ? DwtCtrlPcsamplena : 0)
            | DwtCtrlSynctap16M
            | CycTap1K(16)
            | DwtCtrlCyccntena, cancellationToken);

        // configure and enable ITM
        await Write32Async(p.Itm + ItmTcr,
            (uint)(options.TraceBusId & 0x7F) << 16
            | ItmTcrTxena
            | ItmTcrSyncena
            | ItmTcrItmena, cancellationToken);

        // enable all ITM stimuli
        await Write32Async(p.Itm + ItmTer, ~0u, cancellationToken);
    }

    /// <summary>
    /// Turns DWT PC sampling on/off (the profiler's sample source). The rest
    /// of DWT_CTRL (sync/cycle tap, CYCCNTENA) must already be configured -
    /// <see cref="SetupTraceAsync"/> does that when the SWO session starts.
    /// </summary>
    public async Task SetPcSamplingAsync(bool enable, CancellationToken cancellationToken = default)
    {
        var p = await DetectPeripheralsAsync(cancellationToken);
        await Modify32Async(p.Dwt + DwtCtrl,
            n => enable ? n | DwtCtrlPcsamplena : n & ~DwtCtrlPcsamplena, cancellationToken);
    }

    /// <summary>Sets the vector-catch mask in DEMCR (exception breakpoints).</summary>
    public async Task SetExceptionMaskAsync(int mask, CancellationToken cancellationToken = default)
    {
        var p = await DetectPeripheralsAsync(cancellationToken);
        if (p.Scs == 0)
            throw new NotSupportedException("No SCS entry in the Cortex-M ROM table");
        await Modify32Async(p.Scs + ScsDemcr, n => (n & ~0xFFFFu) | (uint)mask, cancellationToken);
    }
}
