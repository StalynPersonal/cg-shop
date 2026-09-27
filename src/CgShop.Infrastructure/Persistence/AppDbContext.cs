using System.Reflection;
using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Infrastructure.Persistence;

/// <summary>
/// Base de datos compartida con discriminador <c>TenantId</c>.
/// <list type="bullet">
/// <item>Filtro global <c>HasQueryFilter</c> en toda entidad <see cref="ITenantEntity"/>.</item>
/// <item><c>TenantId</c> se asigna automáticamente al insertar.</item>
/// <item>Cualquier escritura sobre filas de otro tenant se bloquea con <see cref="CrossTenantWriteException"/>.</item>
/// </list>
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
    : IdentityDbContext<ApplicationUser>(options), IAppDbContext
{
    public const string TenantFilterName = "TenantFilter";

    /// <summary>
    /// Evaluado en cada consulta (EF parametriza las propiedades del DbContext),
    /// por lo que el modelo cacheado sirve a todos los tenants.
    /// Sin tenant resuelto vale Guid.Empty ⇒ no se devuelve ninguna fila (seguro por defecto).
    /// </summary>
    public Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<PaymentReceipt> PaymentReceipts => Set<PaymentReceipt>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<CgShop.Domain.Marketing.StoreBanner> StoreBanners => Set<CgShop.Domain.Marketing.StoreBanner>();
    public DbSet<CgShop.Domain.Platform.PlatformSettings> PlatformSettings => Set<CgShop.Domain.Platform.PlatformSettings>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        var applyFilter = typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter),
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Los Id (Guid v7) se generan en el dominio: así EF detecta como "Added" los hijos
        // agregados vía navegación (historial, comprobantes, variantes).
        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && !t.IsOwned()))
        {
            builder.Entity(entityType.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
        }

        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && !t.IsOwned()))
        {
            applyFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [builder]);
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantEntity
    {
        builder.Entity<TEntity>().HasQueryFilter(TenantFilterName, e => e.TenantId == CurrentTenantId);
        builder.Entity<TEntity>().HasIndex(e => e.TenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyTenantRules()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                if (tenantContext.TenantId is not { } current)
                    throw new CrossTenantWriteException(
                        $"No se puede insertar {entry.Metadata.ClrType.Name} sin un tenant activo.");
                entry.Entity.TenantId = current;
            }

            if (entry.State == EntityState.Modified &&
                entry.Property(nameof(ITenantEntity.TenantId)).IsModified)
                throw new CrossTenantWriteException("El TenantId de una entidad no puede modificarse.");

            if (tenantContext.IsSystemScope)
            {
                if (entry.Entity.TenantId == Guid.Empty)
                    throw new CrossTenantWriteException("Toda entidad multi-tenant requiere TenantId.");
                continue;
            }

            if (entry.Entity.TenantId != CurrentTenantId)
                throw new CrossTenantWriteException(
                    $"Escritura bloqueada: {entry.Metadata.ClrType.Name} pertenece a otro tenant.");
        }
    }
}

public sealed class CrossTenantWriteException(string message) : InvalidOperationException(message);
