using YamlDotNet.Serialization;

namespace MinuteOS.Build;

/// <summary>
/// Describes how to execute a compiled test binary for a configuration or target.
///
/// When <see cref="Command"/> is null the binary is executed directly (host builds).
/// Otherwise the binary is launched via the given command, e.g. an emulator:
///
///   # qemu
///   test-runner:
///     command: qemu-system-arm
///     args: [-machine, lm3s6965evb, -nographic, -semihosting, -kernel, "{binary}"]
///     timeout: 60
///
///   # renode
///   test-runner:
///     command: renode-test
///     args: ["{binary}"]
///
/// Placeholders substituted in args:
///   {binary} - absolute path to the compiled test binary
///   {filter} - the active test filter (removed if no filter is set)
/// </summary>
public class TestRunnerConfig
{
    /// <summary>
    /// The executable used to run the test binary. Null/empty means run the
    /// binary directly (host execution).
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Arguments passed to the runner command. Supports {binary} and {filter}.
    /// </summary>
    public List<string>? Args { get; set; }

    /// <summary>
    /// Timeout in seconds for a single test suite run. Default 60.
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// Builds the (program, args) to execute for a given binary and optional filter.
    /// </summary>
    public (string Program, List<string> Args) Resolve(string binaryPath, string? filter)
    {
        if (string.IsNullOrEmpty(Command))
        {
            // Run the binary directly
            var directArgs = new List<string>();
            if (!string.IsNullOrEmpty(filter))
                directArgs.Add(filter);
            return (binaryPath, directArgs);
        }

        var args = new List<string>();
        foreach (var arg in Args ?? [])
        {
            // {filter} is always substituted (empty when no filter is set), so an
            // option that takes the filter as its value - e.g. qemu's `-append
            // {filter}` - keeps its argument rather than being left dangling.
            var replaced = arg
                .Replace("{binary}", binaryPath)
                .Replace("{filter}", filter ?? "");
            args.Add(replaced);
        }

        return (Command, args);
    }
}
