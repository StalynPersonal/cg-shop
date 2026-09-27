namespace CgShop.Domain.Catalog;

/// <summary>Público al que va dirigido el producto (Hombre, Mujer, Niños o Unisex).</summary>
public enum ProductAudience
{
    Unisex = 0,
    Men = 1,
    Women = 2,
    Kids = 3
}

public static class ProductAudienceExtensions
{
    public static string DisplayName(this ProductAudience audience) => audience switch
    {
        ProductAudience.Men => "Hombre",
        ProductAudience.Women => "Mujer",
        ProductAudience.Kids => "Niños",
        _ => "Unisex"
    };

    /// <summary>Segmento de URL: /hombre, /mujer, /ninos.</summary>
    public static string Slug(this ProductAudience audience) => audience switch
    {
        ProductAudience.Men => "hombre",
        ProductAudience.Women => "mujer",
        ProductAudience.Kids => "ninos",
        _ => "unisex"
    };

    public static ProductAudience? FromSlug(string? slug) =>
        Enum.GetValues<ProductAudience>().Cast<ProductAudience?>()
            .FirstOrDefault(a => string.Equals(a!.Value.Slug(), slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Públicos que se muestran al navegar una sección: lo unisex aparece en Hombre y en Mujer; Niños es exclusivo.
    /// </summary>
    public static ProductAudience[] Includes(this ProductAudience audience) => audience switch
    {
        ProductAudience.Men => [ProductAudience.Men, ProductAudience.Unisex],
        ProductAudience.Women => [ProductAudience.Women, ProductAudience.Unisex],
        _ => [audience]
    };
}
