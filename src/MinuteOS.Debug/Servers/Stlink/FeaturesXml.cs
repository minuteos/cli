using System.Text;

namespace MinuteOS.Debug.Servers.Stlink;

/// <summary>
/// Builds the GDB target description (<c>target.xml</c>) from a target's
/// register metadata - the port of <c>gdb-server/features.ts</c>. Registers are
/// grouped into features; the bitfield type definitions are declared in each
/// feature (as the extension did).
/// </summary>
internal static class FeaturesXml
{
    public static byte[] Generate(IDebugTarget target)
    {
        var regs = target.RegisterInfo
            .Select((r, i) => (Index: i, Info: r))
            .Where(x => x.Info != null)
            .Select(x => (x.Index, Info: x.Info!))
            .ToList();

        var features = regs.GroupBy(x => x.Info.GdbFeature);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n<!DOCTYPE target SYSTEM \"gdb-target.dtd\">\n");
        sb.Append("<target version=\"1.0\">\n");
        sb.Append("  <architecture>").Append(target.Info.Architecture).Append("</architecture>\n");
        sb.Append("  <osabi>none</osabi>\n");

        foreach (var feature in features)
        {
            sb.Append("  <feature name=\"").Append(Escape(feature.Key)).Append("\">\n");

            foreach (var type in target.RegisterTypes)
                AppendType(sb, type);

            foreach (var (index, info) in feature)
            {
                sb.Append("    <reg name=\"").Append(Escape(info.Name))
                    .Append("\" bitsize=\"").Append(info.Bits)
                    .Append("\" regnum=\"").Append(index)
                    .Append("\" type=\"").Append(Escape(info.Type ?? "int"))
                    .Append("\" group=\"").Append(Escape(info.Group)).Append("\"/>\n");
            }

            sb.Append("  </feature>\n");
        }

        sb.Append("</target>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendType(StringBuilder sb, RegisterTypeInfo type)
    {
        var size = type.Size is { } s ? $" size=\"{s}\"" : "";
        sb.Append("    <").Append(type.Kind).Append(" id=\"").Append(Escape(type.Id)).Append('"').Append(size).Append(">\n");
        foreach (var field in type.Fields)
        {
            sb.Append("      <field name=\"").Append(Escape(field.Name)).Append('"');
            var fieldType = field.Type ?? (field.BitHi > field.BitLo ? "uint" : field.BitLo != null ? "bool" : null);
            if (fieldType != null)
                sb.Append(" type=\"").Append(Escape(fieldType)).Append('"');
            if (field.BitLo is { } lo)
                sb.Append(" start=\"").Append(lo).Append("\" end=\"").Append(field.BitHi ?? lo).Append('"');
            sb.Append("/>\n");
        }
        sb.Append("    </").Append(type.Kind).Append(">\n");
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
