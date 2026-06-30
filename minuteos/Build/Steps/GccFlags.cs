namespace MinuteOS.Cli.Build.Steps;

/// <summary>
/// Builds GCC compile flags from the toolchain-agnostic <see cref="Settings"/> bag
/// (not from typed configuration fields). Shared by source compiles and the PCH so
/// they stay in lockstep. Only structural facts (source dir, optimization config,
/// step-contributed defines/includes) come from outside the bag.
/// </summary>
internal static class GccFlags
{
    /// <summary>Adapter for the legacy phase-based step (reads contributions from State).</summary>
    public static void AppendCompileFlags(
        List<string> args, StepContext ctx, string sourceDir, SourceLanguage language) =>
        AppendCompileFlags(args, ctx.Configuration.Settings, ctx.Configuration,
            ctx.State.ExtraDefines, ctx.State.ExtraIncludeDirs, sourceDir, language);

    /// <summary>
    /// Builds compile flags from the settings bag plus step-contributed extras.
    /// Pure over its inputs so the graph compile step and the legacy step produce
    /// byte-identical command lines.
    /// </summary>
    public static void AppendCompileFlags(
        List<string> args, Settings s, BuildConfiguration config,
        IEnumerable<string> extraDefines, IEnumerable<string> extraIncludeDirs,
        string sourceDir, SourceLanguage language)
    {
        // Defines: aggregated bag + step-contributed (e.g. git-version).
        foreach (var define in s.List("defines"))
            args.AddRange(["-D", define]);
        foreach (var define in extraDefines)
            args.AddRange(["-D", define]);

        // Includes: aggregated bag + step-contributed (e.g. a transform's headers).
        foreach (var inc in s.List("include-dirs"))
            args.AddRange(["-I", inc]);
        foreach (var inc in extraIncludeDirs)
            args.AddRange(["-I", inc]);

        var privateDir = Path.Combine(sourceDir, "private");
        if (Directory.Exists(privateDir))
            args.AddRange(["-I", privateDir]);

        args.AddRange(["-MMD", "-MP"]);

        args.AddRange(s.List("gcc.arch-flags"));

        args.AddRange(["-g", "-Wall", "-fmessage-length=0", "-fno-exceptions", "-fdata-sections", "-ffunction-sections"]);

        if (config.Config == "Debug")
            args.Add("-O0");
        else
            args.AddRange(["-O3", "-Os"]);

        if (language == SourceLanguage.C)
        {
            args.Add("-std=gnu11");
            args.AddRange(s.List("gcc.c-flags"));
        }
        else if (language == SourceLanguage.Cpp)
        {
            args.AddRange(["-std=gnu++17", "-fno-rtti", "-fno-threadsafe-statics", "-fno-use-cxa-atexit"]);
            args.AddRange(s.List("gcc.cxx-flags"));
        }
        // Assembly (.S): no -std / language flags.
    }
}
