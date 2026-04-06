using System.ComponentModel;
using MinuteOS.Cli.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("info", Description = "Show project build configuration details")]
public class InfoCommand : LoggingCommand
{
    [Option("--target", "-t", Description = "Target platform (default: host)")]
    [DefaultValue("host")]
    public string Target { get; set; } = "host";

    [Option("--config", "-c", Description = "Build configuration (default: Release)")]
    [DefaultValue("Release")]
    public string Config { get; set; } = "Release";

    [Option("--components", Description = "Components (comma-separated, default: kernel)")]
    public string? Components { get; set; }

    [Option("--project", "-p", Description = "Project root directory (default: current directory)")]
    public string? ProjectDir { get; set; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectDir ?? Directory.GetCurrentDirectory();
        projectRoot = Path.GetFullPath(projectRoot);

        var components = Components?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ["kernel"];

        BuildConfiguration config;
        try
        {
            config = BuildConfiguration.Create(
                projectRoot,
                target: Target,
                config: Config,
                components: components);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to configure: {Message}", ex.Message);
            return Task.FromResult(1);
        }

        Console.WriteLine($"Project:        {config.OutputName}");
        Console.WriteLine($"Root:           {config.Layout.ProjectRoot}");
        Console.WriteLine($"Target:         {config.Target}");
        Console.WriteLine($"Configuration:  {config.Config}");
        Console.WriteLine($"Output:         {config.PrimaryOutput}");
        Console.WriteLine();

        Console.WriteLine("Lib roots:");
        foreach (var root in config.Layout.LibRoots)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, root)}");
        Console.WriteLine();

        Console.WriteLine("Target roots:");
        foreach (var root in config.Layout.TargetRoots)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, root)}");
        Console.WriteLine();

        Console.WriteLine("Target directories:");
        foreach (var dir in config.TargetDirs)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, dir)}");
        Console.WriteLine();

        Console.WriteLine($"Components ({config.Components.Count}):");
        foreach (var c in config.Components)
            Console.WriteLine($"  {c}");
        Console.WriteLine();

        Console.WriteLine("Component directories:");
        foreach (var dir in config.ComponentDirs)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, dir)}");
        Console.WriteLine();

        Console.WriteLine("Include directories:");
        foreach (var dir in config.IncludeDirs)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, dir)}");
        Console.WriteLine();

        Console.WriteLine("Source directories:");
        foreach (var dir in config.SourceDirs)
            Console.WriteLine($"  {Path.GetRelativePath(projectRoot, dir)}");
        Console.WriteLine();

        Console.WriteLine($"Source files ({config.Sources.Count}):");
        foreach (var source in config.Sources)
            Console.WriteLine($"  [{source.Language}] {source.RelativePath}");
        Console.WriteLine();

        Console.WriteLine("Defines:");
        foreach (var d in config.Defines)
            Console.WriteLine($"  {d}");

        return Task.FromResult(0);
    }
}
