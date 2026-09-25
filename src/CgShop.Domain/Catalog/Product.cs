using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

public sealed class Product : Entity, ITenantEntity
{
    private readonly List<ProductVariant> _variants = [];

    private Product() { }

    public Guid TenantId { get; set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string? Description { get; private set; }
    public string? Brand { get; private set; }
    public ProductCategory Category { get; private set; }
    public string? ImageUrl { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Atributos específicos de la categoría (notas olfativas, material, tipo de movimiento...).
    /// Se persiste como columna JSON.
    /// </summary>
    public Dictionary<string, string> Attributes { get; private set; } = [];

    public IReadOnlyList<ProductVariant> Variants => _variants;

    public decimal? MinPrice => _variants.Count == 0 ? null : _variants.Min(v => v.Price);
    public int TotalAvailable => _variants.Sum(v => v.Available);

    public static Product Create(string name, ProductCategory category, string? brand = null,
        string? description = null, string? imageUrl = null, IDictionary<string, string>? attributes = null)
    {
        var product = new Product { Category = category };
        product.Update(name, brand, description, imageUrl, attributes);
        return product;
    }

    public void Update(string name, string? brand, string? description, string? imageUrl,
        IDictionary<string, string>? attributes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("El nombre del producto es obligatorio.");
        if (!Enum.IsDefined(Category))
            throw new DomainException("Categoría inválida.");

        Name = name.Trim();
        Slug = Slugify(Name);
        Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        Attributes = attributes is null ? [] : new Dictionary<string, string>(attributes);
    }

    public ProductVariant AddVariant(string sku, decimal price, int initialStock, string? size = null,
        string? color = null, int? volumeMl = null)
    {
        var normalizedSku = sku?.Trim().ToUpperInvariant() ?? "";
        if (_variants.Any(v => v.Sku == normalizedSku))
            throw new DomainException($"El SKU '{normalizedSku}' ya existe en este producto.");

        var (needsSize, _, needsVolume) = Category.VariantDimensions();
        if (needsSize && string.IsNullOrWhiteSpace(size))
            throw new DomainException($"Los productos de {Category.DisplayName()} requieren talla.");
        if (needsVolume && volumeMl is null or <= 0)
            throw new DomainException("Los perfumes requieren volumen (ml) mayor que cero.");

        var variant = new ProductVariant(Id, normalizedSku, price, initialStock, size, color, volumeMl)
        {
            TenantId = TenantId
        };
        _variants.Add(variant);
        return variant;
    }

    public void RemoveVariant(Guid variantId)
    {
        var variant = _variants.FirstOrDefault(v => v.Id == variantId)
                      ?? throw new DomainException("Variante no encontrada.");
        if (variant.StockReserved > 0)
            throw new DomainException("No se puede eliminar una variante con stock reservado en órdenes pendientes.");
        _variants.Remove(variant);
    }

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    public static string Slugify(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                        System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
