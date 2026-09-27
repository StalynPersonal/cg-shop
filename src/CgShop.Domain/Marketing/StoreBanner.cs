using CgShop.Domain.Common;

namespace CgShop.Domain.Marketing;

/// <summary>Imagen del carrusel de portada de la tienda, con texto y enlace opcionales.</summary>
public sealed class StoreBanner : Entity, ITenantEntity
{
    public const int MaxPerStore = 10;
    public const int TitleMax = 80, SubtitleMax = 160, ButtonMax = 30, LinkMax = 300;

    private StoreBanner() { }

    public Guid TenantId { get; set; }
    public string StoragePath { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? Title { get; private set; }
    public string? Subtitle { get; private set; }
    public string? ButtonText { get; private set; }

    /// <summary>Ruta interna de la tienda ("/seccion/mujer") o enlace https.</summary>
    public string? LinkUrl { get; private set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedAtUtc { get; private set; }

    public static StoreBanner Create(string storagePath, string contentType, long sizeBytes, int width, int height,
        int sortOrder, DateTime nowUtc) => new()
    {
        StoragePath = storagePath,
        ContentType = contentType,
        SizeBytes = sizeBytes,
        Width = width,
        Height = height,
        SortOrder = sortOrder,
        CreatedAtUtc = nowUtc
    };

    public void UpdateContent(string? title, string? subtitle, string? buttonText, string? linkUrl, bool isActive)
    {
        Title = Clean(title, TitleMax, "El título");
        Subtitle = Clean(subtitle, SubtitleMax, "El texto");
        ButtonText = Clean(buttonText, ButtonMax, "El texto del botón");
        LinkUrl = CleanLink(linkUrl);
        if (ButtonText is not null && LinkUrl is null)
            throw new DomainException("Para mostrar un botón indique a dónde lleva (enlace).");
        IsActive = isActive;
    }

    private static string? Clean(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length > max ? throw new DomainException($"{field} admite hasta {max} caracteres.") : trimmed;
    }

    /// <summary>Solo rutas internas ("/catalogo/ropa") o https: evita enlaces javascript: o a sitios sin cifrar.</summary>
    private static string? CleanLink(string? value)
    {
        var link = Clean(value, LinkMax, "El enlace");
        if (link is null)
            return null;
        if (link.StartsWith('/') && !link.StartsWith("//"))
            return link;
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return uri.ToString();
        throw new DomainException("El enlace debe ser una ruta de la tienda (por ejemplo /seccion/mujer) o empezar con https://.");
    }
}
