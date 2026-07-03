using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Debug.Servers.Stlink;

namespace MinuteOS.Debug.Tests;

/// <summary>
/// The internal GDB-RSP server driven over a real socket by a fake target -
/// validates packet framing, checksums, and the command dispatch (attach,
/// register/memory read+write) end to end, without any hardware.
/// </summary>
public class StlinkRspTests : IAsyncLifetime
{
    private readonly FakeTarget _target = new();
    private TestGdbServer _server = null!;
    private TcpClient _client = null!;
    private NetworkStream _stream = null!;

    public async Task InitializeAsync()
    {
        _server = new TestGdbServer(_target);
        await _server.StartAsync();
        var (host, port) = (_server.Address.Split(':')[0], int.Parse(_server.Address.Split(':')[1]));
        _client = new TcpClient();
        await _client.ConnectAsync(host, port);
        _stream = _client.GetStream();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Fact]
    public async Task AttachReadWrite_RoundTripsOverTheProtocol()
    {
        Assert.Contains("PacketSize", await Command("qSupported:multiprocess+"));

        // Attach -> the target halts and reports the interrupt signal.
        Assert.Equal("S02", await Command("vAttach;1"));

        // 'g' returns every register as little-endian hex; the fake sets reg i = i.
        var regs = await Command("g");
        Assert.Equal("00000000" + "01000000" + "02000000", regs);

        // Memory read returns bytes in address order (0x12, 0x34, 0x56, 0x78).
        Assert.Equal("12345678", await Command("m20000000,4"));

        // Memory write then read-back.
        Assert.Equal("OK", await Command("M20000000,4:aabbccdd"));
        Assert.Equal("aabbccdd", await Command("m20000000,4"));
    }

    /// <summary>Sends one RSP packet and returns the decoded reply payload.</summary>
    private async Task<string> Command(string payload)
    {
        var checksum = 0;
        foreach (var c in payload)
            checksum = (checksum + (byte)c) & 0xFF;
        var packet = Encoding.ASCII.GetBytes($"${payload}#{checksum:x2}");
        await _stream.WriteAsync(packet);
        return await ReadReplyAsync();
    }

    private async Task<string> ReadReplyAsync()
    {
        var buffer = new byte[4096];
        var payload = new StringBuilder();
        var state = 0; // 0 = waiting for '$', 1 = in payload, 2 = checksum
        var trailer = 0;
        while (true)
        {
            var n = await _stream.ReadAsync(buffer);
            if (n == 0)
                throw new EndOfStreamException();
            for (var i = 0; i < n; i++)
            {
                var b = (char)buffer[i];
                switch (state)
                {
                    case 0 when b == '$':
                        state = 1;
                        break;
                    case 1 when b == '#':
                        state = 2;
                        trailer = 2;
                        break;
                    case 1:
                        payload.Append(b);
                        break;
                    case 2:
                        if (--trailer == 0)
                            return payload.ToString();
                        break;
                }
            }
        }
    }

    private sealed class TestGdbServer(IDebugTarget target) : InternalGdbServer(NullLogger.Instance)
    {
        public Task StartAsync() => StartListenerAsync();
        public Task StopAsync() => StopListenerAsync();
        protected override Task<IDebugTarget> GetTargetAsync(int pid, CancellationToken cancellationToken)
            => Task.FromResult(target);
    }

    private sealed class FakeTarget : IDebugTarget
    {
        private readonly Dictionary<uint, byte> _memory = new()
        {
            [0x20000000] = 0x12, [0x20000001] = 0x34, [0x20000002] = 0x56, [0x20000003] = 0x78,
        };

        public DebugTargetInfo Info { get; } = new("armv7e-m", "Fake");
        public IReadOnlyList<DebugThread> Threads { get; } = [new DebugThread { Id = 1 }];
        public IReadOnlyList<RegisterInfo?> RegisterInfo { get; } =
        [
            new RegisterInfo("f", "general", "r0", 32),
            new RegisterInfo("f", "general", "r1", 32),
            new RegisterInfo("f", "general", "r2", 32),
        ];
        public IReadOnlyList<RegisterTypeInfo> RegisterTypes { get; } = [];

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Threads[0].StopReason = StopSignal.Interrupt;
            return Task.CompletedTask;
        }

        public Task StepAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ContinueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> BreakpointAsync(bool set, uint address, int kind, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<byte[]?> ReadRegisterAsync(int index, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>([(byte)index, 0, 0, 0]);

        public Task WriteRegisterAsync(int index, byte[] value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<byte[]?[]> ReadAllRegistersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?[]>([[0, 0, 0, 0], [1, 0, 0, 0], [2, 0, 0, 0]]);

        public Task<byte[]> ReadMemoryAsync(uint address, int length, CancellationToken cancellationToken = default)
        {
            var data = new byte[length];
            for (var i = 0; i < length; i++)
                data[i] = _memory.GetValueOrDefault(address + (uint)i);
            return Task.FromResult(data);
        }

        public Task WriteMemoryAsync(uint address, byte[] data, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < data.Length; i++)
                _memory[address + (uint)i] = data[i];
            return Task.CompletedTask;
        }
    }
}
