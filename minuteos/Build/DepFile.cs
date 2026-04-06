namespace MinuteOS.Cli.Build;

/// <summary>
/// Parses GCC/Clang -MMD dependency files (.d).
///
/// Format (Make syntax):
///   target.o: source.cpp header1.h \
///     header2.h header3.h
///   header1.h:
///   header2.h:
///
/// The -MP flag generates phony targets (lines with just "path:")
/// which we ignore. We only parse the first rule's dependency list.
/// </summary>
public static class DepFile
{
    public static List<string>? Parse(string depFilePath)
    {
        if (!File.Exists(depFilePath))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(depFilePath);
        }
        catch
        {
            return null;
        }

        // First, join continuation lines (backslash + newline → space)
        content = content.Replace("\\\n", " ");

        // Now find the first rule (first line containing ':')
        // Format: "target.o: dep1 dep2 dep3"
        var lines = content.Split('\n');
        string? ruleLine = null;
        foreach (var line in lines)
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0 || line.TrimEnd().Length == 0)
                continue;

            // Check if this is a real rule (has content after ':')
            // vs a phony target (just "path:" with nothing after)
            var afterColon = line[(colonIdx + 1)..].Trim();
            if (afterColon.Length > 0)
            {
                ruleLine = afterColon;
                break;
            }
        }

        if (ruleLine == null)
            return null;

        // Parse space-separated paths (handling escaped spaces)
        var deps = new List<string>();
        var path = new System.Text.StringBuilder();
        var i = 0;

        while (i < ruleLine.Length)
        {
            // Skip whitespace
            while (i < ruleLine.Length && (ruleLine[i] == ' ' || ruleLine[i] == '\t'))
                i++;

            if (i >= ruleLine.Length)
                break;

            path.Clear();
            while (i < ruleLine.Length)
            {
                if (ruleLine[i] == '\\' && i + 1 < ruleLine.Length && ruleLine[i + 1] == ' ')
                {
                    path.Append(' ');
                    i += 2;
                    continue;
                }
                if (ruleLine[i] == ' ' || ruleLine[i] == '\t')
                    break;

                path.Append(ruleLine[i]);
                i++;
            }

            if (path.Length > 0)
                deps.Add(path.ToString());
        }

        return deps.Count > 0 ? deps : null;
    }

    public static string GetDepPath(string objectPath)
    {
        return Path.ChangeExtension(objectPath, ".d");
    }
}
