namespace MinuteOS.Cli.Build;

/// <summary>
/// Collects source files from source directories matching the configured extensions.
/// Mirrors the source collection logic from Base.mk.
/// </summary>
public class SourceCollector
{
    public static readonly string[] DefaultExtensions = [".c", ".cpp", ".S"];

    private readonly string[] _extensions;

    public SourceCollector(string[]? extensions = null)
    {
        _extensions = extensions ?? DefaultExtensions;
    }

    /// <summary>
    /// Collects all source files from the given directories.
    /// </summary>
    public List<SourceFile> CollectSources(IEnumerable<string> sourceDirs, string projectRoot)
    {
        var sources = new List<SourceFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in sourceDirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var ext in _extensions)
            {
                foreach (var file in Directory.EnumerateFiles(dir, $"*{ext}"))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath))
                        continue;

                    var relativePath = Path.GetRelativePath(projectRoot, fullPath);
                    var language = ext switch
                    {
                        ".c" => SourceLanguage.C,
                        ".cpp" => SourceLanguage.Cpp,
                        ".S" => SourceLanguage.Assembly,
                        _ => SourceLanguage.C
                    };

                    sources.Add(new SourceFile(fullPath, relativePath, language));
                }
            }
        }

        return sources;
    }
}

public record SourceFile(string FullPath, string RelativePath, SourceLanguage Language);

public enum SourceLanguage
{
    C,
    Cpp,
    Assembly
}
