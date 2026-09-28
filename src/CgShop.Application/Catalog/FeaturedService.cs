using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Application.Catalog;

public sealed record FeaturedProductDto(Guid ProductId, string Name, string? Brand, ProductCategory Category,
    string? ImageUrl, int Rank, bool IsActive, bool HasVariants, int Available)
{
    /// <summary>Solo aparece en la tienda si está activo y tiene variantes.</summary>
    public bool VisibleInStore => IsActive && HasVariants;
}

/// <summary>
/// Productos destacados a mano por el propietario en "Productos en tendencia" y "Novedades".
/// Van primero y en el orden elegido; el resto de cada sección se completa automáticamente.
/// </summary>
public sealed class FeaturedService(IAppDbContextFactory dbFactory, TimeProvider clock)
{
    public async Task<IReadOnlyList<FeaturedProductDto>> ListAsync(FeaturedSection section, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var rows = await Featured(db, section).AsNoTracking()
            .Select(p => new
            {
                p.Id, p.Name, p.Brand, p.Category, p.ImageUrl, p.IsActive, p.TrendingRank, p.NewArrivalsRank,
                MainImageId = p.Images.OrderBy(i => i.SortOrder).Select(i => (Guid?)i.Id).FirstOrDefault(),
                HasVariants = p.Variants.Any(),
                Available = p.Variants.Sum(v => v.StockOnHand - v.StockReserved)
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new FeaturedProductDto(r.Id, r.Name, r.Brand, r.Category,
                r.MainImageId is { } img ? ProductImageService.UrlFor(img) : r.ImageUrl,
                (section == FeaturedSection.Trending ? r.TrendingRank : r.NewArrivalsRank)!.Value,
                r.IsActive, r.HasVariants, r.Available))
            .OrderBy(r => r.Rank).ToList();
    }

    /// <summary>Secciones en las que está destacado un producto (para los interruptores del editor).</summary>
    public async Task<(bool Trending, bool NewArrivals)> GetStatusAsync(Guid productId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var p = await db.Products.AsNoTracking().Where(x => x.Id == productId)
                    .Select(x => new { x.TrendingRank, x.NewArrivalsRank }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Producto no encontrado.");
        return (p.TrendingRank is not null, p.NewArrivalsRank is not null);
    }

    /// <summary>Destaca (al final de la lista) o quita un producto de una sección.</summary>
    public async Task SetAsync(Guid productId, FeaturedSection section, bool featured, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw new NotFoundException("Producto no encontrado.");
        var isFeatured = product.FeaturedRank(section) is not null;
        if (isFeatured == featured)
            return;

        var others = await Featured(db, section).Where(p => p.Id != productId).ToListAsync(ct);
        if (featured)
        {
            if (others.Count >= FeaturedSectionExtensions.MaxPerSection)
                throw new DomainException(
                    $"Ya hay {FeaturedSectionExtensions.MaxPerSection} productos destacados en {section.DisplayName()}. Quite uno primero.");
            product.SetFeaturedRank(section, others.Count);
        }
        else
        {
            product.SetFeaturedRank(section, null);
            Renumber(others.OrderBy(p => p.FeaturedRank(section)), section);
        }

        db.ProductChanges.Add(new ProductChange(product,
            featured ? ProductChangeType.Featured : ProductChangeType.Unfeatured, section.DisplayName(), actor,
            clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Mueve un destacado una posición antes (-1) o después (+1).</summary>
    public async Task MoveAsync(Guid productId, FeaturedSection section, int offset, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var ordered = (await Featured(db, section).ToListAsync(ct)).OrderBy(p => p.FeaturedRank(section)).ToList();
        var index = ordered.FindIndex(p => p.Id == productId);
        if (index < 0)
            throw new NotFoundException("El producto no está destacado en esa sección.");
        var target = Math.Clamp(index + Math.Sign(offset), 0, ordered.Count - 1);
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        Renumber(ordered, section);
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<Product> Featured(IAppDbContext db, FeaturedSection section) =>
        section == FeaturedSection.Trending
            ? db.Products.Where(p => p.TrendingRank != null)
            : db.Products.Where(p => p.NewArrivalsRank != null);

    private static void Renumber(IEnumerable<Product> ordered, FeaturedSection section)
    {
        var position = 0;
        foreach (var p in ordered.ToList())
            p.SetFeaturedRank(section, position++);
    }
}
