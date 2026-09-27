namespace CgShop.Domain.Catalog;

public enum ProductCategory
{
    Clothing = 1,
    Caps = 2,
    Watches = 3,
    Perfumes = 4,
    Footwear = 5,
    Phones = 6,

    /// <summary>Audífonos, AirPods, cargadores, bocinas, relojes inteligentes y accesorios.</summary>
    Electronics = 7
}

public static class ProductCategoryExtensions
{
    public static string DisplayName(this ProductCategory category) => category switch
    {
        ProductCategory.Clothing => "Ropa",
        ProductCategory.Caps => "Gorras",
        ProductCategory.Watches => "Relojes",
        ProductCategory.Perfumes => "Perfumes",
        ProductCategory.Footwear => "Calzados",
        ProductCategory.Phones => "Celulares",
        ProductCategory.Electronics => "Electrónica",
        _ => category.ToString()
    };

    /// <summary>Nombre de la dimensión "talla" según la categoría: en celulares es la capacidad.</summary>
    public static string SizeLabel(this ProductCategory category) =>
        category == ProductCategory.Phones ? "Capacidad" : "Talla";

    /// <summary>
    /// Dos categorías son compatibles si sus variantes usan las mismas dimensiones (p. ej. Ropa, Gorras y Calzados:
    /// talla + color). Entre compatibles, las variantes se conservan al cambiar la categoría.
    /// </summary>
    public static bool HasSameVariantsAs(this ProductCategory category, ProductCategory other) =>
        category.VariantDimensions() == other.VariantDimensions();

    /// <summary>Qué dimensiones de variante son relevantes por categoría (usado por la UI y validaciones).</summary>
    public static (bool Size, bool Color, bool Volume) VariantDimensions(this ProductCategory category) => category switch
    {
        ProductCategory.Clothing => (true, true, false),
        ProductCategory.Caps => (true, true, false),
        ProductCategory.Watches => (false, true, false),
        ProductCategory.Perfumes => (false, false, true),
        ProductCategory.Footwear => (true, true, false),
        ProductCategory.Phones => (true, true, false),      // "talla" = capacidad (128 GB...)
        ProductCategory.Electronics => (false, true, false),
        _ => (false, false, false)
    };
}
