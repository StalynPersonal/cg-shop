using System.Globalization;
using System.Text.RegularExpressions;

namespace CgShop.Domain.Tenants;

public static partial class HexColor
{
    [GeneratedRegex("^#([0-9a-fA-F]{6})$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value.Trim());

    public static string Normalize(string value) => value.Trim().ToUpperInvariant();

    /// <summary>Luminancia relativa WCAG (0 = negro, 1 = blanco).</summary>
    public static double RelativeLuminance(string hex)
    {
        static double Channel(string hex, int start)
        {
            var c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);
    }

    /// <summary>Devuelve blanco o negro según cuál contraste mejor sobre el color dado.</summary>
    public static string ContrastText(string hex)
    {
        var l = RelativeLuminance(hex);
        var contrastWithWhite = 1.05 / (l + 0.05);
        var contrastWithBlack = (l + 0.05) / 0.05;
        return contrastWithWhite >= contrastWithBlack ? "#FFFFFF" : "#000000";
    }
}
