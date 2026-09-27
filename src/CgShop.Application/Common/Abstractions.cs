using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CgShop.Application.Common;

/// <summary>
/// Crea un contexto por operación (seguro para circuitos Blazor Server de larga duración).
/// Registrada como scoped: cada contexto creado usa el <c>ITenantContext</c> del scope actual.
/// </summary>
public interface IAppDbContextFactory
{
    IAppDbContext CreateDbContext();
}

public interface IAppDbContext : IAsyncDisposable, IDisposable
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductVariant> ProductVariants { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderItem> OrderItems { get; }
    DbSet<OrderStatusHistory> OrderStatusHistory { get; }
    DbSet<PaymentReceipt> PaymentReceipts { get; }
    DbSet<StockReservation> StockReservations { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<CgShop.Domain.Marketing.StoreBanner> StoreBanners { get; }
    DbSet<ProductChange> ProductChanges { get; }
    DatabaseFacade Database { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IFileStorage
{
    /// <summary>Guarda un archivo aislado por tenant y devuelve la ruta relativa de almacenamiento.</summary>
    Task<string> SaveAsync(Guid tenantId, string folder, string extension, Stream content, CancellationToken ct = default);

    /// <summary>Abre un archivo verificando que pertenece al tenant indicado.</summary>
    Task<Stream?> OpenAsync(Guid tenantId, string storagePath, CancellationToken ct = default);

    Task DeleteAsync(Guid tenantId, string storagePath, CancellationToken ct = default);
}

public interface INotificationService
{
    Task OrderStatusChangedAsync(Order order, CancellationToken ct = default);
}

/// <summary>Provisiona el usuario administrador inicial de un tenant (implementado con Identity).</summary>
public interface ITenantAdminProvisioner
{
    Task ProvisionAsync(Guid tenantId, string fullName, string email, string password, CancellationToken ct = default);
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class ForbiddenException(string message) : Exception(message);

public sealed class ValidationException(IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class OrderOptions
{
    /// <summary>Horas que se aparta el stock mientras se valida el pago.</summary>
    public int ReservationHours { get; set; } = 48;

    /// <summary>Frecuencia del job que libera reservas vencidas.</summary>
    public int ExpiryCheckMinutes { get; set; } = 10;

    public long MaxReceiptBytes { get; set; } = 5 * 1024 * 1024;
}
