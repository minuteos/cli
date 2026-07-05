using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// The ST-Link debug access port: the USB command protocol (mode switch,
/// memory/register access, and run control via the DHCSR) - a port of the
/// extension's <c>dap/stlink.ts</c>.
///
/// EXPERIMENTAL: ported from an incomplete work-in-progress branch and not yet
/// verified against hardware; response sizes and the register ordering in
/// particular are best-effort.
/// </summary>
public sealed class StlinkDap : IDebugAccessPort
{
    private const int CommandLength = 16;
    private const int MaxDataLength = 6144;
    private const byte ReplyOk = 0x80;

    private const uint DhcsrAddr = 0xE000EDF0;
    private const uint DhcsrKey = 0xA05F0000;
    private const uint DhcsrDebugEn = 1;
    private const uint DhcsrHalt = 2;
    private const uint DhcsrStep = 4;

    private readonly StlinkUsb _usb;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal StlinkDap(StlinkUsb usb, ILogger logger)
    {
        _usb = usb;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var mode = await CurrentModeAsync(cancellationToken);
        _logger.LogDebug("ST-Link mode: {Mode}", mode);
        await SwitchToDebugAsync(cancellationToken);
    }

    private async Task SwitchToDebugAsync(CancellationToken cancellationToken)
    {
        var current = await CurrentModeAsync(cancellationToken);
        if (current == 2) // Debug
            return;

        // Exit the current mode (Debug=2/Swim=3/Dfu=0 have explicit exits).
        var exit = current switch
        {
            2 => new byte[] { 0xF2, 0x21 },
            3 => [0xF4, 0x01],
            0 => [0xF3, 0x07],
            _ => null,
        };
        if (exit != null)
            await ExecAsync(exit, respLength: 2, cancellationToken: cancellationToken);

        current = await CurrentModeAsync(cancellationToken);
        if (current != 2)
        {
            await ExecAsync([0xF2, 0x30, 0xa3], respLength: 2, checkError: true, cancellationToken: cancellationToken); // Enter SWD, no reset
            current = await CurrentModeAsync(cancellationToken);
        }

        if (current != 2)
            throw new InvalidOperationException($"ST-Link failed to enter debug mode (stayed in mode {current})");
    }

    private async Task<int> CurrentModeAsync(CancellationToken cancellationToken)
    {
        // The ST-Link sometimes ignores GetCurrentMode; retry with a short timeout.
        for (var i = 0; i < 5; i++)
        {
            var res = await ExecAsync([0xF5], respLength: 2, timeout: 100, cancellationToken: cancellationToken);
            if (res != null)
                return res[0];
        }
        var final = await ExecAsync([0xF5], respLength: 2, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Failed to get the current mode from the ST-Link");
        return final[0];
    }

    public async Task<byte[]> ReadCpuIdAsync(CancellationToken cancellationToken = default)
        => await ExecAsync([0xF2, 0x31], respLength: 8, checkError: true, cancellationToken: cancellationToken) ?? [];

    public Task<byte[]?> ReadRegisterAsync(int index, CancellationToken cancellationToken = default)
        => ExecAsync([0xF2, 0x33, (byte)index], respLength: 8, checkError: true, cancellationToken: cancellationToken);

    public Task WriteRegisterAsync(int index, byte[] value, CancellationToken cancellationToken = default)
        => ExecAsync(cmd =>
        {
            cmd[0] = 0xF2;
            cmd[1] = 0x34;
            cmd[2] = (byte)index;
            value.AsSpan(0, Math.Min(4, value.Length)).CopyTo(cmd[3..]);
        }, respLength: 2, checkError: true, cancellationToken: cancellationToken);

    public async Task<byte[]?[]> ReadCoreRegistersAsync(CancellationToken cancellationToken = default)
    {
        var res = await ExecAsync([0xF2, 0x3A], respLength: 88, checkError: true, cancellationToken: cancellationToken) ?? [];
        // Split the block into 4-byte registers (r0..r15, xpsr, msp, psp, cfbp).
        var registers = new byte[]?[res.Length / 4];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = res[(i * 4)..(i * 4 + 4)];
        return registers;
    }

    public async Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default)
    {
        var start = address - (address & 3);
        var end = address + (uint)length + 3;
        end -= end & 3;
        var chunks = new List<byte>((int)(end - start));

        while (start < end)
        {
            var len = (int)Math.Min(end - start, MaxDataLength);
            var block = await ExecAsync(cmd =>
            {
                cmd[0] = 0xF2;
                cmd[1] = 0x07;
                BinaryPrimitives.WriteUInt32LittleEndian(cmd[2..], start);
                BinaryPrimitives.WriteUInt16LittleEndian(cmd[6..], (ushort)len);
            }, respLength: len, cancellationToken: cancellationToken);
            // A timeout (null) or short read must surface, not silently advance
            // start and hand gdb truncated/misaligned memory.
            if (block == null || block.Length < len)
                throw new IOException(
                    $"ST-Link memory read at 0x{start:x8} returned {block?.Length ?? 0}/{len} bytes");
            chunks.AddRange(block);
            start += (uint)len;
        }

        var offset = (int)(address & 3);
        return chunks.GetRange(offset, Math.Min(length, chunks.Count - offset)).ToArray();
    }

    public async Task WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default)
    {
        var span = data.AsMemory();

        if ((address & 3) != 0)
        {
            var lead = (int)Math.Min(4 - (address & 3), (uint)span.Length);
            await WriteBlockAsync(0x0D, address, span[..lead], cancellationToken);
            address += (uint)lead;
            span = span[lead..];
        }

        while (span.Length >= 4)
        {
            var len = Math.Min(span.Length - (span.Length & 3), MaxDataLength);
            await WriteBlockAsync(0x08, address, span[..len], cancellationToken);
            address += (uint)len;
            span = span[len..];
        }

        if (span.Length > 0)
            await WriteBlockAsync(0x0D, address, span, cancellationToken);
    }

    private Task WriteBlockAsync(byte command, uint address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => ExecAsync(cmd =>
        {
            cmd[0] = 0xF2;
            cmd[1] = command;
            BinaryPrimitives.WriteUInt32LittleEndian(cmd[2..], address);
            BinaryPrimitives.WriteUInt16LittleEndian(cmd[6..], (ushort)data.Length);
        }, respLength: 0, writeData: data.ToArray(), cancellationToken: cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => WriteDebugAsync(DhcsrAddr, DhcsrKey | DhcsrHalt | DhcsrDebugEn, cancellationToken);

    public Task StepAsync(CancellationToken cancellationToken = default)
        => WriteDebugAsync(DhcsrAddr, DhcsrKey | DhcsrStep | DhcsrDebugEn, cancellationToken);

    public Task ContinueAsync(CancellationToken cancellationToken = default)
        => WriteDebugAsync(DhcsrAddr, DhcsrKey | DhcsrDebugEn, cancellationToken);

    private Task WriteDebugAsync(uint address, uint value, CancellationToken cancellationToken)
        => ExecAsync(cmd =>
        {
            cmd[0] = 0xF2;
            cmd[1] = 0x35;
            BinaryPrimitives.WriteUInt32LittleEndian(cmd[2..], address);
            BinaryPrimitives.WriteUInt32LittleEndian(cmd[6..], value);
        }, respLength: 2, checkError: true, cancellationToken: cancellationToken);

    private Task<byte[]?> ExecAsync(byte[] command, int respLength, bool checkError = false,
        byte[]? writeData = null, int timeout = 1000, CancellationToken cancellationToken = default)
        => ExecAsync(cmd => command.CopyTo(cmd), respLength, checkError, writeData, timeout, cancellationToken);

    private async Task<byte[]?> ExecAsync(Action<byte[]> build, int respLength, bool checkError = false,
        byte[]? writeData = null, int timeout = 1000, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cmd = new byte[CommandLength];
            build(cmd);
            _usb.Write(cmd);

            if (writeData != null)
            {
                _usb.Write(writeData);
                return null;
            }

            var res = _usb.Read(respLength, timeout);
            if (res == null)
                return null;

            if (checkError)
            {
                if (res.Length > 0 && res[0] != ReplyOk)
                    throw new InvalidOperationException($"ST-Link command 0x{cmd[1]:x2} returned error 0x{res[0]:x2}");
                return res.Length >= 4 ? res[4..] : [];
            }
            return res;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _usb.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
