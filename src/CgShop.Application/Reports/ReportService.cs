using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Application.Reports;

/// <summary>Zona horaria de las tiendas (República Dominicana, UTC-4 sin horario de verano).</summary>
public static class StoreTime
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Santo_Domingo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("AST", TimeSpan.FromHours(-4), "Atlantic Standard Time", "AST");
        }
    }

    /// <summary>Inicio (UTC) del día local indicado.</summary>
    public static DateTime StartOfDayUtc(DateOnly day) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zone);

    public static DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    public static DateOnly Today(TimeProvider clock) => LocalDate(clock.GetUtcNow().UtcDateTime);
}

/// <summary>
/// Reportes por rango de fechas para el propietario. Cuenta como venta todo pedido con pago validado en adelante
/// (validado, en preparación, enviado, listo para retirar, entregado), según la fecha del pedido.
/// </summary>
public sealed class ReportService(IAppDbContextFactory dbFactory)
{
    public const int MaxDays = 366;
    public const int TopCount = 20;

    public async Task<SalesReportDto> GetSalesReportAsync(DateOnly from, DateOnly to, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        if (to < from)
            throw new ValidationException(["La fecha final no puede ser anterior a la inicial."]);
        if (to.DayNumber - from.DayNumber + 1 > MaxDays)
            throw new ValidationException([$"El rango máximo es de {MaxDays} días."]);

        var startUtc = StoreTime.StartOfDayUtc(from);
        var endUtc = StoreTime.StartOfDayUtc(to.AddDays(1));
        await using var db = dbFactory.CreateDbContext();

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedAtUtc >= startUtc && o.CreatedAtUtc < endUtc)
            .Select(o => new
            {
                o.Id, o.Status, o.CreatedAtUtc, o.Subtotal, o.Tax, o.Total, o.PaymentMethod, o.DeliveryMethod,
                o.CustomerName, o.CustomerEmail
            })
            .ToListAsync(ct);

        var paidStatuses = OrderStateMachine.PaidStatuses;
        var paid = orders.Where(o => paidStatuses.Contains(o.Status)).ToList();
        var paidIds = paid.Select(o => o.Id).ToList();
        var items = paidIds.Count == 0
            ? []
            : await db.OrderItems.AsNoTracking().Where(i => paidIds.Contains(i.OrderId))
                .Select(i => new { i.OrderId, i.ProductId, i.ProductName, i.Quantity, i.UnitPrice })
                .ToListAsync(ct);

        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var categories = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => (ProductCategory?)p.Category, ct);
        var dayOf = paid.ToDictionary(o => o.Id, o => StoreTime.LocalDate(o.CreatedAtUtc));

        var revenue = paid.Sum(o => o.Total);
        var summary = new SalesSummaryDto(
            paid.Count,
            paid.Sum(o => o.Subtotal),
            paid.Sum(o => o.Tax),
            revenue,
            paid.Count == 0 ? 0 : Math.Round(revenue / paid.Count, 2, MidpointRounding.AwayFromZero),
            items.Sum(i => i.Quantity),
            orders.Count(o => o.Status == OrderStatus.PendingPaymentValidation),
            orders.Count(o => o.Status == OrderStatus.Cancelled),
            orders.Count(o => o.Status == OrderStatus.PaymentRejected),
            orders.Count(o => o.Status == OrderStatus.Expired));

        // Todos los días del rango, también los que no tuvieron ventas.
        var unitsByDay = items.GroupBy(i => dayOf[i.OrderId]).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var ordersByDay = paid.GroupBy(o => dayOf[o.Id]).ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Sum(o => o.Total)));
        var daily = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(d => from.AddDays(d))
            .Select(day => new DailySalesDto(day, ordersByDay.GetValueOrDefault(day).Count,
                unitsByDay.GetValueOrDefault(day), ordersByDay.GetValueOrDefault(day).Total))
            .ToList();

        // Ingresos por producto/categoría: sin ITBIS (precio x cantidad de cada línea).
        var topProducts = items.GroupBy(i => i.ProductId)
            .Select(g => new ProductSalesDto(g.Key, g.OrderByDescending(i => i.OrderId).First().ProductName,
                categories.GetValueOrDefault(g.Key), g.Sum(i => i.Quantity), g.Sum(i => i.UnitPrice * i.Quantity)))
            .OrderByDescending(p => p.Units).ThenByDescending(p => p.Revenue).ThenBy(p => p.ProductName)
            .Take(TopCount).ToList();

        var itemsRevenue = items.Sum(i => i.UnitPrice * i.Quantity);
        var byCategory = items.GroupBy(i => categories.GetValueOrDefault(i.ProductId))
            .Select(g =>
            {
                var amount = g.Sum(i => i.UnitPrice * i.Quantity);
                return new CategorySalesDto(g.Key, g.Sum(i => i.Quantity), amount,
                    itemsRevenue == 0 ? 0 : Math.Round(amount / itemsRevenue * 100, 1));
            })
            .OrderByDescending(c => c.Revenue).ToList();

        var byPayment = paid.GroupBy(o => o.PaymentMethod)
            .Select(g => new BreakdownDto(g.Key.DisplayName(), g.Count(), g.Sum(o => o.Total)))
            .OrderByDescending(b => b.Revenue).ToList();
        var byDelivery = paid.GroupBy(o => o.DeliveryMethod)
            .Select(g => new BreakdownDto(g.Key.DisplayName(), g.Count(), g.Sum(o => o.Total)))
            .OrderByDescending(b => b.Revenue).ToList();

        var topCustomers = paid.GroupBy(o => o.CustomerEmail.Trim().ToLowerInvariant())
            .Select(g => new CustomerSalesDto(g.OrderByDescending(o => o.CreatedAtUtc).First().CustomerName, g.Key,
                g.Count(), g.Sum(o => o.Total), g.Max(o => dayOf[o.Id])))
            .OrderByDescending(c => c.Revenue).ThenBy(c => c.Name).Take(TopCount).ToList();

        var adjustments = (await db.StockMovements.AsNoTracking()
                .Where(m => m.Type == StockMovementType.ManualAdjustment && m.CreatedAtUtc >= startUtc && m.CreatedAtUtc < endUtc)
                .Select(m => new { m.UserName, m.UserRole, m.Quantity })
                .ToListAsync(ct))
            .GroupBy(m => (m.UserName, m.UserRole))
            .Select(g => new AdjustmentsByUserDto(g.Key.UserName, g.Key.UserRole, g.Count(),
                g.Where(m => m.Quantity > 0).Sum(m => m.Quantity), -g.Where(m => m.Quantity < 0).Sum(m => m.Quantity)))
            .OrderByDescending(a => a.Adjustments).ToList();

        return new SalesReportDto(from, to, summary, daily, topProducts, byCategory, byPayment, byDelivery,
            topCustomers, adjustments);
    }
}
