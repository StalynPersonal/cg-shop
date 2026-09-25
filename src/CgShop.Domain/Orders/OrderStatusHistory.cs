using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

/// <summary>Registro de auditoría de cada cambio de estado (quién, cuándo, por qué, con qué comprobante).</summary>
public sealed class OrderStatusHistory : Entity, ITenantEntity
{
    private OrderStatusHistory() { }

    internal OrderStatusHistory(Guid orderId, OrderStatus? fromStatus, OrderStatus toStatus, ActorInfo actor,
        string? note, Guid? receiptId, DateTime atUtc)
    {
        OrderId = orderId;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ChangedByUserId = actor.UserId;
        ChangedByName = actor.DisplayName;
        Note = note;
        ReceiptId = receiptId;
        ChangedAtUtc = atUtc;
    }

    public Guid TenantId { get; set; }
    public Guid OrderId { get; private set; }
    public OrderStatus? FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public string ChangedByUserId { get; private set; } = "";
    public string ChangedByName { get; private set; } = "";
    public string? Note { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }
}
