namespace CgShop.Domain.Catalog;

public enum ProductCategory
{
    Clothing = 1,
    Caps = 2,
    Watches = 3,
    Perfumes = 4,
    Footwear = 5
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
        _ => category.ToString()
    };

    /// <summary>Qué dimensiones de variante son relevantes por categoría (usado por la UI y validaciones).</summary>
    public static (bool Size, bool Color, bool Volume) VariantDimensions(this ProductCategory category) => category switch
    {
        ProductCategory.Clothing => (true, true, false),
        ProductCategory.Caps => (true, true, false),
        ProductCategory.Watches => (false, true, false),
        ProductCategory.Perfumes => (false, false, true),
        ProductCategory.Footwear => (true, true, false),
        _ => (false, false, false)
    };
}
