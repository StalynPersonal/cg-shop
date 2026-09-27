using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

public sealed class Product : Entity, ITenantEntity
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductImage> _images = [];

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

    /// <summary>Fotos ordenadas; la primera es la principal.</summary>
    public IReadOnlyList<ProductImage> Images => _images.OrderBy(i => i.SortOrder).ToList();

    public ProductImage? MainImage => _images.OrderBy(i => i.SortOrder).FirstOrDefault();

    public ProductImage AddImage(string storagePath, string contentType, long sizeBytes, int width, int height,
        string originalFileName, int maxImages, DateTime nowUtc)
    {
        if (_images.Count >= maxImages)
            throw new DomainException($"El producto ya tiene el máximo de {maxImages} fotos.");
        var image = new ProductImage(Id, storagePath, contentType, sizeBytes, width, height, originalFileName,
            _images.Count == 0 ? 0 : _images.Max(i => i.SortOrder) + 1, nowUtc) { TenantId = TenantId };
        _images.Add(image);
        return image;
    }

    /// <summary>Quita la foto y devuelve su ruta de almacenamiento para eliminar el archivo.</summary>
    public ProductImage RemoveImage(Guid imageId)
    {
        var image = FindImage(imageId);
        _images.Remove(image);
        Renumber(_images.OrderBy(i => i.SortOrder));
        return image;
    }

    public void SetMainImage(Guid imageId)
    {
        var image = FindImage(imageId);
        Renumber(new[] { image }.Concat(_images.Where(i => i != image).OrderBy(i => i.SortOrder)));
    }

    /// <summary>Mueve la foto una posición hacia adelante (-1) o hacia atrás (+1).</summary>
    public void MoveImage(Guid imageId, int offset)
    {
        var ordered = _images.OrderBy(i => i.SortOrder).ToList();
        var index = ordered.IndexOf(FindImage(imageId));
        var target = Math.Clamp(index + Math.Sign(offset), 0, ordered.Count - 1);
        if (target == index)
            return;
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        Renumber(ordered);
    }

    /// <summary>
    /// Asocia la foto a un color de las variantes (la tienda la muestra al elegir ese color).
    /// null o vacío = la foto aplica a todos los colores.
    /// </summary>
    public void SetImageColor(Guid imageId, string? color)
    {
        var image = FindImage(imageId);
        if (string.IsNullOrWhiteSpace(color))
        {
            image.Color = null;
            return;
        }

        var match = _variants.Select(v => v.Color).FirstOrDefault(c =>
            c is not null && string.Equals(c, color.Trim(), StringComparison.OrdinalIgnoreCase));
        image.Color = match ?? throw new DomainException($"El producto no tiene variantes de color '{color.Trim()}'.");
    }

    private ProductImage FindImage(Guid imageId) =>
        _images.FirstOrDefault(i => i.Id == imageId) ?? throw new DomainException("Foto no encontrada.");

    private static void Renumber(IEnumerable<ProductImage> ordered)
    {
        var position = 0;
        foreach (var image in ordered.ToList())
            image.SortOrder = position++;
    }

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

    /// <summary>Asigna un slug alternativo (p.ej. con sufijo) cuando el generado ya existe en la tienda.</summary>
    public void UseSlug(string slug)
    {
        var normalized = Slugify(slug);
        if (normalized.Length == 0)
            throw new DomainException("El slug no puede estar vacío.");
        Slug = normalized;
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
