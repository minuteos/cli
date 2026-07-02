using System.Text.Json.Nodes;

namespace MinuteOS.Build;

/// <summary>
/// Builds the minute-debug launch model (the extension's
/// <c>InputLaunchConfiguration</c> shape) from a configuration's settings bag.
/// The single source of truth for debug configuration: consumed by
/// <c>minuteos info --json</c> (queried by the extension at debug time) and by
/// <c>minuteos vscode</c> (generated launch entries). See
/// docs/design/debugger-integration.md.
/// </summary>
public static class MinuteDebugConfig
{
    /// <summary>
    /// The full resolved debug model for a configuration. <c>program</c> is
    /// project-relative (launch configs and the extension resolve against cwd).
    /// </summary>
    public static JsonObject Describe(BuildConfiguration config)
    {
        var model = new JsonObject
        {
            ["name"] = config.Name,
            ["program"] = Path.GetRelativePath(config.ProjectRoot, config.PrimaryOutput).Replace('\\', '/'),
            ["cwd"] = config.ProjectRoot,
            ["gdb"] = (config.Settings.Scalar("gcc.toolchain-prefix") ?? "") + "gdb",
        };

        if (Server(config.Settings) is { } server)
            model["server"] = server;
        if (Smu(config.Settings) is { } smu)
            model["smu"] = smu;
        if (config.Settings.Scalar("debug.svd") is { } svd)
            model["svd"] = svd;
        if (config.Settings.Scalar("debug.smart-load") is "false" or "off")
            model["smartLoad"] = false;

        var settings = new JsonObject();
        foreach (var (key, values) in config.Settings.All.OrderBy(kv => kv.Key))
            settings[key] = values.Count == 1 ? values[0] : new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        model["settings"] = settings;

        return model;
    }

    /// <summary>
    /// The `server` value: the preset name alone when nothing is customized,
    /// else an inline configuration object. Null when <c>debug.server</c> unset.
    /// </summary>
    public static JsonNode? Server(Settings s)
    {
        var server = s.Scalar("debug.server");
        if (string.IsNullOrEmpty(server))
            return null;

        switch (server.ToLowerInvariant())
        {
            case "bmp":
                var bmp = new JsonObject { ["type"] = "bmp" };
                if (s.Scalar("bmp.port") is { } port) bmp["port"] = port;
                if (IsOn(s.Scalar("bmp.power"))) bmp["power"] = true;
                return bmp.Count > 1 ? bmp : "bmp";
            case "qemu":
                var qemu = new JsonObject { ["type"] = "qemu" };
                if (s.Scalar("qemu.machine") is { } machine) qemu["machine"] = machine;
                if (s.Scalar("qemu.cpu") is { } cpu) qemu["cpu"] = cpu;
                return qemu.Count > 1 ? qemu : "qemu";
            case "renode":
                var renode = new JsonObject { ["type"] = "renode" };
                if (s.Scalar("renode.script") is { } script) renode["script"] = script;
                if (s.Scalar("renode.machine") is { } rmachine) renode["machine"] = rmachine;
                return renode.Count > 1 ? renode : "renode";
            default:
                return server; // a user-defined preset name
        }
    }

    /// <summary>The `smu` value from smu.* settings (null when unset).</summary>
    public static JsonNode? Smu(Settings s)
    {
        if (s.Scalar("smu.type") is not { } type)
            return null;

        var smu = new JsonObject { ["type"] = type };
        if (s.Scalar("smu.port") is { } port) smu["port"] = port;
        if (s.Scalar("smu.output") is { } output) smu["output"] = output;
        if (double.TryParse(s.Scalar("smu.voltage"), System.Globalization.CultureInfo.InvariantCulture, out var v))
            smu["voltage"] = v;
        if (IsOn(s.Scalar("smu.start-power-on"))) smu["startPowerOn"] = true;
        if (IsOn(s.Scalar("smu.stop-power-off"))) smu["stopPowerOff"] = true;
        return smu.Count > 1 ? smu : type;
    }

    private static bool IsOn(string? value) => value is "true" or "on" or "1";
}
