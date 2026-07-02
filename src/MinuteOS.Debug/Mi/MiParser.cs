using System.Text;
using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Mi;

public enum MiRecordType
{
    /// <summary>^ command result</summary>
    Result,
    /// <summary>* exec async (stopped/running)</summary>
    Exec,
    /// <summary>+ status async (progress)</summary>
    Status,
    /// <summary>= notify async (thread-created, ...)</summary>
    Notify,
    /// <summary>~ console stream</summary>
    ConsoleStream,
    /// <summary>@ target stream</summary>
    TargetStream,
    /// <summary>&amp; log stream</summary>
    LogStream,
    /// <summary>(gdb) prompt - gdb is idle</summary>
    Prompt,
}

/// <summary>One parsed GDB/MI output record.</summary>
public sealed record MiRecord(MiRecordType Type, int? Token, string? Class, JsonObject? Results, string? Text);

/// <summary>
/// Parser for GDB/MI output records - a port of the minute-debug extension's
/// <c>mi.ts</c>. Values become JSON nodes; result names stay kebab-case;
/// numeric-looking constants become numbers; list elements that are named
/// results keep their name as <c>$type</c>.
/// </summary>
public static class MiParser
{
    public static MiRecord? Parse(string line)
    {
        if (line == "(gdb) " || line == "(gdb)")
            return new MiRecord(MiRecordType.Prompt, null, null, null, null);

        var pos = 0;

        // Token: leading digits.
        var tokenStart = pos;
        while (pos < line.Length && line[pos] is >= '0' and <= '9')
            pos++;
        int? token = pos > tokenStart ? int.Parse(line[tokenStart..pos]) : null;

        if (pos >= line.Length)
            return null;

        var type = line[pos++];
        switch (type)
        {
            case '~' or '@' or '&':
            {
                var text = ParseCString(line, ref pos) ?? "";
                return new MiRecord(type switch
                {
                    '~' => MiRecordType.ConsoleStream,
                    '@' => MiRecordType.TargetStream,
                    _ => MiRecordType.LogStream,
                }, token, null, null, text);
            }

            case '^' or '*' or '+' or '=':
            {
                // Class runs to the first comma.
                var comma = line.IndexOf(',', pos);
                var cls = comma < 0 ? line[pos..] : line[pos..comma];
                pos = comma < 0 ? line.Length : comma;

                var results = new JsonObject();
                while (pos < line.Length && line[pos] == ',')
                {
                    pos++;
                    // Either a bare value (rare) or name=value.
                    if (pos < line.Length && (line[pos] == '{' || line[pos] == '[' || line[pos] == '"'))
                    {
                        var value = ParseValue(line, ref pos);
                        // Single unnamed tuple: merge into the results (mi.ts behavior).
                        if (value is JsonObject obj && pos >= line.Length && results.Count == 0)
                        {
                            foreach (var (k, v) in obj.ToList())
                            {
                                obj.Remove(k);
                                results[k] = v;
                            }
                        }
                        else
                        {
                            var extra = results["$results"] as JsonArray ?? (JsonArray)(results["$results"] = new JsonArray());
                            extra.Add(value);
                        }
                    }
                    else
                    {
                        var (name, value) = ParseResult(line, ref pos);
                        if (name != null)
                            results[name] = value;
                    }
                }

                return new MiRecord(type switch
                {
                    '^' => MiRecordType.Result,
                    '*' => MiRecordType.Exec,
                    '+' => MiRecordType.Status,
                    _ => MiRecordType.Notify,
                }, token, cls, results, null);
            }
        }

        return null;
    }

    private static (string? Name, JsonNode? Value) ParseResult(string line, ref int pos)
    {
        var eq = line.IndexOf('=', pos);
        if (eq < 0)
        {
            pos = line.Length;
            return (null, null);
        }
        var name = line[pos..eq];
        pos = eq + 1;
        return (name, ParseValue(line, ref pos));
    }

    private static JsonNode? ParseValue(string line, ref int pos)
    {
        if (pos >= line.Length)
            return null;
        return line[pos] switch
        {
            '[' => ParseList(line, ref pos),
            '{' => ParseTuple(line, ref pos),
            '"' => ParseConstant(line, ref pos),
            _ => null,
        };
    }

    private static JsonArray ParseList(string line, ref int pos)
    {
        var result = new JsonArray();
        pos++; // [
        while (pos < line.Length && line[pos] != ']')
        {
            if (line[pos] == ',')
            {
                pos++;
                continue;
            }
            if (line[pos] is '{' or '[' or '"')
            {
                result.Add(ParseValue(line, ref pos));
            }
            else
            {
                // Named result inside a list: keep the name as $type.
                var (name, value) = ParseResult(line, ref pos);
                if (name != null)
                {
                    if (value is JsonObject obj)
                        obj["$type"] = name;
                    result.Add(value);
                }
            }
        }
        if (pos < line.Length)
            pos++; // ]
        return result;
    }

    private static JsonObject ParseTuple(string line, ref int pos)
    {
        var result = new JsonObject();
        pos++; // {
        while (pos < line.Length && line[pos] != '}')
        {
            if (line[pos] == ',')
            {
                pos++;
                continue;
            }
            var (name, value) = ParseResult(line, ref pos);
            if (name == null)
                break;
            result[name] = value;
        }
        if (pos < line.Length)
            pos++; // }
        return result;
    }

    private static JsonNode? ParseConstant(string line, ref int pos)
    {
        var s = ParseCString(line, ref pos);
        if (s == null)
            return null;
        // Numeric-looking constants become numbers (mi.ts behavior).
        return double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var n)
               && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == s
            ? JsonValue.Create(n)
            : JsonValue.Create(s);
    }

    /// <summary>Parses a gdb C-string: backslash escapes incl. octal.</summary>
    private static string? ParseCString(string line, ref int pos)
    {
        if (pos >= line.Length || line[pos] != '"')
            return null;
        pos++;
        var sb = new StringBuilder();
        while (pos < line.Length)
        {
            var c = line[pos++];
            if (c == '"')
                return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (pos >= line.Length)
                break;
            var e = line[pos++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                default:
                    if (e is >= '0' and <= '7')
                    {
                        // Octal escape, up to 3 digits.
                        var value = e - '0';
                        for (var i = 0; i < 2 && pos < line.Length && line[pos] is >= '0' and <= '7'; i++)
                            value = value * 8 + (line[pos++] - '0');
                        sb.Append((char)value);
                    }
                    else
                    {
                        sb.Append(e);
                    }
                    break;
            }
        }
        return sb.ToString(); // unterminated - return what we have
    }
}
