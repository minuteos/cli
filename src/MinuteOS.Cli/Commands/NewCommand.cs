using MinuteOS.Build;
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
            "lib/targets/all/base/tests/sanity",
            "lib/targets/all/kernel",
            "lib/targets/all/testrunner",
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

              # Example emulator configuration (requires an ARM target + qemu):
              # qemu:
              #   target: cortex-m3
              #   test-runner:
              #     command: qemu-system-arm
              #     args: [-machine, lm3s6965evb, -nographic, -semihosting, -kernel, "{binary}"]
              #     timeout: 60
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

        // testrunner component - a minimal host test framework
        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/testrunner/component.yaml"),
            "# Minimal test framework. Provides main() and the TEST_CASE/CHECK macros.\n");

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/testrunner/testrunner.h"),
            """
            #pragma once

            #include <cstdio>
            #include <cstring>

            namespace testrunner {

            typedef void (*TestFn)();

            struct TestCase {
                const char* name;
                TestFn fn;
            };

            // Records a failure for the currently running test.
            void report_failure(const char* expr, const char* file, int line);

            } // namespace testrunner

            // Test cases are emitted as const descriptors into a dedicated linker
            // section; main() walks that section directly. This avoids static
            // constructors / .init_array, which are unreliable in a minimal
            // bare-metal C++ runtime.
            #define TEST_CASE(name) \
                static void test_##name(); \
                __attribute__((section("test_cases"), used)) \
                static const ::testrunner::TestCase tc_##name = { #name, test_##name }; \
                static void test_##name()

            #define CHECK(expr) \
                do { if (!(expr)) { ::testrunner::report_failure(#expr, __FILE__, __LINE__); return; } } while (0)

            #define CHECK_EQ(a, b) CHECK((a) == (b))
            """);

        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/testrunner/main.cpp"),
            """
            #include "testrunner.h"

            // Linker-provided bounds of the "test_cases" section. Every TEST_CASE
            // descriptor lands between them - no runtime registration needed.
            extern "C" {
            extern const testrunner::TestCase __start_test_cases[];
            extern const testrunner::TestCase __stop_test_cases[];
            }

            namespace testrunner {

            static bool g_failed = false;
            static char g_detail[256];

            void report_failure(const char* expr, const char* file, int line) {
                if (!g_failed) {
                    g_failed = true;
                    snprintf(g_detail, sizeof(g_detail), "%s at %s:%d", expr, file, line);
                }
            }

            } // namespace testrunner

            int main(int argc, char** argv) {
                using namespace testrunner;
                const char* filter = argc > 1 ? argv[1] : nullptr;
                int total = 0, passed = 0, failed = 0;

                for (const TestCase* tc = __start_test_cases; tc < __stop_test_cases; tc++) {
                    if (filter && strstr(tc->name, filter) == nullptr) continue;
                    total++;
                    g_failed = false;
                    tc->fn();
                    if (g_failed) {
                        failed++;
                        printf("##TEST## %s FAIL %s\n", tc->name, g_detail);
                    } else {
                        passed++;
                        printf("##TEST## %s PASS\n", tc->name);
                    }
                }

                printf("##SUMMARY## total=%d passed=%d failed=%d\n", total, passed, failed);
                return failed == 0 ? 0 : 1;
            }
            """);

        // Sample test suite for the base component
        File.WriteAllText(Path.Combine(projectDir, "lib/targets/all/base/tests/sanity/sanity.cpp"),
            """
            #include <testrunner/testrunner.h>
            #include <base/base.h>

            TEST_CASE(ticks_start_at_zero)
            {
                base_init();
                CHECK(base_ticks() == 0);
            }

            TEST_CASE(ticks_increment)
            {
                base_init();
                uint32_t a = base_ticks();
                uint32_t b = base_ticks();
                CHECK_EQ(b, a + 1);
            }
            """);

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
        Logger.LogInformation("Project created. To build and test:");
        Logger.LogInformation("  cd {ProjectName}", Name);
        Logger.LogInformation("  minuteos build");
        Logger.LogInformation("  ./out/release/{ProjectName}.elf", Name);
        Logger.LogInformation("  minuteos test");

        return Task.FromResult(0);
    }
}
