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
    private const int ScopePeripherals = 0x10000001;
    private const int ScopeGlobal = 0x10000002;
    private const int ScopeLocal = 0x10000003;
    private const int ScopePeripheral = 0x20000000;
    private const int ScopePeripheralRegister = 0x21000000;
    private const int ScopePeripheralGroup = 0x22000000;

    private Probe? _probe;
    private bool _launch;
    private bool _stopAtConnect;
    private bool _configured;
    private bool _terminated;
    private Cortex? _cortex;
    private Task<Svd.SvdDevice?>? _svdTask;
    private Swo.SwoSession? _swoSession;
    private Swo.ISwoSource? _swoSource;
    private readonly Swo.SwoProfiler _profiler = new();
    private Lazy<ElfSymbols>? _symbols;
    private DisassemblyCache? _disassemblyCache;
    private Trace.TraceRecorder? _recorder;
    private Trace.ISmuSampleSource? _smuSource;
    private int _smuRefs;
    private string? _cwd;
    private string? _programPath;
    private Trace.TimelineStore? _timeline;
    private Trace.Symbolizer? _symbolizer;
    private long _timelineStart;
    private CancellationTokenSource? _timelineTick;
    private int _pcSamplingRefs;

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
                "scopes" => await ScopesAsync(args),
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
                "disassemble" => await DisassembleAsync(args, cancellationToken),
                "minuteos.profile.start" => await ProfileStartAsync(cancellationToken),
                "minuteos.profile.stop" => await ProfileStopAsync(args, cancellationToken),
                "minuteos.trace.start" => await TraceStartAsync(args, cancellationToken),
                "minuteos.trace.stop" => await TraceStopAsync(cancellationToken),
                "minuteos.timeline.start" => await TimelineStart(cancellationToken),
                "minuteos.timeline.stop" => await TimelineStop(cancellationToken),
                "minuteos.timeline.histogram" => TimelineHistogram(args),
                "minuteos.timeline.series" => TimelineSeries(args),
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
        ["supportsDisassembleRequest"] = true,
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
        _cwd = cwd;
        var program = config["program"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Launch configuration has no 'program'");
        var programPath = Path.GetFullPath(program, cwd != null ? Path.GetFullPath(cwd) : Environment.CurrentDirectory);
        _programPath = programPath;
        _symbols = new Lazy<ElfSymbols>(() => ElfSymbols.Load(programPath));

        var probeConfig = new ProbeConfig
        {
            Program = program,
            Gdb = config["gdb"]?.GetValue<string>() ?? "gdb",
            Server = config["server"]
                ?? throw new InvalidOperationException("Launch configuration has no 'server'"),
            Smu = config["smu"],
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

        _cortex = new Cortex(Gdb, logger);
        _svdTask = LoadSvdAsync(config["svd"], cancellationToken);

        // Renode framebuffer bridge: announce the socket side-channel so a
        // frontend can connect and render frames.
        if (Probe.Server is Servers.RenodeGdbServer { DisplayPort: { } displayPort })
            await connection.SendEventAsync("minuteos.display", new JsonObject
            {
                ["host"] = "127.0.0.1",
                ["port"] = displayPort,
            }, cancellationToken);

        if (config["swo"] is { } swoNode)
            await StartSwoAsync(swoNode, cancellationToken);

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

    /// <summary>
    /// Starts SWO capture: connects the byte source (BMP trace channel /
    /// Renode ITM overlay), enables emission, configures the target's trace
    /// bits, and forwards stimulus-port 0 to the debug console.
    /// </summary>
    private async Task StartSwoAsync(JsonNode swoNode, CancellationToken cancellationToken)
    {
        var (source, swoConfig) = Swo.SwoSourceFactory.Create(swoNode, logger);
        _swoSource = source;
        await source.ConnectAsync(cancellationToken);
        if (source.Stream is not { } stream)
            return;

        await source.EnableAsync(Probe.Server, Gdb, cancellationToken);

        _profiler.Enabled = swoConfig.PcSample;
        _swoSession = new Swo.SwoSession(swoConfig, _cortex!, stream, packet =>
        {
            if (!packet.Dwt && packet.Channel == 0)
                _ = SendOutputAsync("stdout", System.Text.Encoding.UTF8.GetString(packet.Data));
            _profiler.OnPacket(packet);
            _recorder?.OnSwoPacket(packet);
            _timeline?.OnSwoPacket(TimelineNowNs(), packet);
        }, logger);
        await _swoSession.StartAsync(cancellationToken);
    }

    // #region Profiling (minuteos.profile.* custom requests)

    /// <summary>Starts PC-sample collection: turns on DWT PC sampling and resets counters.</summary>
    private async Task<JsonObject?> ProfileStartAsync(CancellationToken cancellationToken)
    {
        if (_swoSession == null)
            throw new InvalidOperationException("Profiling needs SWO - configure 'swo' in the launch configuration");

        _profiler.Reset();
        if (!_profiler.Enabled)
            await AcquirePcSamplingAsync(cancellationToken);
        _profiler.Enabled = true;
        return null;
    }

    // DWT PC sampling has two independent owners - the profiler and the timeline
    // pie - so it is reference-counted: the hardware bit flips on the first
    // acquire and off on the last release. A no-op without SWO (no PC packets).

    private async Task AcquirePcSamplingAsync(CancellationToken cancellationToken)
    {
        if (_swoSession == null || _cortex == null)
            return;
        if (_pcSamplingRefs++ == 0)
            await ExecWhileStoppedAsync(() => _cortex.SetPcSamplingAsync(true, cancellationToken), cancellationToken);
    }

    private async Task ReleasePcSamplingAsync(CancellationToken cancellationToken)
    {
        if (_cortex == null || _pcSamplingRefs == 0 || --_pcSamplingRefs != 0)
            return;
        try
        {
            await ExecWhileStoppedAsync(() => _cortex.SetPcSamplingAsync(false, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Disabling PC sampling failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Stops collection and returns the symbolicated report: samples grouped
    /// by function (resolved once per unique PC from the program's ELF symbol
    /// table), sorted by count.
    /// </summary>
    private async Task<JsonObject> ProfileStopAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (_profiler.Enabled)
        {
            _profiler.Enabled = false;
            await ReleasePcSamplingAsync(cancellationToken);
        }

        Func<uint, FunctionSymbol?> resolve;
        try
        {
            var symbols = _symbols?.Value;
            resolve = pc => symbols?.Resolve(pc);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cannot load symbols for profiling: {Message}", ex.Message);
            resolve = _ => null;
        }

        var report = _profiler.Report(resolve,
            args["top"] is { } top ? MiClient.ParseNumber(top) : 50);
        await SendOutputAsync("console", Swo.SwoProfiler.FormatReport(report));
        return report;
    }

    // #endregion

    // #region Timeline recording (minuteos.trace.* custom requests)

    /// <summary>
    /// Starts a unified timeline recording (mtrace): ITM logs and DWT PC samples
    /// from the SWO stream, plus SMU measurements once V3PWR streaming lands, on
    /// one host clock. PC events require PC sampling to be enabled (via profiling
    /// or <c>swo.profile</c>); logs and SMU samples are always captured.
    /// </summary>
    private async Task<JsonObject> TraceStartAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (_recorder != null)
            throw new InvalidOperationException("A trace recording is already in progress");

        var path = Path.GetFullPath(args["path"]?.GetValue<string>()
            ?? Path.Combine(_cwd ?? Environment.CurrentDirectory, "trace.mtrace"));

        _recorder = new Trace.TraceRecorder(path);
        var smu = await AcquireSmuAsync(cancellationToken);
        foreach (var channel in smu.Channels)
            _recorder.DefineChannel(channel.Id, channel.Kind, channel.Scale, channel.Name, channel.Unit);
        await SendOutputAsync("console", $"Recording timeline to {path}\n");
        return new JsonObject { ["path"] = path };
    }

    /// <summary>Stops the recording and finalizes the file.</summary>
    private async Task<JsonObject> TraceStopAsync(CancellationToken cancellationToken)
    {
        if (_recorder is not { } recorder)
            return new JsonObject { ["recording"] = false };
        _recorder = null; // stop the SMU fan-out feeding it before we finalize

        var path = recorder.Path;
        var events = recorder.EventCount;
        await ReleaseSmuAsync(cancellationToken);
        await recorder.DisposeAsync();
        await SendOutputAsync("console", $"Timeline written: {path} ({events} events)\n");
        return new JsonObject { ["path"] = path, ["events"] = events };
    }

    // Current measurement is reference-counted like PC sampling: the SMU stream
    // opens on the first consumer (timeline or recording) and closes on the last.
    // One session-level subscription fans each sample out to whichever consumers
    // are active, each stamping it on its own clock. A no-op without an SMU.

    private async Task<Trace.ISmuSampleSource> AcquireSmuAsync(CancellationToken cancellationToken)
    {
        if (_smuRefs++ == 0)
        {
            _smuSource = _probe?.Smu?.CreateSampleSource() ?? new Trace.NullSmuSampleSource();
            _smuSource.Sample += OnSmuSample;
            await _smuSource.StartAsync(cancellationToken);
            if (_smuSource.Channels.Count > 0)
                await SendOutputAsync("console",
                    $"SMU: streaming current from {_smuSource.Channels[0].Name}\n");
        }
        return _smuSource!;
    }

    private async Task ReleaseSmuAsync(CancellationToken cancellationToken)
    {
        if (_smuSource is not { } source || _smuRefs == 0 || --_smuRefs != 0)
            return;
        _smuSource = null;
        source.Sample -= OnSmuSample;
        await source.StopAsync(cancellationToken);
        await source.DisposeAsync();
    }

    private void OnSmuSample(Trace.SmuSample sample)
    {
        _timeline?.AddMeasurement(TimelineNowNs(), sample.Channel, sample.Raw);
        _recorder?.RecordMeasurement(sample.Channel, sample.Raw);
    }

    // #endregion

    // #region Live timeline (minuteos.timeline.* custom requests)

    /// <summary>
    /// Starts feeding a queryable in-memory timeline (PC samples, logs, and SMU
    /// current) and ticking the client so it can pull the visible window. PC
    /// sampling is enabled here (no-op without SWO); the power track needs an
    /// <c>smu</c> in the launch configuration.
    /// </summary>
    private async Task<JsonObject> TimelineStart(CancellationToken cancellationToken)
    {
        if (_timeline != null)
            return new JsonObject { ["running"] = true };

        _timeline = new Trace.TimelineStore();
        _timelineStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _symbolizer ??= Trace.Symbolizer.TryLoad(_programPath);
        // Populate the PC pie without a separate "start profiling" step (no-op
        // when there is no SWO to sample from).
        await AcquirePcSamplingAsync(cancellationToken);
        var smu = await AcquireSmuAsync(cancellationToken);
        foreach (var channel in smu.Channels)
            _timeline.DefineChannel(channel.Id, channel.Scale, channel.Name, channel.Unit);
        StartTimelineTicks();
        return new JsonObject { ["running"] = true };
    }

    private async Task<JsonObject> TimelineStop(CancellationToken cancellationToken)
    {
        StopTimelineTicks();
        if (_timeline != null)
        {
            _timeline = null; // stop the SMU fan-out feeding it first
            await ReleasePcSamplingAsync(cancellationToken);
            await ReleaseSmuAsync(cancellationToken);
        }
        return new JsonObject { ["running"] = false };
    }

    private JsonObject TimelineHistogram(JsonObject args)
    {
        if (_timeline is not { } store)
            throw new InvalidOperationException("The timeline is not running");
        var (from, to) = TimelineWindow(args, store);
        var granularity = Trace.TimelineJson.ParseGranularity(args["granularity"]?.GetValue<string>());
        var top = args["top"] is { } t ? (int)MiClient.ParseNumberLong(t) : 100;
        return Trace.TimelineJson.ToJson(store.Histogram(from, to, granularity, _symbolizer, top));
    }

    private JsonObject TimelineSeries(JsonObject args)
    {
        if (_timeline is not { } store)
            throw new InvalidOperationException("The timeline is not running");
        var (from, to) = TimelineWindow(args, store);
        var maxPoints = args["maxPoints"] is { } m ? (int)MiClient.ParseNumberLong(m) : 600;
        return Trace.TimelineJson.ToJson(store.Series(from, to, maxPoints));
    }

    private static (long From, long To) TimelineWindow(JsonObject args, Trace.TimelineStore store)
    {
        var (start, end) = store.Range();
        return (args["from"] is { } f ? MiClient.ParseNumberLong(f) : start,
            args["to"] is { } t ? MiClient.ParseNumberLong(t) : end);
    }

    private long TimelineNowNs() => System.Diagnostics.Stopwatch.GetElapsedTime(_timelineStart).Ticks * 100;

    private void StartTimelineTicks()
    {
        StopTimelineTicks();
        var cts = new CancellationTokenSource();
        _timelineTick = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(250, cts.Token);
                    if (_timeline != null)
                        await connection.SendEventAsync("minuteos.timeline",
                            new JsonObject { ["now"] = TimelineNowNs() }, cts.Token);
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (Exception ex) { logger.LogDebug(ex, "Timeline tick loop ended"); }
        }, cts.Token);
    }

    private void StopTimelineTicks()
    {
        _timelineTick?.Cancel();
        _timelineTick?.Dispose();
        _timelineTick = null;
    }

    // #endregion

    /// <summary>
    /// Resolves the SVD layers: a model name (or .svd path), or a list of
    /// <c>{ model, peripherals }</c> layers; model "target" uses what the
    /// server detected. Layers merge as first device + all peripherals.
    /// </summary>
    private async Task<Svd.SvdDevice?> LoadSvdAsync(JsonNode? svdNode, CancellationToken cancellationToken)
    {
        var layers = svdNode switch
        {
            null => new JsonArray(new JsonObject { ["model"] = "target" }),
            JsonArray array => array,
            JsonObject obj => [(JsonObject)obj.DeepClone()],
            _ => [new JsonObject { ["model"] = svdNode.GetValue<string>() }],
        };

        var cache = new Svd.SvdCache(logger);
        var devices = new List<Svd.SvdDevice>();
        foreach (var layer in layers.OfType<JsonObject>())
        {
            try
            {
                var model = layer["model"]?.GetValue<string>();
                if (model == "target")
                    model = Probe.Target.Model;
                if (string.IsNullOrEmpty(model))
                    continue;

                var device = await cache.GetAsync(model, cancellationToken);
                if (device == null)
                    continue;

                if (layer["peripherals"] is { } filterNode)
                {
                    var patterns = filterNode is JsonArray filters
                        ? filters.Select(f => f?.GetValue<string>() ?? "").ToList()
                        : [filterNode.GetValue<string>()];
                    var matcher = Svd.SvdCache.WildcardMatcher(patterns);
                    device.Peripherals = device.Peripherals.Where(p => matcher(p.Name)).ToList();
                }
                devices.Add(device);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to load SVD layer: {Message}", ex.Message);
            }
        }

        if (devices.Count == 0)
            return null;
        var merged = devices[0];
        merged.Peripherals = devices.SelectMany(d => d.Peripherals).ToList();
        return merged;
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
        StopTimelineTicks();
        _timeline = null;
        if (_recorder is { } recorder)
        {
            _recorder = null;
            await recorder.DisposeAsync();
        }
        // Stop the SMU stream (sends `stop`) before the probe disposes the
        // SessionSmu, which cuts power and closes the shared VCP.
        if (_smuSource is { } smuSource)
        {
            _smuSource = null;
            _smuRefs = 0;
            smuSource.Sample -= OnSmuSample;
            await smuSource.StopAsync(CancellationToken.None);
            await smuSource.DisposeAsync();
        }
        if (_swoSession is { } swoSession)
        {
            _swoSession = null;
            await swoSession.DisposeAsync();
        }
        if (_swoSource is { } swoSource)
        {
            _swoSource = null;
            await swoSource.DisposeAsync();
        }
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
        // A late thread-state notification can arrive after teardown (the
        // ThreadsChanged subscription outlives the probe); ignore it rather than
        // touch the disposed gdb.
        if (_suppressExecEvents > 0 || _terminated || _probe is null)
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

    private async Task<JsonObject> ScopesAsync(JsonObject args)
    {
        var frameId = MiClient.ParseNumber(args["frameId"]);
        var scopes = new JsonArray(
            new JsonObject { ["name"] = "Local", ["variablesReference"] = ScopeLocal + frameId, ["expensive"] = true },
            new JsonObject { ["name"] = "Global", ["variablesReference"] = ScopeGlobal, ["expensive"] = true },
            new JsonObject { ["name"] = "Registers", ["variablesReference"] = ScopeRegisters, ["expensive"] = true });
        if (await GetSvdAsync() is { } svd)
            scopes.Add(new JsonObject
            {
                ["name"] = $"Peripherals ({svd.Name})",
                ["variablesReference"] = ScopePeripherals,
                ["expensive"] = true,
            });
        return new JsonObject { ["scopes"] = scopes };
    }

    private async Task<Svd.SvdDevice?> GetSvdAsync()
    {
        try
        {
            return _svdTask == null ? null : await _svdTask;
        }
        catch (Exception ex)
        {
            logger.LogWarning("SVD load failed: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<JsonObject> VariablesAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var reference = MiClient.ParseNumber(args["variablesReference"]);
        var hex = (args["format"] as JsonObject)?["hex"]?.GetValue<bool>() ?? false;

        var variables = reference switch
        {
            ScopeRegisters => await RegisterVariablesAsync(hex, cancellationToken),
            ScopeGlobal => await GlobalVariablesAsync(cancellationToken),
            ScopePeripherals => await PeripheralsScopeAsync(),
            >= ScopePeripheralGroup => await PeripheralGroupAsync(reference - ScopePeripheralGroup),
            >= ScopePeripheralRegister => await PeripheralRegisterFieldsAsync(reference - ScopePeripheralRegister),
            >= ScopePeripheral => await PeripheralRegistersAsync(reference - ScopePeripheral, cancellationToken),
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

    // #region SVD peripheral scopes

    private readonly Dictionary<Svd.SvdPeripheral, byte[][]> _peripheralData = [];

    private async Task<JsonArray> PeripheralsScopeAsync()
    {
        var result = new JsonArray();
        if (await GetSvdAsync() is not { } svd)
            return result;

        var grouped = new Dictionary<string, List<(int Index, Svd.SvdPeripheral Peripheral)>>();
        foreach (var (peripheral, index) in svd.Peripherals.Select((p, i) => (p, i)))
            (grouped.TryGetValue(peripheral.GroupName ?? "", out var list)
                ? list
                : grouped[peripheral.GroupName ?? ""] = []).Add((index, peripheral));

        var rows = new List<JsonObject>();
        foreach (var (group, peripherals) in grouped)
        {
            foreach (var (index, peripheral) in peripherals)
            {
                if (group.Length == 0 || peripherals.Count == 1)
                {
                    rows.Add(Row(peripheral.Name, peripheral.Description ?? peripheral.Name, ScopePeripheral + index));
                }
                else
                {
                    rows.Add(Row(group,
                        peripheral.Description != null ? $"Group ({peripheral.Description})" : "Group",
                        ScopePeripheralGroup + index));
                    break;
                }
            }
        }

        foreach (var row in rows.OrderBy(r => r["name"]!.GetValue<string>(), NaturalComparer.Instance))
            result.Add(row);
        return result;

        static JsonObject Row(string name, string value, int reference) => new()
        {
            ["name"] = name,
            ["value"] = value,
            ["variablesReference"] = reference,
        };
    }

    private async Task<JsonArray> PeripheralGroupAsync(int index)
    {
        var result = new JsonArray();
        if (await GetSvdAsync() is not { } svd || index >= svd.Peripherals.Count)
            return result;

        var groupName = svd.Peripherals[index].GroupName;
        var rows = svd.Peripherals
            .Select((p, i) => (Peripheral: p, Index: i))
            .Where(x => x.Peripheral.GroupName == groupName)
            .Select(x => new JsonObject
            {
                ["name"] = x.Peripheral.Name,
                ["value"] = x.Peripheral.Description ?? x.Peripheral.Name,
                ["variablesReference"] = ScopePeripheral + x.Index,
            })
            .OrderBy(r => r["name"]!.GetValue<string>(), NaturalComparer.Instance);
        foreach (var row in rows)
            result.Add(row);
        return result;
    }

    private async Task<JsonArray> PeripheralRegistersAsync(int index, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        if (await GetSvdAsync() is not { } svd || index >= svd.Peripherals.Count)
            return result;

        var peripheral = svd.Peripherals[index];

        // A great time to (re)read the peripheral's memory blocks.
        var data = new byte[peripheral.AddressBlocks.Count][];
        for (var i = 0; i < data.Length; i++)
        {
            var block = peripheral.AddressBlocks[i];
            data[i] = await Gdb.ReadMemoryAsync(peripheral.BaseAddress + (ulong)block.Offset, (int)block.Size,
                cancellationToken);
        }
        _peripheralData[peripheral] = data;

        var maxLength = peripheral.Registers.Count > 0 ? peripheral.Registers.Max(r => r.Name.Length) : 0;
        foreach (var (register, i) in peripheral.Registers.Select((r, i) => (r, i)))
        {
            result.Add(new JsonObject
            {
                ["name"] = register.Name.PadRight(maxLength),
                ["value"] = PeripheralRegisterValue(svd, peripheral, register, data) ?? "???",
                ["variablesReference"] = register.Fields is { Count: > 0 }
                    ? ScopePeripheralRegister + (index << 12) + i
                    : 0,
            });
        }
        return result;
    }

    private async Task<JsonArray> PeripheralRegisterFieldsAsync(int packed)
    {
        var result = new JsonArray();
        if (await GetSvdAsync() is not { } svd)
            return result;

        var peripheralIndex = packed >> 12;
        var registerIndex = packed & 0xFFF;
        if (peripheralIndex >= svd.Peripherals.Count)
            return result;
        var peripheral = svd.Peripherals[peripheralIndex];
        if (registerIndex >= peripheral.Registers.Count)
            return result;
        var register = peripheral.Registers[registerIndex];
        if (register.Fields == null || !_peripheralData.TryGetValue(peripheral, out var data))
            return result;

        var maxLength = register.Fields.Count > 0 ? register.Fields.Max(f => f.Name.Length) : 0;
        foreach (var field in register.Fields)
        {
            result.Add(new JsonObject
            {
                ["name"] = field.Name.PadRight(maxLength),
                ["value"] = PeripheralRegisterValue(svd, peripheral, register, data, field) ?? "???",
                ["variablesReference"] = 0,
            });
        }
        return result;
    }

    /// <summary>Formats a register (or one bitfield of it) from the block data read earlier.</summary>
    private static string? PeripheralRegisterValue(Svd.SvdDevice svd, Svd.SvdPeripheral peripheral,
        Svd.SvdRegister register, byte[][] data, Svd.SvdField? field = null)
    {
        var bytes = register.Size / svd.AddressUnitBits;
        var blockIndex = peripheral.AddressBlocks.FindIndex(b =>
            register.AddressOffset >= b.Offset && register.AddressOffset + bytes <= b.Offset + b.Size);
        if (blockIndex < 0)
            return null;

        var block = data[blockIndex];
        var offset = (int)(register.AddressOffset - peripheral.AddressBlocks[blockIndex].Offset);
        if (offset + bytes > block.Length)
            return null;

        ulong value = 0;
        for (var i = 0; i < bytes; i++)
            value |= (ulong)block[offset + i] << (8 * (svd.BigEndian ? bytes - 1 - i : i));

        const string green = "\e[92m", cyan = "\e[96m", yellow = "\e[93m", reset = "\e[0m";
        var parts = new List<string>();
        if (field == null)
        {
            parts.Add($"{green}0x{value.ToString("x").PadLeft(bytes * 2, '0')}{reset}");
            if (register.Description != null)
                parts.Add($"({register.Description})");
        }
        else
        {
            value >>= field.BitOffset;
            value &= field.BitWidth >= 64 ? ulong.MaxValue : (1ul << field.BitWidth) - 1;
            parts.Add($"{cyan}{value}{reset}");
            if (field.BitWidth > 3)
                parts.Add($"{green}0x{value:x}{reset}");
            if (field.BitWidth > 1)
                parts.Add($"{yellow}0b{Convert.ToString((long)value, 2).PadLeft(field.BitWidth, '0')}{reset}");
            if (field.Description != null)
                parts.Add($"({field.Description})");
        }
        return string.Join(' ', parts);
    }

    /// <summary>Natural ("USART2" &lt; "USART10") string ordering for peripheral lists.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var cmp = long.Parse(x[si..i]).CompareTo(long.Parse(y[sj..j]));
                    if (cmp != 0)
                        return cmp;
                }
                else
                {
                    var cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (cmp != 0)
                        return cmp;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    // #endregion

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
            await ExecWhileStoppedAsync(() => _cortex!.SetExceptionMaskAsync(mask, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            // Not all targets expose the Cortex-M debug peripherals (e.g. qemu).
            logger.LogDebug("Exception breakpoints unavailable: {Message}", ex.Message);
        }
        return new JsonObject { ["breakpoints"] = new JsonArray() };
    }

    // #endregion

    private async Task<JsonObject> DisassembleAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var reference = args["memoryReference"]?.GetValue<string>()
            ?? throw new ArgumentException("Invalid disassembly address");
        var baseAddress = MiClient.ParseNumberLong(JsonValue.Create(reference))
            + MiClient.ParseNumberLong(args["offset"]);

        var cache = _disassemblyCache ??= new DisassemblyCache(async (address, ct) =>
        {
            var res = await Gdb.ExecuteAsync($"data-disassemble -a 0x{address:x} --opcodes bytes --source", ct);
            return res.Results["asm_insns"] as JsonArray ?? [];
        }, logger);

        var instructions = await cache.FillAsync(
            baseAddress,
            MiClient.ParseNumber(args["instructionOffset"]),
            MiClient.ParseNumber(args["count"]),
            cancellationToken);

        var mapped = new JsonArray();
        foreach (var ins in instructions)
        {
            var entry = new JsonObject
            {
                ["address"] = ins.Start < 0 ? "-1" : $"0x{ins.Start:x}",
                ["instruction"] = ins.Function != null && ins.Offset == 0
                    ? $"{ins.Mnemonic}\t;;; FUNCTION: {ins.Function}"
                    : ins.Mnemonic,
            };
            if (ins.Bytes != null)
                entry["instructionBytes"] = ins.Bytes;
            if (ins.Source is { } src)
            {
                entry["location"] = new JsonObject { ["name"] = src.File, ["path"] = src.FullName ?? src.File };
                entry["line"] = src.Line;
                if (src.EndLine is { } endLine)
                    entry["endLine"] = endLine;
            }
            if (ins.Function != null)
                entry["symbol"] = ins.Function;
            mapped.Add(entry);
        }
        return new JsonObject { ["instructions"] = mapped };
    }

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
