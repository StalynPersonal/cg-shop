using System.Security.Cryptography;
using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Application.Orders;

/// <summary>
/// Crea la orden en estado <see cref="OrderStatus.PendingPaymentValidation"/> y aparta el stock
/// de forma atómica (una sola transacción + concurrencia optimista con reintentos).
/// </summary>
public sealed class CheckoutService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    TimeProvider clock,
    IOptions<OrderOptions> options,
    INotificationService notifications,
    ILogger<CheckoutService> logger)
{
    public const int MaxQuantityPerLine = 20;

    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var tenantInfo = Guard.RequireTenant(tenantContext);
        if (string.IsNullOrWhiteSpace(request.CustomerUserId))
            throw new ForbiddenException("Debes iniciar sesión para realizar un pedido.");
        Validate(request);

        var requested = request.Lines.GroupBy(l => l.VariantId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        var order = await ConcurrencyRetry.ExecuteAsync(dbFactory, async db =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantInfo.Id, ct);
            var accepted = request.PaymentMethod switch
            {
                PaymentMethod.BankTransfer => tenant.PaymentSettings.AcceptsBankTransfer,
                PaymentMethod.PaymentLink => tenant.PaymentSettings.AcceptsPaymentLink,
                _ => false
            };
            if (!accepted)
                throw new DomainException("La tienda no tiene habilitado el método de pago seleccionado.");

            var ids = requested.Keys.ToList();
            var variants = await db.ProductVariants.Include(v => v.Product)
                .Where(v => ids.Contains(v.Id)).ToListAsync(ct);
            if (variants.Count != ids.Count)
                throw new DomainException("Uno o más productos del carrito ya no están disponibles.");

            var now = clock.GetUtcNow().UtcDateTime;
            var lines = new List<OrderLine>();
            foreach (var variant in variants.OrderBy(v => v.Sku))
            {
                if (variant.Product is not { IsActive: true })
                    throw new DomainException($"El producto {variant.Product?.Name ?? variant.Sku} ya no está disponible.");
                var qty = requested[variant.Id];
                variant.Reserve(qty); // DomainException si no hay stock suficiente
                lines.Add(new OrderLine(variant.Id, variant.Sku, variant.Product.Name, variant.Description,
                    variant.Price, qty, variant.ProductId));
            }

            var ttl = TimeSpan.FromHours(options.Value.ReservationHours);
            var created = Order.Place(NewOrderNumber(now),
                new CustomerInfo(request.FullName, request.Email, request.Phone, request.ShippingAddress,
                    request.CustomerUserId, request.DeliveryMethod),
                lines, request.PaymentMethod, tenant.TaxRate, tenant.Currency, now, ttl);

            db.Orders.Add(created);
            foreach (var item in created.Items)
                db.StockReservations.Add(new StockReservation(created.Id, item.VariantId, item.Quantity,
                    created.ReservationExpiresAtUtc));

            // SaveChanges es transaccional: stock reservado + orden + reservas, todo o nada.
            // El RowVersion de cada variante detecta ventas concurrentes del mismo SKU.
            await db.SaveChangesAsync(ct);
            return created;
        }, ct);

        logger.LogInformation("Orden {Number} creada en tenant {Tenant} por {Total} {Currency}", order.Number,
            tenantInfo.Slug, order.Total, order.Currency);
        await notifications.OrderStatusChangedAsync(order, ct);

        return new PlaceOrderResult(order.Id, order.Number, order.AccessToken, order.Total,
            order.ReservationExpiresAtUtc);
    }

    private static void Validate(PlaceOrderRequest r)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(r.FullName)) errors.Add("El nombre es obligatorio.");
        if (string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@')) errors.Add("Correo electrónico inválido.");
        if (string.IsNullOrWhiteSpace(r.Phone)) errors.Add("El teléfono es obligatorio.");
        if (!Enum.IsDefined(r.DeliveryMethod)) errors.Add("Seleccione envío o retiro en tienda.");
        if (r.DeliveryMethod == DeliveryMethod.Shipping && string.IsNullOrWhiteSpace(r.ShippingAddress))
            errors.Add("La dirección de envío es obligatoria para pedidos con envío.");
        if (r.Lines.Count == 0) errors.Add("El carrito está vacío.");
        if (r.Lines.Any(l => l.Quantity is <= 0 or > MaxQuantityPerLine))
            errors.Add($"Las cantidades deben estar entre 1 y {MaxQuantityPerLine}.");
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    private static string NewOrderNumber(DateTime now) =>
        $"{now:yyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
}
