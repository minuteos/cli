using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Represents a GCC-based toolchain for compiling minuteos projects.
/// Mirrors the toolchain configuration from Base.mk.
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

    /// <summary>
    /// Compiles a single source file to an object file.
    /// </summary>
    public async Task<CompilationResult> CompileAsync(
        SourceFile source,
        string outputPath,
        BuildConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var compiler = source.Language == SourceLanguage.C ? CC : CXX;

        var args = new List<string> { "-c", source.FullPath };

        // Add defines
        foreach (var define in config.Defines)
        {
            args.Add("-D");
            args.Add(define);
        }

        // Add include directories
        foreach (var inc in config.IncludeDirs)
        {
            args.Add("-I");
            args.Add(inc);
        }

        // Add per-source private include directory
        var privateDir = Path.Combine(Path.GetDirectoryName(source.FullPath)!, "private");
        if (Directory.Exists(privateDir))
        {
            args.Add("-I");
            args.Add(privateDir);
        }

        // Dependency generation
        args.AddRange(["-MMD", "-MP"]);

        // Common flags
        args.AddRange(["-g", "-Wall", "-fmessage-length=0", "-fno-exceptions", "-fdata-sections", "-ffunction-sections"]);

        // Language-specific flags
        if (source.Language == SourceLanguage.C)
        {
            args.Add("-std=gnu11");
        }
        else if (source.Language == SourceLanguage.Cpp)
        {
            args.AddRange(["-std=gnu++17", "-fno-rtti", "-fno-threadsafe-statics", "-fno-use-cxa-atexit"]);
        }

        // Optimization flags (Release default)
        if (config.Config == "Debug")
        {
            args.Add("-O0");
        }
        else
        {
            args.AddRange(["-O3", "-Os"]);
        }

        // Output
        args.AddRange(["-o", outputPath]);

        return await RunAsync(compiler, args, config.Layout.ProjectRoot, cancellationToken);
    }

    /// <summary>
    /// Links object files into the primary output ELF.
    /// </summary>
    public async Task<CompilationResult> LinkAsync(
        IEnumerable<string> objectFiles,
        string outputPath,
        BuildConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-o", outputPath };
        args.AddRange(objectFiles.Order());

        // Add library search paths
        var libDirs = config.TargetDirs.Concat(config.ComponentDirs);
        if (Directory.Exists(config.Layout.SourceDir))
            libDirs = new[] { config.Layout.SourceDir }.Concat(libDirs);

        foreach (var dir in libDirs)
        {
            args.Add("-L");
            args.Add(dir);
        }

        // Garbage-collect unused sections
        args.Add("-Wl,--gc-sections");

        return await RunAsync(CXX, args, config.Layout.ProjectRoot, cancellationToken);
    }

    /// <summary>
    /// Reports the size of the output binary.
    /// </summary>
    public async Task<CompilationResult> SizeAsync(string elfPath, CancellationToken cancellationToken = default)
    {
        return await RunAsync(Size, [elfPath], Path.GetDirectoryName(elfPath)!, cancellationToken);
    }

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
