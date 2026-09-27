using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

/// <summary>Línea a facturar: copia los datos del producto/variante al momento de la compra.</summary>
public sealed record OrderLine(Guid VariantId, string Sku, string ProductName, string VariantDescription,
    decimal UnitPrice, int Quantity, Guid ProductId = default);

/// <param name="ShippingAddress">Obligatoria para <see cref="DeliveryMethod.Shipping"/>; se ignora en retiro.</param>
public sealed record CustomerInfo(string FullName, string Email, string Phone, string? ShippingAddress,
    string? UserId = null, DeliveryMethod Delivery = DeliveryMethod.Shipping);

/// <summary>
/// Agregado raíz de la orden. Encapsula la máquina de estados y la auditoría.
/// La orden nace en <see cref="OrderStatus.PendingPaymentValidation"/> y SOLO un TenantAdmin
/// puede validar (o rechazar) el pago manualmente.
/// </summary>
public sealed class Order : Entity, ITenantEntity
{
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderStatusHistory> _history = [];
    private readonly List<PaymentReceipt> _receipts = [];

    private Order() { }

    public Guid TenantId { get; set; }
    public string Number { get; private set; } = "";
    public OrderStatus Status { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }

    public string CustomerName { get; private set; } = "";
    public string CustomerEmail { get; private set; } = "";
    public string CustomerPhone { get; private set; } = "";
    public DeliveryMethod DeliveryMethod { get; private set; } = DeliveryMethod.Shipping;

    /// <summary>Dirección de envío; nula cuando el cliente retira en tienda.</summary>
    public string? ShippingAddress { get; private set; }

    public string? CustomerUserId { get; private set; }

    public decimal Subtotal { get; private set; }
    public decimal TaxRate { get; private set; }
    public decimal Tax { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = "DOP";

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ReservationExpiresAtUtc { get; private set; }
    public DateTime? PaymentValidatedAtUtc { get; private set; }
    public string? PaymentValidatedBy { get; private set; }

    /// <summary>Token de acceso público (sin login) para ver la orden y subir comprobante.</summary>
    public string AccessToken { get; private set; } = "";

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<OrderItem> Items => _items;
    public IReadOnlyList<OrderStatusHistory> History => _history;
    public IReadOnlyList<PaymentReceipt> Receipts => _receipts;

    public bool IsPaid => OrderStateMachine.PaidStatuses.Contains(Status);
    public bool HoldsReservation => Status == OrderStatus.PendingPaymentValidation;

    public static Order Place(string number, CustomerInfo customer, IReadOnlyCollection<OrderLine> lines,
        PaymentMethod paymentMethod, decimal taxRate, string currency, DateTime nowUtc,
        TimeSpan reservationTtl)
    {
        if (string.IsNullOrWhiteSpace(number))
            throw new DomainException("El número de orden es obligatorio.");
        if (lines.Count == 0)
            throw new DomainException("La orden debe contener al menos un producto.");
        if (string.IsNullOrWhiteSpace(customer.FullName) || string.IsNullOrWhiteSpace(customer.Email))
            throw new DomainException("Nombre y correo son obligatorios.");
        if (!Enum.IsDefined(customer.Delivery))
            throw new DomainException("Seleccione envío o retiro en tienda.");
        if (customer.Delivery == DeliveryMethod.Shipping && string.IsNullOrWhiteSpace(customer.ShippingAddress))
            throw new DomainException("La dirección de envío es obligatoria para pedidos con envío.");
        if (!customer.Email.Contains('@'))
            throw new DomainException("El correo electrónico no es válido.");
        if (reservationTtl <= TimeSpan.Zero)
            throw new DomainException("El tiempo de reserva debe ser positivo.");

        var order = new Order
        {
            Number = number,
            Status = OrderStatus.PendingPaymentValidation,
            PaymentMethod = paymentMethod,
            CustomerName = customer.FullName.Trim(),
            CustomerEmail = customer.Email.Trim().ToLowerInvariant(),
            CustomerPhone = customer.Phone?.Trim() ?? "",
            DeliveryMethod = customer.Delivery,
            ShippingAddress = customer.Delivery == DeliveryMethod.Shipping ? customer.ShippingAddress!.Trim() : null,
            CustomerUserId = customer.UserId,
            TaxRate = taxRate,
            Currency = currency,
            CreatedAtUtc = nowUtc,
            ReservationExpiresAtUtc = nowUtc.Add(reservationTtl),
            AccessToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
        };

        foreach (var group in lines.GroupBy(l => l.VariantId))
        {
            var first = group.First();
            order._items.Add(new OrderItem(order.Id, first.VariantId, first.Sku, first.ProductName,
                first.VariantDescription, first.UnitPrice, group.Sum(l => l.Quantity), first.ProductId));
        }

        order.Subtotal = order._items.Sum(i => i.LineTotal);
        order.Tax = Math.Round(order.Subtotal * taxRate, 2, MidpointRounding.AwayFromZero);
        order.Total = order.Subtotal + order.Tax;

        var customerActor = new ActorInfo(customer.UserId ?? "guest", order.CustomerName, [Roles.Customer]);
        order._history.Add(new OrderStatusHistory(order.Id, null, order.Status, customerActor,
            "Orden creada. Stock apartado hasta la validación del pago.", null, nowUtc));
        return order;
    }

    /// <summary>El cliente adjunta el comprobante de transferencia/pago.</summary>
    public PaymentReceipt AttachReceipt(string originalFileName, string storagePath, string contentType,
        long sizeBytes, string? reference, DateTime nowUtc)
    {
        if (Status != OrderStatus.PendingPaymentValidation)
            throw new DomainException("Solo se pueden adjuntar comprobantes a órdenes pendientes de validación.");

        var receipt = new PaymentReceipt(Id, originalFileName, storagePath, contentType, sizeBytes, reference,
            nowUtc) { TenantId = TenantId };
        _receipts.Add(receipt);
        AddHistory(Status, Status, new ActorInfo(CustomerUserId ?? "guest", CustomerName, [Roles.Customer]),
            "Comprobante de pago adjuntado.", receipt.Id, nowUtc);
        return receipt;
    }

    /// <summary>Validación manual del pago por el propietario/administrador del tenant.</summary>
    public void ValidatePayment(ActorInfo actor, string? note, DateTime nowUtc)
    {
        EnsureTenantAdmin(actor, "validar pagos");
        ChangeStatus(OrderStatus.PaymentValidated, actor,
            string.IsNullOrWhiteSpace(note) ? "Pago validado manualmente." : note.Trim(), nowUtc,
            _receipts.LastOrDefault()?.Id);
        PaymentValidatedAtUtc = nowUtc;
        PaymentValidatedBy = actor.DisplayName;
    }

    public void RejectPayment(ActorInfo actor, string reason, DateTime nowUtc)
    {
        EnsureTenantAdmin(actor, "rechazar pagos");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Debe indicar el motivo del rechazo.");
        ChangeStatus(OrderStatus.PaymentRejected, actor, reason, nowUtc, _receipts.LastOrDefault()?.Id);
    }

    public void StartPreparing(ActorInfo actor, DateTime nowUtc) =>
        ChangeStatusByStaff(OrderStatus.Preparing, actor, "Pedido en preparación.", nowUtc);

    /// <summary>Despacho a domicilio. Un pedido de retiro en tienda nunca se envía.</summary>
    public void Ship(ActorInfo actor, string? trackingInfo, DateTime nowUtc)
    {
        if (DeliveryMethod == DeliveryMethod.Pickup)
            throw new DomainException(
                $"El pedido {Number} es para retiro en tienda: no se envía, se marca como listo para retirar.");
        ChangeStatusByStaff(OrderStatus.Shipped, actor, trackingInfo ?? "Pedido despachado.", nowUtc);
    }

    /// <summary>Pedido de retiro preparado y disponible en la tienda.</summary>
    public void MarkReadyForPickup(ActorInfo actor, string? note, DateTime nowUtc)
    {
        if (DeliveryMethod != DeliveryMethod.Pickup)
            throw new DomainException(
                $"El pedido {Number} es con envío a domicilio: no puede marcarse como listo para retirar.");
        ChangeStatusByStaff(OrderStatus.ReadyForPickup, actor,
            string.IsNullOrWhiteSpace(note) ? "Pedido listo para retirar en la tienda." : note, nowUtc);
    }

    public void MarkDelivered(ActorInfo actor, DateTime nowUtc) =>
        ChangeStatusByStaff(OrderStatus.Delivered, actor,
            DeliveryMethod == DeliveryMethod.Pickup ? "Pedido retirado por el cliente en la tienda." : "Pedido entregado.",
            nowUtc);

    public void Cancel(ActorInfo actor, string reason, DateTime nowUtc)
    {
        if (!actor.IsTenantStaff)
            throw new DomainException("Solo el personal de la tienda puede cancelar órdenes.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Debe indicar el motivo de la cancelación.");
        ChangeStatus(OrderStatus.Cancelled, actor, reason, nowUtc);
    }

    /// <summary>Expiración automática de la reserva por falta de pago.</summary>
    public void Expire(DateTime nowUtc)
    {
        if (nowUtc < ReservationExpiresAtUtc)
            throw new DomainException("La reserva de esta orden aún no ha expirado.");
        ChangeStatus(OrderStatus.Expired, ActorInfo.System, "Reserva expirada sin validación de pago.", nowUtc);
    }

    private void ChangeStatusByStaff(OrderStatus target, ActorInfo actor, string note, DateTime nowUtc)
    {
        if (!actor.IsTenantStaff)
            throw new DomainException("Solo el personal de la tienda puede avanzar el estado de la orden.");
        ChangeStatus(target, actor, note, nowUtc);
    }

    private void ChangeStatus(OrderStatus target, ActorInfo actor, string? note, DateTime nowUtc,
        Guid? receiptId = null)
    {
        if (!OrderStateMachine.CanTransition(Status, target, DeliveryMethod))
            throw new DomainException(
                $"Transición inválida: la orden {Number} no puede pasar de '{Status.DisplayName()}' a '{target.DisplayName()}'.");
        var from = Status;
        Status = target;
        AddHistory(from, target, actor, note, receiptId, nowUtc);
    }

    private void AddHistory(OrderStatus? from, OrderStatus to, ActorInfo actor, string? note, Guid? receiptId,
        DateTime nowUtc) =>
        _history.Add(new OrderStatusHistory(Id, from, to, actor, string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            receiptId, nowUtc) { TenantId = TenantId });

    private static void EnsureTenantAdmin(ActorInfo actor, string action)
    {
        if (!actor.IsTenantAdmin)
            throw new DomainException($"Solo el propietario/administrador de la tienda puede {action}.");
    }
}
