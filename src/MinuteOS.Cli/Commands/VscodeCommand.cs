using System.Text.Json;
using System.Text.Json.Nodes;
using MinuteOS.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

/// <summary>
/// Generates the VS Code integration files (the port of the Make-era
/// <c>VSCode.mk</c>): Cortex-Debug launch configurations (servertype jlink,
/// SWO console) for every configuration with a <c>jlink.device</c> setting,
/// minuteos build tasks, and IntelliSense wired to the generated
/// <c>compile_commands.json</c>.
/// </summary>
[Command("vscode", Description = "Generate .vscode/ integration (cortex-debug launch configs, build tasks, IntelliSense)")]
public class VscodeCommand : LoggingCommand
{
    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    [Option("--force", "-f", Description = "Overwrite existing launch.json/tasks.json")]
    public bool Force { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectConfig.GetProjectRoot(ProjectDir);

        ProjectConfig projectConfig;
        try
        {
            projectConfig = ProjectConfig.Load(projectRoot);
        }
        catch (Exception ex)
        {
            Logger.LogError("{Message}", ex.Message);
            return Task.FromResult(1);
        }

        var configs = new List<BuildConfiguration>();
        foreach (var name in projectConfig.ConfigurationNames)
        {
            try { configs.Add(BuildConfiguration.Create(projectConfig, name, projectRoot)); }
            catch (Exception ex) { Logger.LogWarning("Skipping '{Name}': {Message}", name, ex.Message); }
        }
        if (configs.Count == 0)
        {
            Logger.LogError("No resolvable configurations.");
            return Task.FromResult(1);
        }

        var vscodeDir = Path.Combine(projectRoot, ".vscode");
        Directory.CreateDirectory(vscodeDir);

        WriteAlways(Path.Combine(vscodeDir, "c_cpp_properties.json"), CCppProperties(configs[0]));
        WriteIfMissing(Path.Combine(vscodeDir, "tasks.json"), Tasks(configs));
        WriteIfMissing(Path.Combine(vscodeDir, "launch.json"), Launch(projectRoot, configs));

        return Task.FromResult(0);
    }

    /// <summary>IntelliSense from the build's own compilation database.</summary>
    private static JsonObject CCppProperties(BuildConfiguration config) => new()
    {
        ["configurations"] = new JsonArray(new JsonObject
        {
            ["name"] = "minuteos",
            ["compileCommands"] = $"${{workspaceFolder}}/out/{config.Name}/compile_commands.json",
            ["cStandard"] = "gnu11",
            ["cppStandard"] = "gnu++17",
        }),
        ["version"] = 4,
    };

    private static JsonObject Tasks(IReadOnlyList<BuildConfiguration> configs) => new()
    {
        ["version"] = "2.0.0",
        ["tasks"] = new JsonArray(configs.Select((c, i) => (JsonNode)new JsonObject
        {
            ["label"] = $"minuteos: build {c.Name}",
            ["type"] = "shell",
            ["command"] = $"minuteos build -c {c.Name}",
            ["group"] = i == 0
                ? new JsonObject { ["kind"] = "build", ["isDefault"] = true }
                : "build",
            ["presentation"] = new JsonObject { ["clear"] = true },
        }).ToArray()),
    };

    /// <summary>
    /// Debug launch entries: <c>minute-debug</c> (the minuteos debug adapter) for
    /// configurations with <c>debug.server</c>, and legacy Cortex-Debug/J-Link
    /// entries for configurations with <c>jlink.device</c>.
    /// </summary>
    private JsonObject Launch(string projectRoot, IReadOnlyList<BuildConfiguration> configs)
    {
        var entries = new JsonArray();

        foreach (var config in configs)
        {
            var server = config.Settings.Scalar("debug.server");
            if (string.IsNullOrEmpty(server))
                continue;

            var program = Path.GetRelativePath(projectRoot, config.PrimaryOutput).Replace('\\', '/');
            foreach (var request in (string[])["launch", "attach"])
            {
                var entry = new JsonObject
                {
                    ["name"] = $"{(request == "launch" ? "Launch" : "Attach")} {config.Name}",
                    ["type"] = "minute-debug",
                    ["request"] = request,
                    ["cwd"] = "${workspaceRoot}",
                    ["program"] = program,
                    ["server"] = ServerConfig(config, server),
                };
                if (SmuConfig(config) is { } smu)
                    entry["smu"] = smu;
                if (config.Settings.Scalar("debug.svd") is { } svd)
                    entry["svd"] = svd;
                if (config.Settings.Scalar("debug.smart-load") is "false" or "off")
                    entry["smartLoad"] = false;
                if (request == "launch")
                    entry["preLaunchTask"] = $"minuteos: build {config.Name}";
                entries.Add(entry);
            }
        }

        foreach (var config in configs)
        {
            var device = config.Settings.Scalar("jlink.device");
            if (string.IsNullOrEmpty(device))
                continue;

            var swoFrequency = int.TryParse(config.Settings.Scalar("jlink.swo-frequency"), out var f) ? f : 1_000_000;
            var executable = "${workspaceRoot}/" +
                Path.GetRelativePath(projectRoot, config.PrimaryOutput).Replace('\\', '/');

            foreach (var request in (string[])["launch", "attach"])
            {
                var entry = new JsonObject
                {
                    ["name"] = $"{(request == "launch" ? "Launch" : "Attach")} {config.Name}",
                    ["cwd"] = "${workspaceRoot}",
                    ["executable"] = executable,
                    ["request"] = request,
                    ["type"] = "cortex-debug",
                    ["servertype"] = "jlink",
                    ["device"] = device,
                    ["postStartSessionCommands"] = new JsonArray($"monitor SWO Start 0 {swoFrequency}"),
                    ["swoConfig"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["swoFrequency"] = swoFrequency,
                        ["decoders"] = new JsonArray(new JsonObject
                        {
                            ["port"] = 0,
                            ["type"] = "console",
                            ["label"] = "SWV",
                            ["showOnStartup"] = true,
                        }),
                    },
                };
                if (request == "launch")
                    entry["preLaunchTask"] = $"minuteos: build {config.Name}";
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
            Logger.LogWarning(
                "No configuration sets 'debug.server' (minute-debug) or 'jlink.device' (Cortex-Debug); " +
                "launch.json will have no debug entries.");

        return new JsonObject { ["version"] = "0.2.0", ["configurations"] = entries };
    }

    /// <summary>
    /// The minute-debug `server` value: the preset name alone when nothing is
    /// customized, else an inline configuration object.
    /// </summary>
    private static JsonNode ServerConfig(BuildConfiguration config, string server)
    {
        var s = config.Settings;
        switch (server.ToLowerInvariant())
        {
            case "bmp":
                var bmp = new JsonObject { ["type"] = "bmp" };
                if (s.Scalar("bmp.port") is { } port) bmp["port"] = port;
                if (s.Scalar("bmp.power") is "true" or "on" or "1") bmp["power"] = true;
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

    /// <summary>The minute-debug `smu` value from smu.* settings (null when unset).</summary>
    private static JsonNode? SmuConfig(BuildConfiguration config)
    {
        var s = config.Settings;
        if (s.Scalar("smu.type") is not { } type)
            return null;

        var smu = new JsonObject { ["type"] = type };
        if (s.Scalar("smu.port") is { } port) smu["port"] = port;
        if (s.Scalar("smu.output") is { } output) smu["output"] = output;
        if (double.TryParse(s.Scalar("smu.voltage"), System.Globalization.CultureInfo.InvariantCulture, out var v))
            smu["voltage"] = v;
        if (s.Scalar("smu.start-power-on") is "true" or "on" or "1") smu["startPowerOn"] = true;
        if (s.Scalar("smu.stop-power-off") is "true" or "on" or "1") smu["stopPowerOff"] = true;
        return smu.Count > 1 ? smu : type;
    }

    private void WriteAlways(string path, JsonObject content)
    {
        File.WriteAllText(path, content.ToJsonString(JsonOptions) + "\n");
        Logger.LogInformation("Wrote   {File}", path);
    }

    private void WriteIfMissing(string path, JsonObject content)
    {
        if (File.Exists(path) && !Force)
        {
            Logger.LogInformation("Kept    {File} (exists; use --force to overwrite)", path);
            return;
        }
        File.WriteAllText(path, content.ToJsonString(JsonOptions) + "\n");
        Logger.LogInformation("Wrote   {File}", path);
    }
}
