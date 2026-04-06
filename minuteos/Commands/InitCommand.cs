using MinuteOS.Cli.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("init", Description = "Initialize a new minuteos.yaml configuration")]
public class InitCommand : LoggingCommand
{
    [Option("--project", "-p", Description = "Project root directory")]
    public string? ProjectDir { get; set; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectRoot = ProjectDir != null
            ? Path.GetFullPath(ProjectDir)
            : Directory.GetCurrentDirectory();

        var configPath = Path.Combine(projectRoot, ProjectConfig.FileName);

        if (File.Exists(configPath))
        {
            Logger.LogError("{File} already exists.", ProjectConfig.FileName);
            return Task.FromResult(1);
        }

        var name = Path.GetFileName(projectRoot);

        var yaml = $$"""
            name: {{name}}

            defaults:
              target: host
              config: Release
              components:
                - kernel

            configurations:
              release: {}

              debug:
                config: Debug

              # Example ARM target:
              # arm:
              #   target: cortex-m4
              #   config: Release
              #   toolchain-prefix: arm-none-eabi-
              #   arch-flags:
              #     - -mcpu=cortex-m4
              #     - -mthumb
              #   link-flags:
              #     - -T
              #     - linker.ld
              #     - --specs=nosys.specs
            """;

        File.WriteAllText(configPath, yaml);
        Logger.LogInformation("Created {File}", configPath);

        return Task.FromResult(0);
    }
}
