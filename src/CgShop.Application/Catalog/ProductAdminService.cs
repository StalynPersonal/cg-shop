using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CgShop.Application.Catalog;

/// <summary>ABM de productos, variantes e inventario del tenant activo.</summary>
public sealed class ProductAdminService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenant,
    ILogger<ProductAdminService> logger)
{
    public async Task<PagedResult<ProductAdminRowDto>> ListAsync(string? search, ProductCategory? category,
        int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        (page, pageSize) = Guard.Paging(page, pageSize);
        var query = db.Products.AsNoTracking();
        if (category is { } c)
            query = query.Where(p => p.Category == c);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.Name.Contains(term) || (p.Brand != null && p.Brand.Contains(term)) ||
                                     p.Variants.Any(v => v.Sku.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProductAdminRowDto(p.Id, p.Name, p.Brand, p.Category, p.IsActive, p.Variants.Count,
                p.Variants.Sum(v => v.StockOnHand), p.Variants.Sum(v => v.StockReserved),
                p.Variants.Min(v => (decimal?)v.Price)))
            .ToListAsync(ct);
        return new PagedResult<ProductAdminRowDto>(items, total, page, pageSize);
    }

    public async Task<(ProductUpsertDto Product, IReadOnlyList<VariantDto> Variants)> GetForEditAsync(Guid productId,
        CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var p = await db.Products.AsNoTracking().Include(x => x.Variants)
                    .FirstOrDefaultAsync(x => x.Id == productId, ct)
                ?? throw new NotFoundException("Producto no encontrado.");
        return (new ProductUpsertDto
        {
            Name = p.Name, Category = p.Category, Brand = p.Brand, Description = p.Description,
            ImageUrl = p.ImageUrl, Attributes = new Dictionary<string, string>(p.Attributes)
        }, p.Variants.OrderBy(v => v.Sku).Select(CatalogService.ToDto).ToList());
    }

    public async Task<Guid> CreateAsync(ProductUpsertDto dto, IReadOnlyList<VariantUpsertDto> variants,
        ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        var tenantInfo = Guard.RequireTenant(tenant);
        await using var db = dbFactory.CreateDbContext();

        var product = Product.Create(dto.Name, dto.Category, dto.Brand, dto.Description, dto.ImageUrl, dto.Attributes);
        product.TenantId = tenantInfo.Id;
        foreach (var v in variants)
            product.AddVariant(v.Sku, v.Price, v.InitialStock, v.Size, v.Color, v.VolumeMl).TenantId = tenantInfo.Id;

        await EnsureUniqueSkusAsync(db, product.Variants.Select(v => v.Sku), ct);
        await EnsureUniqueSlugAsync(db, product, ct);

        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Producto {ProductId} creado por {User}", product.Id, actor.UserId);
        return product.Id;
    }

    public async Task UpdateAsync(Guid productId, ProductUpsertDto dto, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        product.Update(dto.Name, dto.Brand, dto.Description, dto.ImageUrl, dto.Attributes);
        await EnsureUniqueSlugAsync(db, product, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid productId, bool active, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        if (active) product.Activate();
        else product.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Baja: si el producto tiene ventas se desactiva (baja lógica); si no, se elimina.</summary>
    public async Task<bool> DeleteAsync(Guid productId, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        var variantIds = product.Variants.Select(v => v.Id).ToList();
        var hasSales = await db.OrderItems.AnyAsync(i => variantIds.Contains(i.VariantId), ct);
        if (hasSales)
            product.Deactivate();
        else
            db.Products.Remove(product);
        await db.SaveChangesAsync(ct);
        return !hasSales;
    }

    public async Task<Guid> AddVariantAsync(Guid productId, VariantUpsertDto dto, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        await EnsureUniqueSkusAsync(db, [dto.Sku.Trim().ToUpperInvariant()], ct);
        var variant = product.AddVariant(dto.Sku, dto.Price, dto.InitialStock, dto.Size, dto.Color, dto.VolumeMl);
        variant.TenantId = product.TenantId;
        await db.SaveChangesAsync(ct);
        return variant.Id;
    }

    public async Task RemoveVariantAsync(Guid productId, Guid variantId, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        if (await db.OrderItems.AnyAsync(i => i.VariantId == variantId, ct))
            throw new DomainException("La variante tiene ventas registradas; ajuste su stock a cero en lugar de eliminarla.");
        product.RemoveVariant(variantId);
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePriceAsync(Guid variantId, decimal price, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct)
                      ?? throw new NotFoundException("Variante no encontrada.");
        variant.ChangePrice(price);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ajuste manual de inventario (entrada o salida). Reintenta ante conflicto de concurrencia.</summary>
    public async Task<int> AdjustStockAsync(Guid variantId, int delta, string reason, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        if (delta == 0)
            throw new DomainException("El ajuste debe ser distinto de cero.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Indique el motivo del ajuste.");

        return await ConcurrencyRetry.ExecuteAsync(dbFactory, async db =>
        {
            var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId, ct)
                          ?? throw new NotFoundException("Variante no encontrada.");
            variant.AdjustStock(delta);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Stock {Sku} ajustado {Delta} por {User}: {Reason}", variant.Sku, delta,
                actor.UserId, reason);
            return variant.StockOnHand;
        }, ct);
    }

    public async Task<IReadOnlyList<InventoryRowDto>> GetInventoryAsync(string? search = null,
        int? maxAvailable = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var query = db.ProductVariants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(v => v.Sku.Contains(term) || v.Product!.Name.Contains(term));
        }

        if (maxAvailable is { } max)
            query = query.Where(v => v.StockOnHand - v.StockReserved <= max);

        var rows = await query.OrderBy(v => v.Product!.Name).ThenBy(v => v.Sku)
            .Select(v => new { Variant = v, v.Product!.Name, v.Product.Category })
            .ToListAsync(ct);

        return rows.Select(r => new InventoryRowDto(r.Variant.Id, r.Variant.ProductId, r.Name, r.Category,
            r.Variant.Sku, r.Variant.Description, r.Variant.Price, r.Variant.StockOnHand, r.Variant.StockReserved,
            r.Variant.Available)).ToList();
    }

    private static async Task<Product> LoadAsync(IAppDbContext db, Guid productId, CancellationToken ct) =>
        await db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == productId, ct)
        ?? throw new NotFoundException("Producto no encontrado.");

    private static async Task EnsureUniqueSkusAsync(IAppDbContext db, IEnumerable<string> skus, CancellationToken ct)
    {
        var list = skus.ToList();
        var duplicated = await db.ProductVariants.Where(v => list.Contains(v.Sku))
            .Select(v => v.Sku).FirstOrDefaultAsync(ct);
        if (duplicated is not null)
            throw new DomainException($"El SKU '{duplicated}' ya existe en la tienda.");
    }

    private static async Task EnsureUniqueSlugAsync(IAppDbContext db, Product product, CancellationToken ct)
    {
        var baseSlug = Product.Slugify(product.Name);
        var slug = baseSlug;
        for (var i = 2; await db.Products.AnyAsync(p => p.Slug == slug && p.Id != product.Id, ct); i++)
            slug = $"{baseSlug}-{i}";
        if (slug != product.Slug)
            product.UseSlug(slug);
    }
}
