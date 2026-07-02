using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MinuteOS.Build;
using MinuteOS.Debug.Mi;

namespace MinuteOS.Debug.Dap;

/// <summary>
/// The minuteos debug adapter session - a port of the minute-debug extension's
/// <c>MinuteDebugSession</c>, speaking DAP over a <see cref="DapConnection"/>
/// instead of in-process to VS Code. Launch arguments are either inline
/// (program/gdb/server) or a <c>config</c> name resolved through the build
/// system (the same model as <c>minuteos info --json</c>).
/// </summary>
public sealed class DebugSession(DapConnection connection, ILogger logger) : IAsyncDisposable
{
    // Fake variable reference numbers for the scopes (the extension's VariableScope).
    private const int ScopeRegisters = 0x10000000;
    private const int ScopeGlobal = 0x10000002;
    private const int ScopeLocal = 0x10000003;

    private Probe? _probe;
    private bool _launch;
    private bool _stopAtConnect;
    private bool _configured;
    private bool _terminated;

    private readonly Dictionary<int, JsonObject> _frameIdMap = [];
    private readonly Dictionary<object, GdbVar> _varMap = [];
    private int _varNextRef = 1;
    private readonly Dictionary<string, List<JsonObject>> _breakpointMap = [];
    private readonly List<JsonObject> _instructionBreakpoints = [];

    private readonly Dictionary<int, bool> _vsThreads = [];
    private int _interrupted;
    private int _suppressExecEvents;

    private Probe Probe => _probe ?? throw new InvalidOperationException("Not connected");
    private MiClient Gdb => Probe.Gdb;

    /// <summary>Runs the session until the client disconnects or the stream ends.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!_terminated && await connection.ReadAsync(cancellationToken) is { } message)
        {
            if (message["type"]?.GetValue<string>() != "request")
                continue;
            await HandleRequestAsync(message, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var command = request["command"]?.GetValue<string>() ?? "";
        var args = request["arguments"] as JsonObject ?? [];
        logger.LogDebug("dap< {Command}", command);
        try
        {
            var body = command switch
            {
                "initialize" => Initialize(),
                "launch" => await LaunchOrAttachAsync(args, loadProgram: true, cancellationToken),
                "attach" => await LaunchOrAttachAsync(args, loadProgram: false, cancellationToken),
                "configurationDone" => await ConfigurationDoneAsync(cancellationToken),
                "disconnect" => await DisconnectAsync(),
                "threads" => await ThreadsAsync(cancellationToken),
                "stackTrace" => await StackTraceAsync(args, cancellationToken),
                "scopes" => Scopes(args),
                "variables" => await VariablesAsync(args, cancellationToken),
                "evaluate" => await EvaluateAsync(args, cancellationToken),
                "setBreakpoints" => await SetBreakpointsAsync(args, cancellationToken),
                "setInstructionBreakpoints" => await SetInstructionBreakpointsAsync(args, cancellationToken),
                "setExceptionBreakpoints" => await SetExceptionBreakpointsAsync(args, cancellationToken),
                "pause" => await ExecAsync("exec-interrupt", cancellationToken),
                "continue" => await ExecAsync("exec-continue", cancellationToken),
                "next" => await ExecAsync(Granularity(args, "exec-next"), cancellationToken),
                "stepIn" => await ExecAsync(Granularity(args, "exec-step"), cancellationToken),
                "stepOut" => await ExecAsync("exec-finish", cancellationToken),
                "readMemory" => await ReadMemoryAsync(args, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported request '{command}'"),
            };
            await connection.SendResponseAsync(request, success: true, body, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Request '{Command}' failed", command);
            await connection.SendResponseAsync(request, success: false, message: ex.Message,
                cancellationToken: cancellationToken);
        }
    }

    private static string Granularity(JsonObject args, string command)
        => args["granularity"]?.GetValue<string>() == "instruction" ? command + "-instruction" : command;

    private async Task<JsonObject?> ExecAsync(string command, CancellationToken cancellationToken)
    {
        await Gdb.ExecuteAsync(command, cancellationToken);
        return null;
    }

    // #region Lifecycle

    private static JsonObject Initialize() => new()
    {
        ["supportsConfigurationDoneRequest"] = true,
        ["supportsANSIStyling"] = true,
        ["supportsSteppingGranularity"] = true,
        ["supportsInstructionBreakpoints"] = true,
        ["supportsValueFormattingOptions"] = true,
        ["supportsReadMemoryRequest"] = true,
        ["exceptionBreakpointFilters"] = new JsonArray(
            new JsonObject { ["filter"] = "0x7F0", ["label"] = "Fault (Hard/Usage/Bus/Memory)", ["default"] = true },
            new JsonObject { ["filter"] = "0x1", ["label"] = "Core Reset", ["default"] = true }),
    };

    private async Task<JsonObject?> LaunchOrAttachAsync(JsonObject args, bool loadProgram, CancellationToken cancellationToken)
    {
        var config = ResolveLaunchConfiguration(args);
        _launch = loadProgram;
        _stopAtConnect = config["stopAtConnect"]?.GetValue<bool>() ?? false;

        var cwd = config["cwd"]?.GetValue<string>();
        var probeConfig = new ProbeConfig
        {
            Program = config["program"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Launch configuration has no 'program'"),
            Gdb = config["gdb"]?.GetValue<string>() ?? "gdb",
            Server = config["server"]
                ?? throw new InvalidOperationException("Launch configuration has no 'server'"),
            Cwd = cwd,
            SmartLoad = config["smartLoad"]?.GetValue<bool>() ?? true,
            ServerOutput = line => _ = SendOutputAsync("stdout", line + "\n"),
        };

        _probe = new Probe(probeConfig, logger);
        await _probe.ConnectAsync(cancellationToken);
        Gdb.Stream += (type, text) =>
        {
            if (type == MiRecordType.ConsoleStream)
                _ = SendOutputAsync("console", text);
        };

        if (loadProgram && !Probe.Server.SkipLoad)
        {
            await SendOutputAsync("console", "Loading program...\n");
            await Probe.LoadAsync(cancellationToken);

            // starti: the running event comes only after the command returns,
            // so set up the not-stopped/stopped gate before issuing it.
            var startStop = Task.Run(async () =>
            {
                await Gdb.WaitThreadsNotStoppedAsync();
                await Gdb.WaitThreadsStoppedAsync();
            }, cancellationToken);
            await Gdb.ConsoleAsync("starti", cancellationToken);
            await startStop;
        }

        await Gdb.WaitThreadsStoppedAsync();

        await connection.SendEventAsync("initialized", cancellationToken: cancellationToken);
        return null;
    }

    /// <summary>
    /// Resolves the effective launch configuration: a <c>config</c> name is
    /// expanded through the build system (the `minuteos info --json` model);
    /// inline arguments override the resolved values.
    /// </summary>
    private JsonObject ResolveLaunchConfiguration(JsonObject args)
    {
        if (args["config"]?.GetValue<string>() is not { } configName)
            return args;

        var projectRoot = ProjectConfig.GetProjectRoot(
            args["projectDir"]?.GetValue<string>() ?? args["cwd"]?.GetValue<string>());
        var projectConfig = ProjectConfig.Load(projectRoot);
        var buildConfig = BuildConfiguration.Create(projectConfig, configName, projectRoot);
        var model = MinuteDebugConfig.Describe(buildConfig);

        foreach (var (key, value) in args)
        {
            if (key is not ("config" or "projectDir"))
                model[key] = value?.DeepClone();
        }
        return model;
    }

    private async Task<JsonObject?> ConfigurationDoneAsync(CancellationToken cancellationToken)
    {
        if (_configured)
            return null;
        _configured = true;

        // Report the initial thread states (started + stopped), then track changes.
        SendExecEvents();
        Gdb.ThreadsChanged += _ => SendExecEvents();

        if (_launch && !_stopAtConnect)
        {
            await Gdb.ExecuteAsync("exec-continue --all", cancellationToken);
            await Gdb.WaitThreadsNotStoppedAsync();
        }
        return null;
    }

    private async Task<JsonObject?> DisconnectAsync()
    {
        await CleanupAsync();
        _terminated = true;
        return null;
    }

    private async Task CleanupAsync()
    {
        if (_probe is { } probe)
        {
            _probe = null;
            await probe.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(CleanupAsync());

    // #endregion

    // #region Execution state events

    private void SendExecEvents()
    {
        if (_suppressExecEvents > 0)
            return;

        var seen = new HashSet<int>();
        foreach (var thread in Gdb.Threads)
        {
            seen.Add(thread.ThreadId);
            var known = _vsThreads.TryGetValue(thread.ThreadId, out var wasStopped);
            if (!known)
                _ = connection.SendEventAsync("thread", new JsonObject
                {
                    ["reason"] = "started",
                    ["threadId"] = thread.ThreadId,
                });
            if (!known || wasStopped != thread.Stopped)
            {
                _vsThreads[thread.ThreadId] = thread.Stopped;
                _ = thread.Stopped
                    ? connection.SendEventAsync("stopped", new JsonObject
                    {
                        ["reason"] = MapStopReason(thread),
                        ["threadId"] = thread.ThreadId,
                    })
                    : connection.SendEventAsync("continued", new JsonObject
                    {
                        ["threadId"] = thread.ThreadId,
                    });
            }
        }

        foreach (var id in _vsThreads.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _vsThreads.Remove(id);
            _ = connection.SendEventAsync("thread", new JsonObject
            {
                ["reason"] = "exited",
                ["threadId"] = id,
            });
        }
    }

    private static string MapStopReason(MiThreadState thread)
    {
        switch (thread.Reason)
        {
            case "breakpoint-hit":
                return "breakpoint";
            case "end-stepping-range" or "function-finished":
                return "step";
        }
        // A user-requested pause interrupts the target with SIGINT.
        if (thread.Details?["signal-name"]?.GetValue<string>() == "SIGINT")
            return "pause";
        return thread.Details?["signal-meaning"]?.GetValue<string>()
            ?? thread.Details?["signal-name"]?.GetValue<string>()
            ?? thread.Reason
            ?? "stopped";
    }

    private Task SendOutputAsync(string category, string text)
        => connection.SendEventAsync("output", new JsonObject
        {
            ["category"] = category,
            ["output"] = text,
        });

    /// <summary>
    /// Runs a callback with the target stopped, interrupting/resuming around it
    /// when threads are running (breakpoint changes while the target runs).
    /// </summary>
    private async Task ExecWhileStoppedAsync(Func<Task> callback, CancellationToken cancellationToken)
    {
        if (Gdb.Threads.All(t => t.Stopped))
        {
            await callback();
            return;
        }

        var sendInterrupt = _interrupted++ == 0;
        _suppressExecEvents++;
        var decremented = false;
        try
        {
            if (sendInterrupt)
                await Gdb.ExecuteAsync("exec-interrupt --all", cancellationToken);
            await Gdb.WaitThreadsStoppedAsync();
            try
            {
                await callback();
            }
            finally
            {
                decremented = true;
                if (--_interrupted == 0)
                    await Gdb.ExecuteAsync("exec-continue --all", cancellationToken);
                await Gdb.WaitThreadsNotStoppedAsync();
            }
        }
        finally
        {
            if (!decremented)
                _interrupted--;
            _suppressExecEvents--;
            SendExecEvents();
        }
    }

    // #endregion

    // #region Threads / stack / scopes / variables

    private async Task<JsonObject> ThreadsAsync(CancellationToken cancellationToken)
    {
        var threads = new JsonArray();
        await ExecWhileStoppedAsync(async () =>
        {
            var res = await Gdb.ExecuteAsync("thread-info", cancellationToken);
            foreach (var node in res.Results["threads"] as JsonArray ?? [])
            {
                if (node is not JsonObject thread)
                    continue;
                threads.Add(new JsonObject
                {
                    ["id"] = MiClient.ParseNumber(thread["id"]),
                    ["name"] = (thread["frame"] as JsonObject)?["func"]?.ToString()
                        ?? thread["target-id"]?.ToString()
                        ?? $"thread {thread["id"]}",
                });
            }
        }, cancellationToken);
        return new JsonObject { ["threads"] = threads };
    }

    private async Task<JsonObject> StackTraceAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var low = MiClient.ParseNumber(args["startFrame"]);
        var levels = args["levels"] is { } l ? MiClient.ParseNumber(l) : 1000;
        if (levels <= 0)
            levels = 1000;
        var res = await Gdb.ExecuteAsync($"stack-list-frames {low} {low + levels - 1}", cancellationToken);

        var stackFrames = new JsonArray();
        foreach (var node in res.Results["stack"] as JsonArray ?? [])
        {
            if (node is not JsonObject frame)
                continue;
            var level = MiClient.ParseNumber(frame["level"]);
            _frameIdMap[level] = frame;

            var mapped = new JsonObject
            {
                ["id"] = level,
                ["name"] = frame["func"]?.ToString() ?? "??",
                ["line"] = MiClient.ParseNumber(frame["line"]),
                ["column"] = 0,
                ["instructionPointerReference"] = frame["addr"]?.ToString(),
            };
            if ((frame["fullname"] ?? frame["file"]) is not null)
            {
                mapped["source"] = new JsonObject
                {
                    ["name"] = frame["file"]?.ToString(),
                    ["path"] = frame["fullname"]?.ToString() ?? frame["file"]?.ToString(),
                };
            }
            stackFrames.Add(mapped);
        }
        return new JsonObject { ["stackFrames"] = stackFrames };
    }

    private JsonObject Scopes(JsonObject args)
    {
        var frameId = MiClient.ParseNumber(args["frameId"]);
        return new JsonObject
        {
            ["scopes"] = new JsonArray(
                new JsonObject { ["name"] = "Local", ["variablesReference"] = ScopeLocal + frameId, ["expensive"] = true },
                new JsonObject { ["name"] = "Global", ["variablesReference"] = ScopeGlobal, ["expensive"] = true },
                new JsonObject { ["name"] = "Registers", ["variablesReference"] = ScopeRegisters, ["expensive"] = true }),
        };
    }

    private async Task<JsonObject> VariablesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var reference = MiClient.ParseNumber(args["variablesReference"]);
        var hex = (args["format"] as JsonObject)?["hex"]?.GetValue<bool>() ?? false;

        var variables = reference switch
        {
            ScopeRegisters => await RegisterVariablesAsync(hex, cancellationToken),
            ScopeGlobal => await GlobalVariablesAsync(cancellationToken),
            >= ScopeLocal => await LocalVariablesAsync(reference - ScopeLocal, cancellationToken),
            _ => await ExpandVariableAsync(reference, cancellationToken),
        };
        return new JsonObject { ["variables"] = variables };
    }

    private async Task<JsonArray> LocalVariablesAsync(int frame, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        if (!_frameIdMap.ContainsKey(frame))
            return result;

        var res = await Gdb.ExecuteAsync($"stack-list-variables --thread 1 --frame {frame} --no-values", cancellationToken);
        foreach (var node in res.Results["variables"] as JsonArray ?? [])
        {
            var name = (node as JsonObject)?["name"]?.ToString();
            if (name == null)
                continue;
            try
            {
                var vi = await Gdb.ExecuteAsync($"var-create --thread 1 --frame {frame} - * {MiClient.Quote(name)}", cancellationToken);
                result.Add(CreateOrRegisterVar(name, vi.Results).ToVariable());
            }
            catch (MiException ex)
            {
                logger.LogDebug("Failed to create local variable {Name}: {Message}", name, ex.Message);
            }
        }
        return result;
    }

    private List<(string File, string Symbol)>? _globalSymbols;

    private async Task<JsonArray> GlobalVariablesAsync(CancellationToken cancellationToken)
    {
        if (_globalSymbols == null)
        {
            _globalSymbols = [];
            var res = await Gdb.ExecuteAsync("symbol-info-variables", cancellationToken);
            foreach (var node in (res.Results["symbols"] as JsonObject)?["debug"] as JsonArray ?? [])
            {
                if (node is not JsonObject file || file["filename"]?.ToString() is not { } filename)
                    continue;
                foreach (var symNode in file["symbols"] as JsonArray ?? [])
                {
                    if (symNode is not JsonObject sym || sym["name"]?.ToString() is not { } name)
                        continue;
                    if (sym["description"]?.ToString()?.StartsWith("static ") == true)
                        continue;
                    _globalSymbols.Add((filename, name));
                }
            }
        }

        var result = new JsonArray();
        foreach (var (file, name) in _globalSymbols)
        {
            // 'file'::name, quoting names with special characters
            var symbol = System.Text.RegularExpressions.Regex.IsMatch(name, "[:<>()]") ? $"'{name}'" : name;
            var expression = $"'{file}'::{symbol}";
            if (_varMap.TryGetValue("expr:" + expression, out var existing))
            {
                result.Add(existing.ToVariable());
                continue;
            }
            try
            {
                var vi = await Gdb.ExecuteAsync($"var-create - 0 {MiClient.Quote(expression)}", cancellationToken);
                var gdbVar = CreateOrRegisterVar(name, vi.Results);
                _varMap["expr:" + expression] = gdbVar;
                result.Add(gdbVar.ToVariable());
            }
            catch (MiException ex)
            {
                logger.LogDebug("Failed to create global variable {Name}: {Message}", name, ex.Message);
            }
        }
        return result;
    }

    private async Task<JsonArray> RegisterVariablesAsync(bool hex, CancellationToken cancellationToken)
    {
        var names = ((await Gdb.ExecuteAsync("data-list-register-names", cancellationToken))
            .Results["register-names"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToList();
        var values = await Gdb.ExecuteAsync(
            $"data-list-register-values --skip-unavailable {(hex ? 'x' : 'N')}", cancellationToken);

        var result = new JsonArray();
        foreach (var node in values.Results["register-values"] as JsonArray ?? [])
        {
            if (node is not JsonObject rv)
                continue;
            var number = MiClient.ParseNumber(rv["number"]);
            if (number < 0 || number >= names.Count || names[number].Length == 0)
                continue;
            result.Add(new JsonObject
            {
                ["name"] = names[number],
                ["value"] = rv["value"]?.ToString() ?? "",
                ["variablesReference"] = 0,
            });
        }
        return result;
    }

    private async Task<JsonArray> ExpandVariableAsync(int reference, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        if (!_varMap.TryGetValue(reference, out var parent))
            return result;

        var res = await Gdb.ExecuteAsync($"var-list-children --all-values {MiClient.Quote(parent.Name)}", cancellationToken);
        foreach (var node in res.Results["children"] as JsonArray ?? [])
        {
            if (node is not JsonObject child)
                continue;
            var exp = child["exp"]?.ToString() ?? "";
            if (child["type"] == null && exp is "public" or "protected" or "private" or "internal")
            {
                // Access-modifier pseudo-children: splice their children in.
                foreach (var inner in (await Gdb.ExecuteAsync(
                    $"var-list-children --all-values {MiClient.Quote(child["name"]!.ToString())}", cancellationToken))
                    .Results["children"] as JsonArray ?? [])
                {
                    if (inner is JsonObject innerChild)
                        result.Add(CreateOrRegisterVar(innerChild["exp"]?.ToString() ?? "", innerChild).ToVariable());
                }
            }
            else
            {
                result.Add(CreateOrRegisterVar(exp, child).ToVariable());
            }
        }
        return result;
    }

    private sealed class GdbVar
    {
        public required string DisplayName { get; init; }
        /// <summary>The gdb varobj name.</summary>
        public required string Name { get; init; }
        public string? Type { get; init; }
        public string Value = "";
        public bool Expandable;
        public int Ref { get; init; }

        public void Update(JsonNode? value, int numChildren)
        {
            var text = value?.ToString() ?? "";
            if (Type != null && Type.EndsWith('*')
                && (text == "0" || text.StartsWith("0x") && text.TrimEnd('0', 'x').Length == 0))
            {
                Value = "NULL";
                Expandable = false;
            }
            else
            {
                Value = text;
                Expandable = numChildren > 0;
            }
        }

        public JsonObject ToVariable()
        {
            var variable = new JsonObject
            {
                ["name"] = DisplayName,
                ["value"] = Value,
                ["variablesReference"] = Expandable ? Ref : 0,
            };
            if (Value.StartsWith("0x"))
                variable["memoryReference"] = Value.Split(' ')[0];
            if (Type != null)
                variable["type"] = Type;
            return variable;
        }
    }

    private GdbVar CreateOrRegisterVar(string displayName, JsonObject vi)
    {
        var gdbVar = new GdbVar
        {
            DisplayName = displayName,
            Name = vi["name"]?.ToString() ?? "",
            Type = vi["type"]?.ToString(),
            Ref = _varNextRef++,
        };
        gdbVar.Update(vi["value"], MiClient.ParseNumber(vi["numchild"]));
        _varMap[gdbVar.Name] = gdbVar;
        _varMap[gdbVar.Ref] = gdbVar;
        return gdbVar;
    }

    // #endregion

    // #region Evaluate

    private async Task<JsonObject> EvaluateAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var expression = args["expression"]?.GetValue<string>() ?? "";
        var context = args["context"]?.GetValue<string>();

        if (context == "repl" && expression.StartsWith('>'))
        {
            // Console command passthrough.
            var res = await Gdb.ConsoleAsync(expression[1..].Trim(), cancellationToken);
            var output = (res.Console ?? "") + (res.Output ?? "");
            return new JsonObject { ["result"] = output, ["variablesReference"] = 0 };
        }

        if (context == "repl" && expression.Length > 1 && expression[0] == '-' && char.IsAsciiLetterLower(expression[1]))
        {
            // Raw MI command.
            var res = await Gdb.ExecuteAsync(expression[1..], cancellationToken);
            return new JsonObject
            {
                ["result"] = res.Results.ToJsonString(JsonSerializerOptions.Default),
                ["variablesReference"] = 0,
            };
        }

        // Evaluate an expression in the requested frame.
        var frameAddr = args["frameId"] is { } frameId
            && _frameIdMap.TryGetValue(MiClient.ParseNumber(frameId), out var frame)
            ? frame["addr"]?.ToString() ?? "*"
            : "*";
        var vi = await Gdb.ExecuteAsync($"var-create - {frameAddr} {MiClient.Quote(expression)}", cancellationToken);
        var gdbVar = CreateOrRegisterVar(expression, vi.Results);
        if (!gdbVar.Expandable)
        {
            _varMap.Remove(gdbVar.Name);
            _varMap.Remove(gdbVar.Ref);
            await Gdb.ExecuteAsync($"var-delete {MiClient.Quote(gdbVar.Name)}", cancellationToken);
        }
        var result = new JsonObject
        {
            ["result"] = gdbVar.Value,
            ["variablesReference"] = gdbVar.Expandable ? gdbVar.Ref : 0,
        };
        if (gdbVar.Value.StartsWith("0x"))
            result["memoryReference"] = gdbVar.Value.Split(' ')[0];
        return result;
    }

    // #endregion

    // #region Breakpoints

    private async Task<JsonObject> SetBreakpointsAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var path = (args["source"] as JsonObject)?["path"]?.GetValue<string>() ?? "<unknown>";
        var wanted = (args["breakpoints"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (!_breakpointMap.TryGetValue(path, out var current))
            _breakpointMap[path] = current = [];

        var breakpoints = new JsonArray();
        await ExecWhileStoppedAsync(
            () => SetBreakpointsCoreAsync(
                breakpoints, wanted, current,
                (want, have) => MiClient.ParseNumber(want["line"]) == MiClient.ParseNumber(have["line"]),
                want => Gdb.ExecuteAsync(
                    $"break-insert --source {MiClient.Quote(path)} --line {MiClient.ParseNumber(want["line"])}",
                    cancellationToken),
                cancellationToken),
            cancellationToken);
        return new JsonObject { ["breakpoints"] = breakpoints };
    }

    private async Task<JsonObject> SetInstructionBreakpointsAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var wanted = (args["breakpoints"] as JsonArray ?? []).OfType<JsonObject>().ToList();

        static string Location(JsonObject bp)
        {
            var reference = bp["instructionReference"]?.GetValue<string>() ?? "0";
            var offset = MiClient.ParseNumber(bp["offset"]);
            return offset == 0 ? $"*{reference}"
                : offset > 0 ? $"*({reference}+{offset})"
                : $"*({reference}{offset})";
        }

        var breakpoints = new JsonArray();
        await ExecWhileStoppedAsync(
            () => SetBreakpointsCoreAsync(
                breakpoints, wanted, _instructionBreakpoints,
                (want, have) => Location(want) == have["original-location"]?.ToString(),
                want => Gdb.ExecuteAsync($"break-insert {Location(want)}", cancellationToken),
                cancellationToken),
            cancellationToken);
        return new JsonObject { ["breakpoints"] = breakpoints };
    }

    private async Task SetBreakpointsCoreAsync(
        JsonArray results,
        List<JsonObject> wanted,
        List<JsonObject> current,
        Func<JsonObject, JsonObject, bool> comparer,
        Func<JsonObject, Task<MiResult>> create,
        CancellationToken cancellationToken)
    {
        // Remove breakpoints that are no longer wanted.
        var remove = current.Where(have => !wanted.Any(want => comparer(want, have))).ToList();
        if (remove.Count > 0)
        {
            await Gdb.ExecuteAsync(
                "break-delete " + string.Join(' ', remove.Select(b => MiClient.ParseNumber(b["number"]))),
                cancellationToken);
            current.RemoveAll(remove.Contains);
        }

        // Add new ones / match existing.
        foreach (var want in wanted)
        {
            if (current.FirstOrDefault(have => comparer(want, have)) is { } existing)
            {
                results.Add(new JsonObject
                {
                    ["verified"] = true,
                    ["id"] = MiClient.ParseNumber(existing["number"]),
                });
                continue;
            }

            try
            {
                var res = await create(want);
                var bkpt = res.Results["bkpt"] as JsonObject
                    ?? (res.Results["bkpt"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()
                    ?? [];
                current.Add(bkpt);
                results.Add(new JsonObject
                {
                    ["verified"] = true,
                    ["id"] = MiClient.ParseNumber(bkpt["number"]),
                    ["line"] = MiClient.ParseNumber(bkpt["line"]),
                    ["message"] = bkpt["func"]?.ToString(),
                });
            }
            catch (Exception ex)
            {
                results.Add(new JsonObject
                {
                    ["verified"] = false,
                    ["message"] = ex.Message,
                });
            }
        }
    }

    private async Task<JsonObject> SetExceptionBreakpointsAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var mask = 0;
        foreach (var filter in args["filters"] as JsonArray ?? [])
        {
            var s = filter?.ToString() ?? "0";
            mask |= s.StartsWith("0x") ? Convert.ToInt32(s[2..], 16) : int.Parse(s);
        }

        try
        {
            await ExecWhileStoppedAsync(() => SetCortexExceptionMaskAsync(mask, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            // Not all targets expose the Cortex-M debug peripherals (e.g. qemu).
            logger.LogDebug("Exception breakpoints unavailable: {Message}", ex.Message);
        }
        return new JsonObject { ["breakpoints"] = new JsonArray() };
    }

    /// <summary>Sets the Cortex-M vector-catch mask in DEMCR (the extension's Cortex.setExceptionMask).</summary>
    private async Task SetCortexExceptionMaskAsync(int mask, CancellationToken cancellationToken)
    {
        const ulong romTable = 0xE00FF000;
        const int scsDemcr = 0xDFC;

        var rom = await Gdb.ReadMemoryAsync(romTable, 4, cancellationToken);
        var scsEntry = BitConverter.ToUInt32(rom);
        if ((scsEntry & 1) == 0)
            throw new NotSupportedException("No SCS entry in the Cortex-M ROM table");
        var scs = (uint)(romTable + (scsEntry & ~3u));

        var demcr = BitConverter.ToUInt32(await Gdb.ReadMemoryAsync(scs + scsDemcr, 4, cancellationToken));
        demcr = (uint)((demcr & ~0xFFFFu) | (uint)mask);
        await Gdb.WriteMemoryAsync(scs + scsDemcr, BitConverter.GetBytes(demcr), cancellationToken);
    }

    // #endregion

    private async Task<JsonObject> ReadMemoryAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var reference = args["memoryReference"]?.GetValue<string>() ?? "0";
        var address = (ulong)((long)MiClient.ParseNumberLong(JsonValue.Create(reference))
            + MiClient.ParseNumberLong(args["offset"]));
        var count = MiClient.ParseNumber(args["count"]);
        var data = count > 0 ? await Gdb.ReadMemoryAsync(address, count, cancellationToken) : [];
        return new JsonObject
        {
            ["address"] = $"0x{address:x}",
            ["data"] = Convert.ToBase64String(data),
        };
    }
}
