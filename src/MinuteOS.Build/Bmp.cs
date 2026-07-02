namespace MinuteOS.Build;

/// <summary>
/// Black Magic Probe support (the primary probe of the minute-debug extension).
/// The BMP *is* the GDB server: it exposes a serial port that gdb connects to
/// with <c>target extended-remote</c>; flashing goes through gdb itself
/// (<c>monitor swdp_scan</c>, <c>attach 1</c>, <c>load</c>).
/// </summary>
public static class Bmp
{
    /// <summary>
    /// Finds the BMP's GDB-server serial port. Prefers stable /dev/serial/by-id
    /// links (the GDB interface is USB interface 00); falls back to an explicit
    /// setting. Mirrors the extension's VID/PID (1d50:6018) autodetection to the
    /// extent possible without a USB stack.
    /// </summary>
    public static string? FindPort()
    {
        const string byId = "/dev/serial/by-id";
        if (!Directory.Exists(byId))
            return null;

        return Directory.EnumerateFiles(byId)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return name.Contains("Black_Magic", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("if00", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p => ResolveLink(p))
            .FirstOrDefault();
    }

    private static string ResolveLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget != null
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, info.LinkTarget))
            : path;
    }

    /// <summary>
    /// GDB batch arguments for a one-shot operation against the BMP. The
    /// per-operation payload commands run after scan+attach; gdb's batch exit
    /// detaches, which lets the target run.
    /// </summary>
    public static List<string> GdbBatchArgs(string port, bool power, IEnumerable<string> payload, string? image)
    {
        var args = new List<string> { "-nx", "--batch", "-ex", $"target extended-remote {port}" };
        if (power)
            args.AddRange(["-ex", "monitor tpwr enable"]);
        args.AddRange(["-ex", "monitor swdp_scan", "-ex", "attach 1"]);
        foreach (var command in payload)
            args.AddRange(["-ex", command]);
        if (image != null)
            args.Add(image);
        return args;
    }

    /// <summary>Interactive attach arguments (for <c>minuteos debug</c> - no server process).</summary>
    public static List<string> GdbAttachArgs(string port, bool power, string image)
    {
        var args = new List<string> { image, "-ex", $"target extended-remote {port}" };
        if (power)
            args.AddRange(["-ex", "monitor tpwr enable"]);
        args.AddRange(["-ex", "monitor swdp_scan", "-ex", "attach 1"]);
        return args;
    }
}
