using System.Globalization;
using System.Text.Json.Nodes;

namespace MinuteOS.Debug.Usb;

/// <summary>Parses USB <c>vid</c>/<c>pid</c> launch-config values.</summary>
public static class UsbIds
{
    /// <summary>A JSON number or a hex (<c>0x...</c>)/decimal string; null when absent or unparseable.</summary>
    public static ushort? Parse(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return (ushort)node.GetValue<int>();
        }
        catch
        {
            var text = node.ToString().Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ushort.TryParse(hex ? text[2..] : text,
                hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : null;
        }
    }
}
