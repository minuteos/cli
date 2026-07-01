namespace MinuteOS.Build;

/// <summary>
/// The resolved build configuration a step reads: names, the output layout, source
/// discovery roots, the precompiled header, and the settings bag. The concrete
/// resolution (parsing project/target/component metadata) lives in MinuteOS.Build;
/// steps depend only on this contract.
/// </summary>
public interface IBuildConfiguration
{
    /// <summary>Configuration name (e.g. "host", "qemu").</summary>
    string Name { get; }

    /// <summary>Build config: Debug / Release / Trace.</summary>
    string Config { get; }

    /// <summary>The project root directory.</summary>
    string ProjectRoot { get; }

    /// <summary>The primary source directory (usually the project's src/).</summary>
    string SourceDir { get; }

    /// <summary>Root output dir for this configuration (out/&lt;name&gt;/...).</summary>
    string OutputRoot { get; }

    /// <summary>Where object files are written.</summary>
    string ObjectDir { get; }

    /// <summary>The output artifact name (without extension).</summary>
    string OutputName { get; }

    /// <summary>The primary linked output path (e.g. out/&lt;name&gt;/app.elf).</summary>
    string PrimaryOutput { get; }

    /// <summary>The primary output extension (.elf, .axf, ...).</summary>
    string PrimaryExt { get; }

    /// <summary>The precompiled header source, if any.</summary>
    string? Pch { get; }

    /// <summary>Where the compiled PCH (.gch) is written.</summary>
    string PchGchFile { get; }

    /// <summary>The <c>-include</c> base for the PCH.</summary>
    string PchIncludeBase { get; }

    /// <summary>Directories scanned for sources.</summary>
    IReadOnlyList<string> SourceDirs { get; }

    /// <summary>Resolved target directories (most-specific first).</summary>
    IReadOnlyList<string> TargetDirs { get; }

    /// <summary>Resolved component directories.</summary>
    IReadOnlyList<string> ComponentDirs { get; }

    /// <summary>The aggregated, toolchain-agnostic settings bag.</summary>
    Settings Settings { get; }
}
