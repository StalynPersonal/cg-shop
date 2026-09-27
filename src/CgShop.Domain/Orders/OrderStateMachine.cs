namespace CgShop.Domain.Orders;

/// <summary>
/// Transiciones permitidas del ciclo de vida de una orden.
/// Regla clave: desde <see cref="OrderStatus.PendingPaymentValidation"/> solo se avanza
/// a <see cref="OrderStatus.PaymentValidated"/> mediante validación manual del propietario.
/// Tras la preparación el camino depende de la entrega:
/// envío → <see cref="OrderStatus.Shipped"/>; retiro en tienda → <see cref="OrderStatus.ReadyForPickup"/>.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.PendingPaymentValidation] =
            [OrderStatus.PaymentValidated, OrderStatus.PaymentRejected, OrderStatus.Cancelled, OrderStatus.Expired],
        [OrderStatus.PaymentRejected] = [],
        [OrderStatus.PaymentValidated] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Shipped, OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.ReadyForPickup] = [OrderStatus.Delivered, OrderStatus.Cancelled],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Expired] = []
    };

    /// <summary>Estados que implican que el pago ya fue validado por el propietario.</summary>
    public static readonly OrderStatus[] PaidStatuses =
    [
        OrderStatus.PaymentValidated, OrderStatus.Preparing, OrderStatus.Shipped, OrderStatus.ReadyForPickup,
        OrderStatus.Delivered
    ];

    /// <summary>Transición permitida para una entrega concreta (un retiro nunca se "envía").</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to, DeliveryMethod delivery) =>
        NextStatuses(from, delivery).Contains(to);

    public static IReadOnlyList<OrderStatus> NextStatuses(OrderStatus from, DeliveryMethod delivery) =>
        NextStatuses(from).Where(to => AppliesTo(to, delivery)).ToList();

    /// <summary>Transiciones sin considerar la entrega (unión de ambos caminos).</summary>
    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<OrderStatus> NextStatuses(OrderStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(OrderStatus status) => NextStatuses(status).Count == 0;

    private static bool AppliesTo(OrderStatus status, DeliveryMethod delivery) => status switch
    {
        OrderStatus.Shipped => delivery == DeliveryMethod.Shipping,
        OrderStatus.ReadyForPickup => delivery == DeliveryMethod.Pickup,
        _ => true
    };
}
