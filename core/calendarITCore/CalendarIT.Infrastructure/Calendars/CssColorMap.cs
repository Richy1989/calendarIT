using System.Globalization;

namespace CalendarIT.Infrastructure.Calendars;

/// <summary>
/// Maps between hex colors (stored) and CSS3 color names (what the iCalendar COLOR
/// property, RFC 7986, requires). Export snaps a hex to the nearest named color; import
/// resolves a name back to hex. A representative subset of CSS3 names is enough for a
/// good nearest-match and covers the app's default swatches exactly.
/// </summary>
public static class CssColorMap
{
    private static readonly (string Name, byte R, byte G, byte B)[] Colors =
    [
        ("black", 0, 0, 0), ("white", 255, 255, 255), ("gray", 128, 128, 128),
        ("silver", 192, 192, 192), ("lightgray", 211, 211, 211), ("dimgray", 105, 105, 105),
        ("slategray", 112, 128, 144),
        ("red", 255, 0, 0), ("crimson", 220, 20, 60), ("tomato", 255, 99, 71),
        ("orangered", 255, 69, 0), ("firebrick", 178, 34, 34), ("indianred", 205, 92, 92),
        ("orange", 255, 165, 0), ("darkorange", 255, 140, 0), ("goldenrod", 218, 165, 32),
        ("gold", 255, 215, 0), ("yellow", 255, 255, 0), ("khaki", 240, 230, 140),
        ("green", 0, 128, 0), ("limegreen", 50, 205, 50), ("mediumseagreen", 60, 179, 113),
        ("seagreen", 46, 139, 87), ("forestgreen", 34, 139, 34), ("olivedrab", 107, 142, 35),
        ("lightgreen", 144, 238, 144),
        ("teal", 0, 128, 128), ("turquoise", 64, 224, 208), ("mediumturquoise", 72, 209, 204),
        ("darkcyan", 0, 139, 139), ("cyan", 0, 255, 255),
        ("blue", 0, 0, 255), ("royalblue", 65, 105, 225), ("cornflowerblue", 100, 149, 237),
        ("steelblue", 70, 130, 180), ("dodgerblue", 30, 144, 255), ("navy", 0, 0, 128),
        ("slateblue", 106, 90, 205), ("mediumslateblue", 123, 104, 238),
        ("purple", 128, 0, 128), ("indigo", 75, 0, 130), ("darkviolet", 148, 0, 211),
        ("mediumpurple", 147, 112, 219), ("violet", 238, 130, 238), ("orchid", 218, 112, 214),
        ("pink", 255, 192, 203), ("hotpink", 255, 105, 180), ("palevioletred", 219, 112, 147),
        ("deeppink", 255, 20, 147),
        ("brown", 165, 42, 42), ("chocolate", 210, 105, 30), ("sienna", 160, 82, 45),
        ("tan", 210, 180, 140),
    ];

    private static readonly Dictionary<string, string> NameToHex =
        Colors.ToDictionary(c => c.Name, c => $"#{c.R:X2}{c.G:X2}{c.B:X2}", StringComparer.OrdinalIgnoreCase);

    /// <summary>Nearest CSS3 color name for a hex value (Euclidean RGB distance).</summary>
    public static string? ToNearestName(string? hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b))
        {
            return null;
        }

        var best = Colors[0];
        var bestDist = int.MaxValue;
        foreach (var c in Colors)
        {
            var d = (c.R - r) * (c.R - r) + (c.G - g) * (c.G - g) + (c.B - b) * (c.B - b);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best.Name;
    }

    /// <summary>Hex for a CSS3 color name; if the value is already a hex string, returns it.</summary>
    public static string? ToHex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        if (name.StartsWith('#'))
        {
            return name;
        }
        return NameToHex.TryGetValue(name.Trim(), out var hex) ? hex : null;
    }

    private static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }
        var h = hex.TrimStart('#');
        if (h.Length == 3)
        {
            h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        }
        if (h.Length != 6)
        {
            return false;
        }
        return byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
