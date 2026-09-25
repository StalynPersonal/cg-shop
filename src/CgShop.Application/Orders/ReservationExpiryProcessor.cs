using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CgShop.Application.Orders;

/// <summary>
/// Expira órdenes pendientes cuyo plazo de reserva venció y libera su stock.
/// Se ejecuta en un scope de sistema: recorre todos los tenants (IgnoreQueryFilters).
/// </summary>
public sealed class ReservationExpiryProcessor(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    TimeProvider clock,
    ILogger<ReservationExpiryProcessor> logger)
{
    public async Task<int> ExpireOverdueAsync(int batchSize = 200, CancellationToken ct = default)
    {
        if (!tenantContext.IsSystemScope)
            throw new InvalidOperationException("La expiración de reservas debe ejecutarse en un scope de sistema.");

        var now = clock.GetUtcNow().UtcDateTime;
        List<Guid> overdue;
        await using (var db = dbFactory.CreateDbContext())
        {
            overdue = await db.Orders.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Status == OrderStatus.PendingPaymentValidation && o.ReservationExpiresAtUtc <= now)
                .OrderBy(o => o.ReservationExpiresAtUtc).Select(o => o.Id).Take(batchSize).ToListAsync(ct);
        }

        var expired = 0;
        foreach (var orderId in overdue)
        {
            try
            {
                await ConcurrencyRetry.ExecuteAsync(dbFactory, async db =>
                {
                    var order = await db.Orders.IgnoreQueryFilters().Include(o => o.History)
                        .FirstAsync(o => o.Id == orderId, ct);
                    if (order.Status != OrderStatus.PendingPaymentValidation)
                        return false; // validada/cancelada mientras tanto

                    order.Expire(now);
                    var reservations = await db.StockReservations.IgnoreQueryFilters()
                        .Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Active).ToListAsync(ct);
                    var ids = reservations.Select(r => r.VariantId).ToList();
                    var variants = await db.ProductVariants.IgnoreQueryFilters()
                        .Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
                    foreach (var r in reservations)
                    {
                        variants[r.VariantId].Release(r.Quantity);
                        r.Release();
                    }

                    await db.SaveChangesAsync(ct);
                    return true;
                }, ct);
                expired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "No se pudo expirar la orden {OrderId}", orderId);
            }
        }

        if (expired > 0)
            logger.LogInformation("{Count} órdenes expiradas y stock liberado", expired);
        return expired;
    }
}
