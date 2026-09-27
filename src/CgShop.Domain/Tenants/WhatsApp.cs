namespace CgShop.Domain.Tenants;

/// <summary>Normalización de números de WhatsApp al formato internacional que usa wa.me (solo dígitos).</summary>
public static class WhatsApp
{
    /// <summary>
    /// Devuelve el número en formato internacional (solo dígitos) o null si no es válido.
    /// Los números dominicanos de 10 dígitos (809/829/849) reciben el código de país 1.
    /// </summary>
    public static string? ToInternational(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;

        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10 && (digits.StartsWith("809") || digits.StartsWith("829") || digits.StartsWith("849")))
            digits = "1" + digits;

        return digits.Length is >= 10 and <= 15 ? digits : null;
    }

    public static bool IsValid(string? number) => ToInternational(number) is not null;

    /// <summary>Enlace de chat con mensaje precargado.</summary>
    public static string? ChatLink(string? number, string message) =>
        ToInternational(number) is { } digits ? $"https://wa.me/{digits}?text={Uri.EscapeDataString(message)}" : null;
}
