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

    [Option("--slim", Description = "Emit minute-debug entries as `config` references resolved at debug time via `minuteos info --json` (requires extension support)")]
    public bool Slim { get; set; }

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
                };
                if (Slim)
                {
                    // Resolved at debug time from `minuteos info -c <name> --json`
                    // (never goes stale; requires extension support).
                    entry["config"] = config.Name;
                }
                else
                {
                    entry["cwd"] = "${workspaceRoot}";
                    entry["program"] = program;
                    entry["server"] = MinuteDebugConfig.Server(config.Settings)!;
                    if (MinuteDebugConfig.Smu(config.Settings) is { } smu)
                        entry["smu"] = smu;
                    if (config.Settings.Scalar("debug.svd") is { } svd)
                        entry["svd"] = svd;
                    if (config.Settings.Scalar("debug.smart-load") is "false" or "off")
                        entry["smartLoad"] = false;
                }
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
