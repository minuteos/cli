using System.Text.RegularExpressions;

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
public static class MakeImport
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

        var cFlags = MkAppend(content, "C_FLAGS_EXTRA").Where(Static).ToList();
        if (cFlags.Count > 0) meta.CFlags = cFlags;
        var cxxFlags = MkAppend(content, "CXX_FLAGS_EXTRA").Where(Static).ToList();
        if (cxxFlags.Count > 0) meta.CxxFlags = cxxFlags;

        var hasContent = meta.Requires != null || meta.Defines != null ||
            meta.CFlags != null || meta.CxxFlags != null;
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

        var prefix = MkAssign(content, "TOOLCHAIN_PREFIX");
        if (prefix != null && Static(prefix)) meta.ToolchainPrefix = prefix;

        var ext = MkAssign(content, "PRIMARY_EXT");
        if (ext != null && Static(ext)) meta.PrimaryExt = ext;

        var ld = MkAssign(content, "LD_SCRIPT");
        if (ld != null && Static(ld)) meta.LdScript = ld;

        var arch = MkAssign(content, "ARCH_FLAGS");
        if (arch != null && Static(arch)) meta.ArchFlags = MkTokens(arch).ToList();

        var linkFlags = MkAppend(content, "LINK_FLAGS").Where(Static).ToList();
        if (linkFlags.Count > 0) meta.LinkFlags = linkFlags;

        var linkDirs = MkAppend(content, "LINK_DIRS")
            .Select(d => Regex.Replace(d, @"\$\(\w+_DIR\)", ""))  // $(NAME_DIR) = target dir
            .Where(Static)
            .ToList();
        if (linkDirs.Count > 0) meta.LinkDirs = linkDirs;

        var defines = ImportDefines(content);
        if (defines.Count > 0) meta.Defines = defines;

        meta.TestRunner = ImportTestRunner(content);

        var hasContent = meta.Requires != null || meta.Components != null ||
            meta.ToolchainPrefix != null || meta.PrimaryExt != null ||
            meta.LdScript != null || meta.ArchFlags != null || meta.LinkFlags != null ||
            meta.LinkDirs != null || meta.Defines != null || meta.TestRunner != null;
        return hasContent ? meta : null;
    }

    private static List<string> ImportDefines(string content) =>
        MkAppend(content, "DEFINES")
            .Where(Static)
            .Select(d => d.Replace("\\\"", "\""))   // Make-escaped quotes -> real quotes
            .ToList();

    private static TestRunnerConfig? ImportTestRunner(string content)
    {
        // TEST_RUN is the emulator invocation; the test binary is appended after
        // it (e.g. `qemu-system-arm ... -kernel <binary>`), then TEST_RUN_ARGS.
        var testRun = MkAssign(content, "TEST_RUN");
        if (testRun == null || !Static(testRun))
            return null;

        var tokens = MkTokens(testRun).ToList();
        if (tokens.Count == 0)
            return null;

        var args = tokens.Skip(1).ToList();
        args.Add("{binary}");

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

        return new TestRunnerConfig { Command = tokens[0], Args = args };
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
