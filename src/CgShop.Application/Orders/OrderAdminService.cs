using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CgShop.Application.Orders;

/// <summary>
/// Gestión de órdenes desde el panel del tenant. Incluye el flujo de <b>validación manual de pagos</b>:
/// la orden solo pasa a <see cref="OrderStatus.PaymentValidated"/> cuando el TenantAdmin revisa el comprobante.
/// Al validar, la reserva se convierte en salida definitiva de stock; al rechazar/cancelar, se libera.
/// </summary>
public sealed class OrderAdminService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    IFileStorage storage,
    TimeProvider clock,
    INotificationService notifications,
    ILogger<OrderAdminService> logger)
{
    public async Task<PagedResult<OrderRowDto>> ListAsync(OrderQuery query, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var (page, pageSize) = Guard.Paging(query.Page, query.PageSize);
        var orders = db.Orders.AsNoTracking();
        if (query.Status is { } status)
            orders = orders.Where(o => o.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            orders = orders.Where(o => o.Number.Contains(term) || o.CustomerName.Contains(term) ||
                                       o.CustomerEmail.Contains(term));
        }

        var total = await orders.CountAsync(ct);
        var items = await orders.OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new OrderRowDto(o.Id, o.Number, o.Status, o.PaymentMethod, o.CustomerName, o.CustomerEmail,
                o.Total, o.Items.Sum(i => i.Quantity), o.Receipts.Count, o.CreatedAtUtc, o.ReservationExpiresAtUtc))
            .ToListAsync(ct);
        return new PagedResult<OrderRowDto>(items, total, page, pageSize);
    }

    public async Task<OrderDetailDto> GetAsync(Guid orderId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var order = await LoadAsync(db, orderId, tracking: false, ct);
        return OrderMapper.ToDetail(order);
    }

    /// <summary>El propietario confirma haber recibido el pago. Solo TenantAdmin.</summary>
    public Task ValidatePaymentAsync(Guid orderId, ActorInfo actor, string? note, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        return MutateAsync(orderId, async (db, order, now) =>
        {
            order.ValidatePayment(actor, note, now);
            await SettleReservationsAsync(db, order.Id, commit: true, ct);
        }, ct);
    }

    /// <summary>El propietario rechaza el comprobante (monto incorrecto, no recibido...). Libera el stock.</summary>
    public Task RejectPaymentAsync(Guid orderId, ActorInfo actor, string reason, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        return MutateAsync(orderId, async (db, order, now) =>
        {
            order.RejectPayment(actor, reason, now);
            await SettleReservationsAsync(db, order.Id, commit: false, ct);
        }, ct);
    }

    public Task StartPreparingAsync(Guid orderId, ActorInfo actor, CancellationToken ct = default) =>
        MutateAsync(orderId, (_, order, now) =>
        {
            order.StartPreparing(actor, now);
            return Task.CompletedTask;
        }, ct);

    public Task ShipAsync(Guid orderId, ActorInfo actor, string? trackingInfo, CancellationToken ct = default) =>
        MutateAsync(orderId, (_, order, now) =>
        {
            order.Ship(actor, trackingInfo, now);
            return Task.CompletedTask;
        }, ct);

    public Task MarkDeliveredAsync(Guid orderId, ActorInfo actor, CancellationToken ct = default) =>
        MutateAsync(orderId, (_, order, now) =>
        {
            order.MarkDelivered(actor, now);
            return Task.CompletedTask;
        }, ct);

    /// <summary>Cancela la orden. Si el pago aún no se validó libera la reserva; si ya se validó, reingresa el stock.</summary>
    public Task CancelAsync(Guid orderId, ActorInfo actor, string reason, CancellationToken ct = default) =>
        MutateAsync(orderId, async (db, order, now) =>
        {
            var wasPending = order.HoldsReservation;
            order.Cancel(actor, reason, now);
            if (wasPending)
            {
                await SettleReservationsAsync(db, order.Id, commit: false, ct);
            }
            else
            {
                var qtyByVariant = order.Items.GroupBy(i => i.VariantId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
                var ids = qtyByVariant.Keys.ToList();
                foreach (var variant in await db.ProductVariants.Where(v => ids.Contains(v.Id)).ToListAsync(ct))
                    variant.AdjustStock(qtyByVariant[variant.Id]);
            }
        }, ct);

    /// <summary>Descarga segura del comprobante: solo si pertenece a una orden del tenant activo.</summary>
    public async Task<(Stream Content, string ContentType, string FileName)?> OpenReceiptAsync(Guid receiptId,
        ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        var tenantInfo = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var receipt = await db.PaymentReceipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
            return null;
        var stream = await storage.OpenAsync(tenantInfo.Id, receipt.StoragePath, ct);
        return stream is null ? null : (stream, receipt.ContentType, receipt.OriginalFileName);
    }

    public async Task<DashboardDto> GetDashboardAsync(int lowStockThreshold = 5, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var byStatus = await db.Orders.AsNoTracking().GroupBy(o => o.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var paid = OrderStateMachine.PaidStatuses;
        var revenue = await db.Orders.AsNoTracking().Where(o => paid.Contains(o.Status)).SumAsync(o => o.Total, ct);
        var ordersToday = await db.Orders.AsNoTracking().CountAsync(o => o.CreatedAtUtc >= today, ct);
        var activeProducts = await db.Products.AsNoTracking().CountAsync(p => p.IsActive, ct);
        var lowStock = await db.ProductVariants.AsNoTracking()
            .CountAsync(v => v.Product!.IsActive && v.StockOnHand - v.StockReserved <= lowStockThreshold, ct);

        int Count(OrderStatus s) => byStatus.GetValueOrDefault(s);
        return new DashboardDto(Count(OrderStatus.PendingPaymentValidation),
            Count(OrderStatus.PaymentValidated) + Count(OrderStatus.Preparing), Count(OrderStatus.Shipped),
            ordersToday, revenue, activeProducts, lowStock, byStatus);
    }

    private async Task MutateAsync(Guid orderId, Func<IAppDbContext, Order, DateTime, Task> action,
        CancellationToken ct)
    {
        var order = await ConcurrencyRetry.ExecuteAsync(dbFactory, async db =>
        {
            var loaded = await LoadAsync(db, orderId, tracking: true, ct);
            var from = loaded.Status;
            await action(db, loaded, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Orden {Number}: {From} → {To}", loaded.Number, from, loaded.Status);
            return loaded;
        }, ct);
        await notifications.OrderStatusChangedAsync(order, ct);
    }

    /// <summary>Confirma (pago validado) o libera (rechazo/cancelación/expiración) las reservas activas.</summary>
    internal static async Task SettleReservationsAsync(IAppDbContext db, Guid orderId, bool commit,
        CancellationToken ct)
    {
        var reservations = await db.StockReservations
            .Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Active).ToListAsync(ct);
        var ids = reservations.Select(r => r.VariantId).Distinct().ToList();
        var variants = await db.ProductVariants.Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);

        foreach (var reservation in reservations)
        {
            var variant = variants[reservation.VariantId];
            if (commit)
            {
                variant.CommitReservation(reservation.Quantity);
                reservation.Commit();
            }
            else
            {
                variant.Release(reservation.Quantity);
                reservation.Release();
            }
        }
    }

    private static async Task<Order> LoadAsync(IAppDbContext db, Guid orderId, bool tracking, CancellationToken ct)
    {
        IQueryable<Order> query = db.Orders.Include(o => o.Items).Include(o => o.History).Include(o => o.Receipts);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(o => o.Id == orderId, ct)
               ?? throw new NotFoundException("Orden no encontrada.");
    }
}
