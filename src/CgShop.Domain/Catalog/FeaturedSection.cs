namespace CgShop.Domain.Catalog;

/// <summary>Secciones del inicio donde el propietario puede destacar productos a mano.</summary>
public enum FeaturedSection
{
    Trending = 1,
    NewArrivals = 2
}

public static class FeaturedSectionExtensions
{
    /// <summary>Máximo de productos destacados por sección (el resto lo completa el cálculo automático).</summary>
    public const int MaxPerSection = 12;

    public static string DisplayName(this FeaturedSection section) => section switch
    {
        FeaturedSection.Trending => "Productos en tendencia",
        FeaturedSection.NewArrivals => "Novedades",
        _ => section.ToString()
    };
}
