using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Application.Catalog;

/// <summary>Consultas públicas del catálogo de la tienda activa (el filtro global aplica el TenantId).</summary>
public sealed class CatalogService(IAppDbContextFactory dbFactory)
{
    public async Task<PagedResult<ProductCardDto>> SearchAsync(CatalogQuery query, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var (page, pageSize) = Guard.Paging(query.Page, query.PageSize);
        var products = db.Products.AsNoTracking().Where(p => p.IsActive && p.Variants.Any());

        if (query.Category is { } category)
            products = products.Where(p => p.Category == category);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            products = products.Where(p => p.Name.Contains(term) || (p.Brand != null && p.Brand.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.Size))
            products = products.Where(p => p.Variants.Any(v => v.Size == query.Size));
        if (!string.IsNullOrWhiteSpace(query.Color))
            products = products.Where(p => p.Variants.Any(v => v.Color == query.Color));
        if (query.InStockOnly)
            products = products.Where(p => p.Variants.Any(v => v.StockOnHand - v.StockReserved > 0));

        var projected = products.Select(p => new
        {
            p.Id,
            p.Slug,
            p.Name,
            p.Brand,
            p.Category,
            p.ImageUrl,
            MainImageId = p.Images.OrderBy(i => i.SortOrder).Select(i => (Guid?)i.Id).FirstOrDefault(),
            MinPrice = p.Variants.Min(v => v.Price),
            Available = p.Variants.Sum(v => v.StockOnHand - v.StockReserved)
        });

        if (query.MinPrice is { } min)
            projected = projected.Where(p => p.MinPrice >= min);
        if (query.MaxPrice is { } max)
            projected = projected.Where(p => p.MinPrice <= max);

        projected = query.Sort switch
        {
            CatalogSort.PriceAsc => projected.OrderBy(p => p.MinPrice).ThenBy(p => p.Name),
            CatalogSort.PriceDesc => projected.OrderByDescending(p => p.MinPrice).ThenBy(p => p.Name),
            CatalogSort.Name => projected.OrderBy(p => p.Name),
            _ => projected.OrderByDescending(p => p.Id) // Guid v7: orden cronológico
        };

        var total = await projected.CountAsync(ct);
        var rows = await projected.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(p => new ProductCardDto(p.Id, p.Slug, p.Name, p.Brand, p.Category,
            ImageUrl(p.MainImageId, p.ImageUrl), p.MinPrice, p.Available)).ToList();

        return new PagedResult<ProductCardDto>(items, total, page, pageSize);
    }

    public async Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.AsNoTracking().Include(p => p.Variants).Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);
        return product is null ? null : ToDetail(product);
    }

    public async Task<ProductDetailDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.AsNoTracking().Include(p => p.Variants).Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return product is null ? null : ToDetail(product);
    }

    /// <summary>Datos actuales de variantes (precio/stock) para el carrito, junto al nombre del producto.</summary>
    public async Task<IReadOnlyList<CartVariantDto>> GetCartVariantsAsync(IEnumerable<Guid> variantIds,
        CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var ids = variantIds.Distinct().ToList();
        var variants = await db.ProductVariants.AsNoTracking().Include(v => v.Product).ThenInclude(p => p!.Images)
            .Where(v => ids.Contains(v.Id)).ToListAsync(ct);
        return variants.Select(v => new CartVariantDto(ToDto(v), v.ProductId, v.Product!.Name, v.Product.Slug,
            ImageUrl(v.Product.MainImage?.Id, v.Product.ImageUrl), v.Product.IsActive)).ToList();
    }

    public async Task<CatalogFacetsDto> GetFacetsAsync(ProductCategory? category = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var variants = db.ProductVariants.AsNoTracking().Where(v => v.Product!.IsActive);
        if (category is { } c)
            variants = variants.Where(v => v.Product!.Category == c);

        var categories = await db.Products.AsNoTracking().Where(p => p.IsActive)
            .Select(p => p.Category).Distinct().ToListAsync(ct);
        var sizes = await variants.Where(v => v.Size != null).Select(v => v.Size!).Distinct().ToListAsync(ct);
        var colors = await variants.Where(v => v.Color != null).Select(v => v.Color!).Distinct().ToListAsync(ct);

        return new CatalogFacetsDto(categories.Order().ToList(), SortSizes(sizes), colors.Order().ToList());
    }

    internal static List<string> SortSizes(IEnumerable<string> sizes)
    {
        string[] clothingOrder = ["XS", "S", "M", "L", "XL", "XXL", "S/M", "L/XL", "Ajustable"];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return sizes
            .OrderBy(s => decimal.TryParse(s, inv, out _) ? 0 : 1)
            .ThenBy(s => decimal.TryParse(s, inv, out var n) ? n : 0)
            .ThenBy(s => Array.IndexOf(clothingOrder, s) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    internal static VariantDto ToDto(ProductVariant v) =>
        new(v.Id, v.Sku, v.Size, v.Color, v.VolumeMl, v.Price, v.Available, v.Description);

    private static ProductDetailDto ToDetail(Product p) =>
        new(p.Id, p.Slug, p.Name, p.Brand, p.Description, p.Category, ImageUrl(p.MainImage?.Id, p.ImageUrl),
            p.Attributes,
            p.Variants.OrderBy(v => v.VolumeMl).ThenBy(v => v.Size).ThenBy(v => v.Color).Select(ToDto).ToList(),
            p.Images.Select(ProductImageService.ToDto).ToList());

    /// <summary>Foto principal subida; si no hay, la URL externa opcional del producto.</summary>
    private static string? ImageUrl(Guid? mainImageId, string? externalUrl) =>
        mainImageId is { } id ? ProductImageService.UrlFor(id) : externalUrl;
}
