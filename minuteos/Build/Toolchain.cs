using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build;

/// <summary>
/// GCC-based toolchain for compiling minuteos projects.
/// Compiler flags are driven by the configuration profile.
/// </summary>
public class Toolchain
{
    private readonly ILogger _logger;
    public string Prefix { get; }

    public string CC => Prefix + "gcc";
    public string CXX => Prefix + "g++";
    public string ObjCopy => Prefix + "objcopy";
    public string ObjDump => Prefix + "objdump";
    public string Size => Prefix + "size";

    public Toolchain(string prefix = "", ILogger? logger = null)
    {
        Prefix = prefix;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<CompilationResult> CompileAsync(
        SourceFile source,
        string outputPath,
        BuildConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var compiler = source.Language == SourceLanguage.C ? CC : CXX;
        var profile = config.Profile;

        var args = new List<string> { "-c", source.FullPath };

        // Defines
        foreach (var define in config.Defines)
            args.AddRange(["-D", define]);

        // Include directories
        foreach (var inc in config.IncludeDirs)
            args.AddRange(["-I", inc]);

        // Per-source private include directory
        var privateDir = Path.Combine(Path.GetDirectoryName(source.FullPath)!, "private");
        if (Directory.Exists(privateDir))
            args.AddRange(["-I", privateDir]);

        // Dependency generation
        args.AddRange(["-MMD", "-MP"]);

        // Architecture flags from profile
        if (profile.ArchFlags != null)
            args.AddRange(profile.ArchFlags);

        // Common flags
        args.AddRange(["-g", "-Wall", "-fmessage-length=0", "-fno-exceptions", "-fdata-sections", "-ffunction-sections"]);

        // Optimization
        if (config.Config == "Debug")
            args.Add("-O0");
        else
            args.AddRange(["-O3", "-Os"]);

        // Language-specific flags (profile overrides, then component contributions)
        if (source.Language == SourceLanguage.C)
        {
            args.Add("-std=gnu11");
            if (profile.CFlags != null)
                args.AddRange(profile.CFlags);
            args.AddRange(config.ComponentCFlags);
        }
        else if (source.Language == SourceLanguage.Cpp)
        {
            args.AddRange(["-std=gnu++17", "-fno-rtti", "-fno-threadsafe-statics", "-fno-use-cxa-atexit"]);
            if (profile.CxxFlags != null)
                args.AddRange(profile.CxxFlags);
            args.AddRange(config.ComponentCxxFlags);
        }

        args.AddRange(["-o", outputPath]);

        return await RunAsync(compiler, args, config.Layout.ProjectRoot, cancellationToken);
    }

    public async Task<CompilationResult> LinkAsync(
        IEnumerable<string> objectFiles,
        string outputPath,
        BuildConfiguration config,
        IEnumerable<string>? extraLinkFlags = null,
        CancellationToken cancellationToken = default)
    {
        var profile = config.Profile;
        var args = new List<string> { "-o", outputPath };

        // Architecture flags for linker too
        if (profile.ArchFlags != null)
            args.AddRange(profile.ArchFlags);

        args.AddRange(objectFiles.Order());

        // Library search paths
        var libDirs = config.TargetDirs.Concat(config.ComponentDirs);
        if (Directory.Exists(config.Layout.SourceDir))
            libDirs = new[] { config.Layout.SourceDir }.Concat(libDirs);

        foreach (var dir in libDirs)
            args.AddRange(["-L", dir]);

        // Extra link flags from profile and components
        if (profile.LinkFlags != null)
            args.AddRange(profile.LinkFlags);
        args.AddRange(config.ComponentLinkFlags);

        // Extra link flags from build steps
        if (extraLinkFlags != null)
            args.AddRange(extraLinkFlags);

        // Garbage-collect unused sections
        args.Add("-Wl,--gc-sections");

        return await RunAsync(CXX, args, config.Layout.ProjectRoot, cancellationToken);
    }

    public async Task<CompilationResult> SizeAsync(string elfPath, CancellationToken cancellationToken = default)
    {
        return await RunAsync(Size, [elfPath], Path.GetDirectoryName(elfPath)!, cancellationToken);
    }

    /// <summary>
    /// Runs an arbitrary toolchain program. Used by build steps.
    /// </summary>
    public Task<CompilationResult> RunToolAsync(
        string program,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
        => RunAsync(program, arguments, workingDirectory, cancellationToken);

    private async Task<CompilationResult> RunAsync(
        string program,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var argString = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        _logger.LogDebug("{Program} {Args}", program, argString);

        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {program}");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CompilationResult(
            process.ExitCode,
            stdout,
            stderr,
            $"{program} {argString}");
    }
}

public record CompilationResult(int ExitCode, string StdOut, string StdErr, string Command)
{
    public bool Success => ExitCode == 0;
}
