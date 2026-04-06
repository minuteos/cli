using Microsoft.Extensions.Logging;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Orchestrates the build process: compiles sources and links the output.
/// </summary>
public class BuildRunner
{
    private readonly Toolchain _toolchain;
    private readonly ILogger _logger;

    public BuildRunner(Toolchain toolchain, ILogger logger)
    {
        _toolchain = toolchain;
        _logger = logger;
    }

    public async Task<bool> BuildAsync(BuildConfiguration config, int parallelism, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Project:      {Name}", config.OutputName);
        _logger.LogInformation("Target:       {Target}", config.Target);
        _logger.LogInformation("Config:       {Config}", config.Config);
        _logger.LogInformation("Components:   {Components}", string.Join(", ", config.Components));
        _logger.LogInformation("Sources:      {Count} files", config.Sources.Count);
        _logger.LogInformation("Output:       {Output}", config.PrimaryOutput);
        _logger.LogInformation("");

        if (config.Sources.Count == 0)
        {
            _logger.LogWarning("No source files found.");
            return false;
        }

        // Compile phase
        _logger.LogInformation("Compiling...");

        var objectFiles = new List<string>();
        var compileTasks = new List<(SourceFile Source, string ObjectPath)>();

        foreach (var source in config.Sources)
        {
            var objPath = config.GetObjectPath(source);
            compileTasks.Add((source, objPath));
            objectFiles.Add(objPath);
        }

        var semaphore = new SemaphoreSlim(parallelism);
        var errors = new List<string>();

        var tasks = compileTasks.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Ensure output directory exists
                var objDir = Path.GetDirectoryName(item.ObjectPath)!;
                Directory.CreateDirectory(objDir);

                // Check if rebuild is needed
                if (!NeedsRebuild(item.Source.FullPath, item.ObjectPath))
                {
                    _logger.LogDebug("Skipping (up-to-date): {Source}", item.Source.RelativePath);
                    return;
                }

                _logger.LogInformation("  {Compiler} -c {Source}",
                    item.Source.Language == SourceLanguage.C ? "gcc" : "g++",
                    item.Source.RelativePath);

                var result = await _toolchain.CompileAsync(item.Source, item.ObjectPath, config, cancellationToken);

                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    _logger.LogWarning("{StdErr}", result.StdErr.TrimEnd());

                if (!result.Success)
                {
                    lock (errors)
                        errors.Add($"Failed to compile {item.Source.RelativePath}: exit code {result.ExitCode}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                _logger.LogError("{Error}", error);
            return false;
        }

        // Link phase
        _logger.LogInformation("");
        _logger.LogInformation("Linking...");

        Directory.CreateDirectory(Path.GetDirectoryName(config.PrimaryOutput)!);

        var linkResult = await _toolchain.LinkAsync(objectFiles, config.PrimaryOutput, config, cancellationToken);
        if (!string.IsNullOrWhiteSpace(linkResult.StdErr))
            _logger.LogWarning("{StdErr}", linkResult.StdErr.TrimEnd());

        if (!linkResult.Success)
        {
            _logger.LogError("Linking failed with exit code {ExitCode}", linkResult.ExitCode);
            return false;
        }

        // Size report
        var sizeResult = await _toolchain.SizeAsync(config.PrimaryOutput, cancellationToken);
        if (sizeResult.Success && !string.IsNullOrWhiteSpace(sizeResult.StdOut))
            _logger.LogInformation("\n{Size}", sizeResult.StdOut.TrimEnd());

        _logger.LogInformation("");
        _logger.LogInformation("Build succeeded: {Output}", config.PrimaryOutput);
        return true;
    }

    private static bool NeedsRebuild(string sourcePath, string objectPath)
    {
        if (!File.Exists(objectPath))
            return true;

        var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
        var objectTime = File.GetLastWriteTimeUtc(objectPath);
        return sourceTime > objectTime;
    }
}
