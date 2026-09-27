using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CgShop.Application.Catalog;

/// <summary>
/// ABM de productos, variantes e inventario del tenant activo.
/// Todo cambio de stock físico deja un <see cref="StockMovement"/> (historial de movimientos) visible para el propietario.
/// </summary>
public sealed class ProductAdminService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenant,
    IFileStorage storage,
    TimeProvider clock,
    ILogger<ProductAdminService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private void RecordInitialStock(IAppDbContext db, Product product, IEnumerable<ProductVariant> variants,
        ActorInfo actor)
    {
        foreach (var v in variants.Where(v => v.StockOnHand > 0))
            db.StockMovements.Add(new StockMovement(v, product.Name, StockMovementType.InitialStock, v.StockOnHand, 0,
                actor, Now, "Stock inicial al crear la variante"));
    }

    /// <summary>Anota un cambio en el historial del producto (mismo SaveChanges que el cambio: atómico).</summary>
    private void Record(IAppDbContext db, Product product, ProductChangeType type, string? details, ActorInfo actor) =>
        db.ProductChanges.Add(new ProductChange(product, type, details, actor, Now));

    private static string Money(decimal value) => value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

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
            Name = p.Name, Category = p.Category, Audience = p.Audience, Brand = p.Brand, Description = p.Description,
            ImageUrl = p.ImageUrl, Attributes = new Dictionary<string, string>(p.Attributes)
        }, p.Variants.OrderBy(v => v.Sku).Select(CatalogService.ToDto).ToList());
    }

    public async Task<Guid> CreateAsync(ProductUpsertDto dto, IReadOnlyList<VariantUpsertDto> variants,
        ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        var tenantInfo = Guard.RequireTenant(tenant);
        await using var db = dbFactory.CreateDbContext();

        var product = Product.Create(dto.Name, dto.Category, dto.Brand, dto.Description, dto.ImageUrl, dto.Attributes,
            dto.Audience);
        product.TenantId = tenantInfo.Id;
        foreach (var v in variants)
            product.AddVariant(v.Sku, v.Price, v.InitialStock, v.Size, v.Color, v.VolumeMl).TenantId = tenantInfo.Id;

        await EnsureUniqueSkusAsync(db, product.Variants.Select(v => v.Sku), ct);
        await EnsureUniqueSlugAsync(db, product, ct);

        db.Products.Add(product);
        RecordInitialStock(db, product, product.Variants, actor);
        Record(db, product, ProductChangeType.Created,
            $"{product.Category.DisplayName()} · {product.Audience.DisplayName()} · {product.Variants.Count} variante(s): " +
            string.Join(", ", product.Variants.Select(v => v.Sku)), actor);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Producto {ProductId} creado por {User}", product.Id, actor.UserId);
        return product.Id;
    }

    public async Task UpdateAsync(Guid productId, ProductUpsertDto dto, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        var before = Snapshot(product);
        product.Update(dto.Name, dto.Brand, dto.Description, dto.ImageUrl, dto.Attributes);
        product.SetAudience(dto.Audience);
        await EnsureUniqueSlugAsync(db, product, ct);
        var changes = DescribeChanges(before, Snapshot(product));
        if (changes.Count > 0)
            Record(db, product, ProductChangeType.Updated, string.Join("; ", changes), actor);
        await db.SaveChangesAsync(ct);
    }

    private sealed record ProductSnapshot(string Name, string? Brand, string? Description, string? ImageUrl,
        ProductAudience Audience, Dictionary<string, string> Attributes);

    private static ProductSnapshot Snapshot(Product p) =>
        new(p.Name, p.Brand, p.Description, p.ImageUrl, p.Audience, new Dictionary<string, string>(p.Attributes));

    /// <summary>Lista legible de lo que cambió ("Nombre: 'A' → 'B'").</summary>
    private static List<string> DescribeChanges(ProductSnapshot a, ProductSnapshot b)
    {
        var changes = new List<string>();
        void Text(string field, string? from, string? to)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal))
                changes.Add($"{field}: '{from ?? "—"}' → '{to ?? "—"}'");
        }

        Text("Nombre", a.Name, b.Name);
        Text("Marca", a.Brand, b.Brand);
        if (!string.Equals(a.Description, b.Description, StringComparison.Ordinal))
            changes.Add("Descripción modificada");
        Text("Imagen externa", a.ImageUrl, b.ImageUrl);
        if (a.Audience != b.Audience)
            changes.Add($"Para: {a.Audience.DisplayName()} → {b.Audience.DisplayName()}");
        foreach (var key in a.Attributes.Keys.Union(b.Attributes.Keys).Order())
            Text(key, a.Attributes.GetValueOrDefault(key), b.Attributes.GetValueOrDefault(key));
        return changes;
    }

    public async Task SetActiveAsync(Guid productId, bool active, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        if (product.IsActive == active)
            return;
        if (active) product.Activate();
        else product.Deactivate();
        Record(db, product, active ? ProductChangeType.Activated : ProductChangeType.Deactivated, null, actor);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Baja: si el producto tiene ventas se desactiva (baja lógica); si no, se elimina.</summary>
    public async Task<bool> DeleteAsync(Guid productId, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        var variantIds = product.Variants.Select(v => v.Id).ToList();
        var hasSales = await db.OrderItems.AnyAsync(i => i.ProductId == productId || variantIds.Contains(i.VariantId), ct);
        if (hasSales)
        {
            product.Deactivate();
            Record(db, product, ProductChangeType.Deactivated, "Se intentó eliminar, pero tiene ventas: quedó desactivado.", actor);
        }
        else
        {
            foreach (var v in product.Variants.Where(v => v.StockOnHand > 0))
                db.StockMovements.Add(new StockMovement(v, product.Name, StockMovementType.ManualAdjustment,
                    -v.StockOnHand, v.StockOnHand, actor, Now, "Producto eliminado"));
            Record(db, product, ProductChangeType.Deleted,
                $"{product.Variants.Count} variante(s) y {product.Variants.Sum(v => v.StockOnHand)} unidad(es) en stock.", actor);
            db.Products.Remove(product); // las fotos se eliminan en cascada
        }
        var imagePaths = hasSales
            ? []
            : await db.ProductImages.Where(i => i.ProductId == productId).Select(i => i.StoragePath).ToListAsync(ct);
        await db.SaveChangesAsync(ct);

        foreach (var path in imagePaths)
            await storage.DeleteAsync(product.TenantId, path, CancellationToken.None);
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
        RecordInitialStock(db, product, [variant], actor);
        Record(db, product, ProductChangeType.VariantAdded,
            $"{variant.Sku} ({variant.Description}) · precio {Money(variant.Price)} · stock {variant.StockOnHand}", actor);
        await db.SaveChangesAsync(ct);
        return variant.Id;
    }

    public async Task RemoveVariantAsync(Guid productId, Guid variantId, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await LoadAsync(db, productId, ct);
        await EnsureNoPendingOrdersAsync(db, [variantId], "eliminar la variante", ct);
        var variant = product.Variants.FirstOrDefault(v => v.Id == variantId)
                      ?? throw new NotFoundException("Variante no encontrada.");
        product.RemoveVariant(variantId);
        if (variant.StockOnHand > 0)
            db.StockMovements.Add(new StockMovement(variant, product.Name, StockMovementType.ManualAdjustment,
                -variant.StockOnHand, variant.StockOnHand, actor, Now, "Variante eliminada"));
        Record(db, product, ProductChangeType.VariantRemoved,
            $"{variant.Sku} ({variant.Description}) · stock eliminado {variant.StockOnHand}", actor);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Una variante con pedidos pendientes de pago (stock reservado) no puede eliminarse: al validar el pago
    /// no habría a qué descontar. Los pedidos ya pagados sí lo permiten: conservan su propia copia.
    /// </summary>
    private static async Task EnsureNoPendingOrdersAsync(IAppDbContext db, IReadOnlyCollection<Guid> variantIds,
        string action, CancellationToken ct)
    {
        var pending = await db.StockReservations
            .Where(r => variantIds.Contains(r.VariantId) && r.Status == CgShop.Domain.Orders.ReservationStatus.Active)
            .Select(r => r.OrderId).Distinct().CountAsync(ct);
        if (pending > 0)
            throw new DomainException($"No se puede {action}: hay {pending} pedido(s) pendiente(s) de pago con este producto. " +
                                      "Valide, rechace o cancele esos pedidos primero.");
    }

    public async Task ChangePriceAsync(Guid variantId, decimal price, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var variant = await db.ProductVariants.Include(v => v.Product).FirstOrDefaultAsync(v => v.Id == variantId, ct)
                      ?? throw new NotFoundException("Variante no encontrada.");
        var before = variant.Price;
        variant.ChangePrice(price);
        if (before != variant.Price)
            Record(db, variant.Product!, ProductChangeType.PriceChanged,
                $"{variant.Sku}: {Money(before)} → {Money(variant.Price)}", actor);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Historial de cambios del producto (más reciente primero). Solo el propietario.</summary>
    public async Task<IReadOnlyList<ProductChangeDto>> GetHistoryAsync(Guid productId, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        return await db.ProductChanges.AsNoTracking().Where(c => c.ProductId == productId)
            .OrderByDescending(c => c.CreatedAtUtc).ThenByDescending(c => c.Id)
            .Select(c => new ProductChangeDto(c.Id, c.Type, c.Details, c.UserName, c.UserRole, c.CreatedAtUtc))
            .ToListAsync(ct);
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
            var variant = await db.ProductVariants.Include(v => v.Product)
                              .FirstOrDefaultAsync(v => v.Id == variantId, ct)
                          ?? throw new NotFoundException("Variante no encontrada.");
            var before = variant.StockOnHand;
            variant.AdjustStock(delta);
            // Mismo SaveChanges: el ajuste y su registro de auditoría son atómicos.
            db.StockMovements.Add(new StockMovement(variant, variant.Product!.Name, StockMovementType.ManualAdjustment,
                delta, before, actor, Now, reason));
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

    /// <summary>
    /// Historial de movimientos de inventario. Solo el propietario (TenantAdmin) puede consultarlo:
    /// así ve cualquier ajuste hecho por empleados. Los movimientos son inmutables.
    /// </summary>
    public async Task<PagedResult<StockMovementDto>> GetMovementsAsync(StockMovementQuery query, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        var (page, pageSize) = Guard.Paging(query.Page, query.PageSize);
        await using var db = dbFactory.CreateDbContext();

        var movements = db.StockMovements.AsNoTracking();
        if (query.Type is { } type)
            movements = movements.Where(m => m.Type == type);
        if (query.VariantId is { } variantId)
            movements = movements.Where(m => m.VariantId == variantId);
        if (query.FromUtc is { } from)
            movements = movements.Where(m => m.CreatedAtUtc >= from);
        if (query.ToUtc is { } to)
            movements = movements.Where(m => m.CreatedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            movements = movements.Where(m => m.Sku.Contains(term) || m.ProductName.Contains(term) ||
                                             m.UserName.Contains(term) ||
                                             (m.OrderNumber != null && m.OrderNumber.Contains(term)) ||
                                             (m.Reason != null && m.Reason.Contains(term)));
        }

        var total = await movements.CountAsync(ct);
        var items = await movements.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new StockMovementDto(m.Id, m.CreatedAtUtc, m.VariantId, m.Sku, m.ProductName, m.Type,
                m.Quantity, m.StockBefore, m.StockAfter, m.Reason, m.OrderNumber, m.UserName, m.UserRole))
            .ToListAsync(ct);
        return new PagedResult<StockMovementDto>(items, total, page, pageSize);
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
