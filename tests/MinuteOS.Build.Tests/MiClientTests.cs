using Microsoft.Extensions.Logging.Abstractions;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Build.Tests;

/// <summary>
/// Integration tests driving a real host gdb in MI mode (skipped when no gdb
/// is installed). The E2E DAP flow against qemu is exercised separately.
/// </summary>
public class MiClientTests
{
    private static string? FindGdb()
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "gdb"))
            .FirstOrDefault(File.Exists);

    [Fact]
    public async Task Execute_RunsCommands_AndCollectsConsoleOutput()
    {
        if (FindGdb() is not { } gdb)
            return; // no gdb on this machine

        await using var mi = new MiClient(NullLogger.Instance);
        await mi.StartAsync(gdb, "/bin/true", cwd: null);

        var version = await mi.ExecuteAsync("gdb-version");
        Assert.Equal("done", version.Class);
        Assert.Contains("GNU gdb", version.Console);

        // list-features returns a result table
        var features = await mi.ExecuteAsync("list-features");
        Assert.Equal("done", features.Class);
        Assert.NotNull(features.Results["features"]);

        // an unknown command surfaces as MiException
        var error = await Assert.ThrowsAsync<MiException>(() => mi.ExecuteAsync("no-such-command"));
        Assert.Equal("error", error.Result.Class);
    }

    [Fact]
    public async Task BreakInsert_ReportsBreakpointDetails()
    {
        if (FindGdb() is not { } gdb)
            return;

        await using var mi = new MiClient(NullLogger.Instance);
        await mi.StartAsync(gdb, "/bin/true", cwd: null);

        // A symbol-less binary still accepts a pending address breakpoint.
        var res = await mi.ExecuteAsync("break-insert -f *0x1000");
        Assert.Equal("done", res.Class);
        var bkpt = res.Results["bkpt"] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(bkpt);
        Assert.True(MiClient.ParseNumber(bkpt["number"]) > 0);

        await mi.ExecuteAsync($"break-delete {MiClient.ParseNumber(bkpt["number"])}");
    }
}
