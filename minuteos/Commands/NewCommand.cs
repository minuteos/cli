using MinuteOS.Cli.Build;
using triaxis.CommandLine;

namespace MinuteOS.Cli.Commands;

[Command("new", Description = "Create a new minuteos project")]
public class NewCommand : LoggingCommand
{
    [Argument(Description = "Project name (also used as directory name)")]
    public string Name { get; set; } = "";

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var projectDir = Path.GetFullPath(Name);

        if (Directory.Exists(projectDir) && Directory.EnumerateFileSystemEntries(projectDir).Any())
        {
            Logger.LogError("Directory '{Dir}' already exists and is not empty.", projectDir);
            return Task.FromResult(1);
        }

        Logger.LogInformation("Creating project '{Name}' in {Dir}", Name, projectDir);

        // Create directory structure
        var dirs = new[]
        {
            "src",
            "lib/targets/all/base",
            "lib/targets/all/kernel",
            "lib/targets/host",
        };

        foreach (var dir in dirs)
            Directory.CreateDirectory(Path.Combine(projectDir, dir));

        // minuteos.yaml
        File.WriteAllText(Path.Combine(projectDir, ProjectConfig.FileName),
            $$"""
            name: {{Name}}

            defaults:
              target: host
              config: Release
              components:
                - kernel

            configurations:
              release: {}

              debug:
                config: Debug
            """);

        // base component
        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/base/base.h"),
            """
            #pragma once

            #include <stdint.h>
            #include <stddef.h>

            #ifdef __cplusplus
            extern "C" {
            #endif

            void base_init(void);
            uint32_t base_ticks(void);

            #ifdef __cplusplus
            }
            #endif
            """);

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/base/base.cpp"),
            """
            #include "base.h"

            static uint32_t tick_count = 0;

            void base_init(void)
            {
                tick_count = 0;
            }

            uint32_t base_ticks(void)
            {
                return tick_count++;
            }
            """);

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/base/debug.h"),
            "#pragma once\n\n#include <stdio.h>\n\n" +
            "#ifdef DEBUG\n" +
            "#define DBG(fmt, ...) fprintf(stderr, \"[DBG] \" fmt \"\\n\", ##__VA_ARGS__)\n" +
            "#else\n" +
            "#define DBG(fmt, ...) ((void)0)\n" +
            "#endif\n\n" +
            "#define ASSERT(cond) do { \\\n" +
            "    if (!(cond)) { \\\n" +
            "        fprintf(stderr, \"ASSERT FAILED: %s at %s:%d\\n\", #cond, __FILE__, __LINE__); \\\n" +
            "    } \\\n" +
            "} while(0)\n");

        // kernel component
        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/kernel/kernel.h"),
            """
            #pragma once

            #include <base/base.h>
            #include <base/debug.h>

            #ifdef __cplusplus
            extern "C" {
            #endif

            void kernel_init(void);
            void kernel_run(void);
            int kernel_schedule(void (*task)(void*), void* arg);

            #ifdef __cplusplus
            }
            #endif
            """);

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/kernel/kernel.cpp"),
            """
            #include "kernel.h"
            #include <stdio.h>

            #define MAX_TASKS 8

            struct Task {
                void (*fn)(void*);
                void* arg;
            };

            static Task task_queue[MAX_TASKS];
            static int task_count = 0;

            void kernel_init(void)
            {
                base_init();
                task_count = 0;
                DBG("kernel initialized, ticks=%u", base_ticks());
            }

            int kernel_schedule(void (*task)(void*), void* arg)
            {
                if (task_count >= MAX_TASKS)
                    return -1;

                task_queue[task_count].fn = task;
                task_queue[task_count].arg = arg;
                task_count++;
                return 0;
            }

            void kernel_run(void)
            {
                DBG("kernel running %d tasks", task_count);
                for (int i = 0; i < task_count; i++)
                {
                    task_queue[i].fn(task_queue[i].arg);
                }
                task_count = 0;
            }
            """);

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/kernel/Include.mk"),
            "COMPONENTS += base\n");

        // Project source
        File.WriteAllText(Path.Combine(projectDir, "src/main.cpp"),
            $$"""
            #include <kernel/kernel.h>
            #include <stdio.h>

            static void hello_task(void* arg)
            {
                const char* name = static_cast<const char*>(arg);
                printf("Hello from %s (tick=%u)\n", name, base_ticks());
            }

            int main()
            {
                printf("{{Name}} starting\n");

                kernel_init();
                kernel_schedule(hello_task, (void*)"world");
                kernel_run();

                return 0;
            }
            """);

        // .gitignore
        File.WriteAllText(Path.Combine(projectDir, ".gitignore"), "out/\n");

        Logger.LogInformation("");
        Logger.LogInformation("Project created. To build:");
        Logger.LogInformation("  cd {ProjectName}", Name);
        Logger.LogInformation("  minuteos build");
        Logger.LogInformation("  ./out/release/{ProjectName}.elf", Name);

        return Task.FromResult(0);
    }
}
