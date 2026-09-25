namespace CgShop.Domain.Orders;

/// <summary>
/// Transiciones permitidas del ciclo de vida de una orden.
/// Regla clave: desde <see cref="OrderStatus.PendingPaymentValidation"/> solo se avanza
/// a <see cref="OrderStatus.PaymentValidated"/> mediante validación manual del propietario.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.PendingPaymentValidation] =
            [OrderStatus.PaymentValidated, OrderStatus.PaymentRejected, OrderStatus.Cancelled, OrderStatus.Expired],
        [OrderStatus.PaymentRejected] = [],
        [OrderStatus.PaymentValidated] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Expired] = []
    };

    /// <summary>Estados que implican que el pago ya fue validado por el propietario.</summary>
    public static readonly OrderStatus[] PaidStatuses =
        [OrderStatus.PaymentValidated, OrderStatus.Preparing, OrderStatus.Shipped, OrderStatus.Delivered];

    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<OrderStatus> NextStatuses(OrderStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(OrderStatus status) => NextStatuses(status).Count == 0;
}
