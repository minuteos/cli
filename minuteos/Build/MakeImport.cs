using System.Text.RegularExpressions;
using MinuteOS.Cli.Build.Steps;

namespace MinuteOS.Cli.Build;

/// <summary>
/// Parses legacy Make <c>Include.mk</c> files into the tool's native metadata
/// (<see cref="ComponentMeta"/> / <see cref="TargetMeta"/>).
///
/// This is the single place that understands Make syntax. It captures the simple,
/// unambiguous assignments; values that reference Make variables (<c>$(...)</c>)
/// are skipped, with two conventional exceptions: <c>$(&lt;NAME&gt;_DIR)</c> (the
/// directory of the makefile) and <c>$(TEST_FILTERS)</c> (the active test filter).
///
/// When every Include.mk has been migrated to YAML, this class can be deleted.
/// </summary>
public static partial class MakeImport
{
    public const string FileName = "Include.mk";

    /// <summary>
    /// Builds component metadata from a component's Include.mk, or null if absent.
    /// Captures COMPONENTS += (dependencies) and DEFINES +=.
    /// </summary>
    public static ComponentMeta? LoadComponent(string componentDir, string name)
    {
        var path = Path.Combine(componentDir, FileName);
        if (!File.Exists(path))
            return null;

        var content = File.ReadAllText(path);
        var meta = new ComponentMeta { ComponentDir = componentDir, Name = name };

        var requires = MkAppend(content, "COMPONENTS");
        if (requires.Count > 0) meta.Requires = requires;

        var defines = ImportDefines(content);
        if (defines.Count > 0) meta.Defines = defines;

        // Compiler flags go into the toolchain-agnostic settings map.
        var settings = new Dictionary<string, object>();
        var cFlags = MkAppend(content, "C_FLAGS_EXTRA").Where(Static).ToList();
        if (cFlags.Count > 0) settings["gcc.c-flags"] = cFlags;
        var cxxFlags = MkAppend(content, "CXX_FLAGS_EXTRA").Where(Static).ToList();
        if (cxxFlags.Count > 0) settings["gcc.cxx-flags"] = cxxFlags;
        if (settings.Count > 0) meta.Settings = settings;

        var hasContent = meta.Requires != null || meta.Defines != null || meta.Settings != null;
        return hasContent ? meta : null;
    }

    /// <summary>
    /// Builds target metadata from a target's Include.mk, or null if absent.
    /// </summary>
    public static TargetMeta? LoadTarget(string targetDir, string name)
    {
        var path = Path.Combine(targetDir, FileName);
        if (!File.Exists(path))
            return null;

        var content = File.ReadAllText(path);
        var meta = new TargetMeta { TargetDir = targetDir, Name = name };

        var requires = MkAppend(content, "TARGETS");
        if (requires.Count > 0) meta.Requires = requires;

        var components = MkAppend(content, "COMPONENTS");
        if (components.Count > 0) meta.Components = components;

        // Toolchain-specific values go into the generic settings map (gcc.*).
        var settings = new Dictionary<string, object>();

        var prefix = MkAssign(content, "TOOLCHAIN_PREFIX");
        if (prefix != null && Static(prefix)) settings["gcc.toolchain-prefix"] = prefix;

        var ext = MkAssign(content, "PRIMARY_EXT");
        if (ext != null && Static(ext)) settings["gcc.primary-ext"] = ext;

        var ld = MkAssign(content, "LD_SCRIPT");
        if (ld != null && Static(ld)) settings["gcc.ld-script"] = ld;

        var arch = MkAssign(content, "ARCH_FLAGS");
        if (arch != null && Static(arch)) settings["gcc.arch-flags"] = MkTokens(arch).ToList();

        var linkFlags = MkAppend(content, "LINK_FLAGS").Where(Static).ToList();
        if (linkFlags.Count > 0) settings["gcc.link-flags"] = linkFlags;

        var linkDirs = MkAppend(content, "LINK_DIRS")
            .Select(d => Regex.Replace(d, @"\$\(\w+_DIR\)", ""))  // $(NAME_DIR) = target dir
            .Where(Static)
            .ToList();
        if (linkDirs.Count > 0) settings["gcc.link-dirs"] = linkDirs;

        if (settings.Count > 0) meta.Settings = settings;

        var defines = ImportDefines(content);
        if (defines.Count > 0) meta.Defines = defines;

        var steps = ImportObjcopySteps(content);
        var runStep = ImportRunStep(content);   // TEST_RUN -> a Run-phase step
        if (runStep != null) steps.Add(runStep);
        if (steps.Count > 0) meta.Steps = steps;

        var hasContent = meta.Requires != null || meta.Components != null ||
            meta.Settings != null || meta.Defines != null || meta.Steps != null;
        return hasContent ? meta : null;
    }

    private static List<string> ImportDefines(string content) =>
        MkAppend(content, "DEFINES")
            .Where(Static)
            .Select(d => d.Replace("\\\"", "\""))   // Make-escaped quotes -> real quotes
            .ToList();

    /// <summary>
    /// Converts the Make <c>TEST_RUN</c> emulator invocation into a Run-phase
    /// step (the target-overridable replacement for the old test-runner). TEST_RUN
    /// is the command + leading args; the image is appended (e.g.
    /// <c>qemu-system-arm ... -kernel &lt;image&gt;</c>), then TEST_RUN_ARGS.
    /// </summary>
    private static StepReference? ImportRunStep(string content)
    {
        var testRun = MkAssign(content, "TEST_RUN");
        if (testRun == null || !Static(testRun))
            return null;

        var tokens = MkTokens(testRun).ToList();
        if (tokens.Count == 0)
            return null;

        var args = tokens.Skip(1).ToList();
        args.Add("{image}");

        var testRunArgs = MkAssign(content, "TEST_RUN_ARGS");
        if (testRunArgs != null)
        {
            foreach (var t in MkTokens(testRunArgs))
            {
                var tok = t.Trim('"').Replace("$(TEST_FILTERS)", "{filter}");
                if (tok.Contains("$("))
                    continue;
                args.Add(tok);
            }
        }

        // Join into a shell-style args string; quote placeholders (they may expand
        // to a path with spaces) and any token containing whitespace.
        var argString = string.Join(' ', args.Select(a =>
            a.Contains(' ') || a.Contains('{') ? $"\"{a}\"" : a));

        return new StepReference
        {
            Name = "run",
            Phase = BuildPhase.Run,
            Config = new Dictionary<string, string>
            {
                ["command"] = tokens[0],
                ["args"] = argString,
            },
        };
    }

    // Matches the common objcopy output rule + recipe, e.g.
    //   $(OUTPUT).bin: $(PRIMARY_OUTPUT)
    //       $(OBJCOPY) -O binary $< $@
    [GeneratedRegex(@"\$\(OUTPUT\)\.(\S+)\s*:[^\n]*\n\s+\$\(OBJCOPY\)([^\n]+)", RegexOptions.Multiline)]
    private static partial Regex ObjcopyRuleRegex();

    /// <summary>
    /// Converts objcopy conversion rules into first-class <c>gcc:objcopy</c> steps
    /// (e.g. the cortex-m binary/ihex/srec outputs), falling back to a generic
    /// <c>shell</c> step for invocations that don't fit the <c>-O &lt;format&gt;</c> shape.
    /// </summary>
    private static List<StepReference> ImportObjcopySteps(string content)
    {
        var steps = new List<StepReference>();
        foreach (Match m in ObjcopyRuleRegex().Matches(content))
        {
            var ext = m.Groups[1].Value.Trim();
            var tokens = MkTokens(m.Groups[2].Value).ToList();

            // Prefer a gcc:objcopy step: objcopy -O <format> [extra] $< $@
            var oIdx = tokens.IndexOf("-O");
            if (oIdx >= 0 && oIdx + 1 < tokens.Count)
            {
                var format = tokens[oIdx + 1];
                var extra = tokens
                    .Where((t, i) => i != oIdx && i != oIdx + 1 && t != "$<" && t != "$@")
                    .ToList();

                var config = new Dictionary<string, string> { ["format"] = format, ["ext"] = "." + ext };
                if (extra.Count > 0)
                    config["args"] = string.Join(' ', extra);

                steps.Add(new StepReference { Name = "gcc:objcopy", Phase = BuildPhase.PostBuild, Config = config });
            }
            else
            {
                var recipeArgs = m.Groups[2].Value.Trim()
                    .Replace("$<", "{output}")
                    .Replace("$@", "{output-base}." + ext);
                steps.Add(new StepReference
                {
                    Name = "shell",
                    Phase = BuildPhase.PostBuild,
                    Config = new Dictionary<string, string> { ["command"] = "{objcopy} " + recipeArgs },
                });
            }
        }
        return steps;
    }

    // A value is "static" if it has no unresolved Make variable reference.
    private static bool Static(string value) => !value.Contains("$(");

    // VAR += value (across any number of lines), returns all whitespace tokens.
    private static List<string> MkAppend(string content, string variable) =>
        Regex.Matches(content, $@"^{variable}\s*\+=\s*(.+)$", RegexOptions.Multiline)
            .SelectMany(m => MkTokens(m.Groups[1].Value))
            .ToList();

    // VAR = / ?= / := value, returns the last assignment (trimmed), or null.
    private static string? MkAssign(string content, string variable)
    {
        var matches = Regex.Matches(content, $@"^{variable}\s*[?:]?=\s*(.+)$", RegexOptions.Multiline);
        return matches.Count > 0 ? matches[^1].Groups[1].Value.Trim() : null;
    }

    private static IEnumerable<string> MkTokens(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
