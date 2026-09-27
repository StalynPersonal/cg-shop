using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;
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
        if (query.Audience is { } audience)
        {
            var audiences = audience.Includes();
            products = products.Where(p => audiences.Contains(p.Audience));
        }
        // Cada palabra debe aparecer en el nombre, la marca, la descripción, un color o la categoría
        // ("tenis negro", "perfume dior"...). Sin distinguir mayúsculas.
        foreach (var word in SearchWords(query.Search))
        {
            // "mujer", "hombres", "niños"... filtran por sección en lugar de buscar el texto.
            if (AudienceWord(word) is { } wordAudience)
            {
                var included = wordAudience.Includes();
                products = products.Where(p => included.Contains(p.Audience));
                continue;
            }

            var categories = Enum.GetValues<ProductCategory>()
                .Where(c => Plain(c.DisplayName()).StartsWith(Plain(word), StringComparison.OrdinalIgnoreCase)
                            || Plain(word).StartsWith(Plain(c.DisplayName()).TrimEnd('s'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            products = products.Where(p =>
                p.Name.ToLower().Contains(word)
                || (p.Brand != null && p.Brand.ToLower().Contains(word))
                || (p.Description != null && p.Description.ToLower().Contains(word))
                || p.Variants.Any(v => v.Color != null && v.Color.ToLower().Contains(word))
                || categories.Contains(p.Category));
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

    /// <summary>Estados en los que un pedido cuenta como venta (pago validado en adelante).</summary>
    private static readonly OrderStatus[] SoldStatuses =
    [
        OrderStatus.PaymentValidated, OrderStatus.Preparing, OrderStatus.Shipped, OrderStatus.ReadyForPickup,
        OrderStatus.Delivered
    ];

    /// <summary>
    /// Productos en tendencia: los más vendidos (unidades) en los últimos <paramref name="days"/> días.
    /// Si hay pocas ventas, se completa con las novedades para que la sección no quede vacía.
    /// </summary>
    public async Task<IReadOnlyList<ProductCardDto>> GetTrendingAsync(DateTime nowUtc, int take = 12, int days = 30,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 48);
        var since = nowUtc.AddDays(-days);
        await using var db = dbFactory.CreateDbContext();

        var ranking = await db.OrderItems
            .Where(i => db.Orders.Any(o => o.Id == i.OrderId && SoldStatuses.Contains(o.Status) && o.CreatedAtUtc >= since))
            .Join(db.ProductVariants, i => i.VariantId, v => v.Id, (i, v) => new { v.ProductId, i.Quantity })
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Units = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.Units)
            .Take(take * 2) // margen por si alguno está inactivo
            .ToListAsync(ct);

        var ids = ranking.Select(r => r.ProductId).ToList();
        var bestSellers = (await CardsAsync(db.Products.Where(p => ids.Contains(p.Id)), ct))
            .OrderBy(c => ids.IndexOf(c.Id)).Take(take).ToList();
        if (bestSellers.Count >= take)
            return bestSellers;

        var chosen = bestSellers.Select(c => c.Id).ToList();
        var newest = await CardsAsync(db.Products.Where(p => !chosen.Contains(p.Id)).OrderByDescending(p => p.Id)
            .Take(take - bestSellers.Count), ct);
        return bestSellers.Concat(newest.OrderByDescending(c => c.Id)).ToList();
    }

    /// <summary>Tarjetas de productos activos con variantes (foto principal, precio mínimo y disponible).</summary>
    private static async Task<List<ProductCardDto>> CardsAsync(IQueryable<Product> products, CancellationToken ct)
    {
        var rows = await products.AsNoTracking().Where(p => p.IsActive && p.Variants.Any())
            .Select(p => new
            {
                p.Id, p.Slug, p.Name, p.Brand, p.Category, p.ImageUrl,
                MainImageId = p.Images.OrderBy(i => i.SortOrder).Select(i => (Guid?)i.Id).FirstOrDefault(),
                MinPrice = p.Variants.Min(v => v.Price),
                Available = p.Variants.Sum(v => v.StockOnHand - v.StockReserved)
            }).ToListAsync(ct);
        return rows.Select(p => new ProductCardDto(p.Id, p.Slug, p.Name, p.Brand, p.Category,
            ImageUrl(p.MainImageId, p.ImageUrl), p.MinPrice, p.Available)).ToList();
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

    public async Task<CatalogFacetsDto> GetFacetsAsync(ProductCategory? category = null,
        ProductAudience? audience = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var variants = db.ProductVariants.AsNoTracking().Where(v => v.Product!.IsActive);
        if (category is { } c)
            variants = variants.Where(v => v.Product!.Category == c);
        if (audience is { } a)
        {
            var audiences = a.Includes();
            variants = variants.Where(v => audiences.Contains(v.Product!.Audience));
        }

        var categories = await db.Products.AsNoTracking().Where(p => p.IsActive)
            .Select(p => p.Category).Distinct().ToListAsync(ct);
        var sizes = await variants.Where(v => v.Size != null).Select(v => v.Size!).Distinct().ToListAsync(ct);
        var colors = await variants.Where(v => v.Color != null).Select(v => v.Color!).Distinct().ToListAsync(ct);

        return new CatalogFacetsDto(categories.Order().ToList(), SortSizes(sizes), colors.Order().ToList());
    }

    private static readonly HashSet<string> StopWords =
        ["de", "del", "la", "las", "el", "los", "para", "con", "en", "un", "una", "por"];

    /// <summary>Palabras de búsqueda en minúsculas (máximo 5; se ignoran artículos y preposiciones).</summary>
    internal static List<string> SearchWords(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? []
            : search.Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w.Length > 1 && !StopWords.Contains(w)).Distinct().Take(5).ToList();

    internal static ProductAudience? AudienceWord(string word) => word switch
    {
        "hombre" or "hombres" or "caballero" or "caballeros" => ProductAudience.Men,
        "mujer" or "mujeres" or "dama" or "damas" => ProductAudience.Women,
        "niño" or "niños" or "nino" or "ninos" or "niña" or "niñas" or "nina" or "ninas" or "infantil" => ProductAudience.Kids,
        _ => null
    };

    /// <summary>Orden natural de tallas: numéricas (38, 40.5), capacidades (64 GB, 1 TB) y luego XS..XXL.</summary>
    public static List<string> SortSizes(IEnumerable<string> sizes)
    {
        string[] clothingOrder = ["XS", "S", "M", "L", "XL", "XXL", "S/M", "L/XL", "Ajustable"];
        return sizes
            .OrderBy(s => NumericSize(s) is null ? 1 : 0)
            .ThenBy(s => NumericSize(s) ?? 0)
            .ThenBy(s => Array.IndexOf(clothingOrder, s) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Valor numérico de una talla ("40.5") o capacidad en GB ("256 GB", "1 TB" = 1024).</summary>
    private static decimal? NumericSize(string size)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = size.Trim();
        var factor = 1m;
        if (s.EndsWith("TB", StringComparison.OrdinalIgnoreCase))
            factor = 1024m;
        if (s.EndsWith("GB", StringComparison.OrdinalIgnoreCase) || s.EndsWith("TB", StringComparison.OrdinalIgnoreCase))
            s = s[..^2].Trim();
        return decimal.TryParse(s, System.Globalization.NumberStyles.Number, inv, out var n) ? n * factor : null;
    }

    /// <summary>Texto sin tildes (para comparar "electronica" con "Electrónica").</summary>
    private static string Plain(string text) =>
        new(text.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());

    internal static VariantDto ToDto(ProductVariant v) =>
        new(v.Id, v.Sku, v.Size, v.Color, v.VolumeMl, v.Price, v.Available, v.Description);

    private static ProductDetailDto ToDetail(Product p) =>
        new(p.Id, p.Slug, p.Name, p.Brand, p.Description, p.Category, ImageUrl(p.MainImage?.Id, p.ImageUrl),
            p.Attributes,
            p.Variants.OrderBy(v => v.VolumeMl).ThenBy(v => v.Size).ThenBy(v => v.Color).Select(ToDto).ToList(),
            p.Images.Select(ProductImageService.ToDto).ToList(), p.Audience);

    /// <summary>Foto principal subida; si no hay, la URL externa opcional del producto.</summary>
    private static string? ImageUrl(Guid? mainImageId, string? externalUrl) =>
        mainImageId is { } id ? ProductImageService.UrlFor(id) : externalUrl;
}
