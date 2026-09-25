using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

public enum ReservationStatus
{
    Active = 1,
    Committed = 2,
    Released = 3
}

/// <summary>Stock apartado temporalmente para una orden pendiente de validación de pago.</summary>
public sealed class StockReservation : Entity, ITenantEntity
{
    private StockReservation() { }

    public StockReservation(Guid orderId, Guid variantId, int quantity, DateTime expiresAtUtc)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad reservada debe ser mayor que cero.");
        OrderId = orderId;
        VariantId = variantId;
        Quantity = quantity;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid TenantId { get; set; }
    public Guid OrderId { get; private set; }
    public Guid VariantId { get; private set; }
    public int Quantity { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public ReservationStatus Status { get; private set; } = ReservationStatus.Active;

    public bool IsExpired(DateTime nowUtc) => Status == ReservationStatus.Active && nowUtc >= ExpiresAtUtc;

    public void Commit() => Transition(ReservationStatus.Committed);
    public void Release() => Transition(ReservationStatus.Released);

    private void Transition(ReservationStatus target)
    {
        if (Status != ReservationStatus.Active)
            throw new DomainException($"La reserva ya está en estado {Status}.");
        Status = target;
    }
}
