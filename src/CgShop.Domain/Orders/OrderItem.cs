using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

public sealed class OrderItem : Entity, ITenantEntity
{
    private OrderItem() { }

    internal OrderItem(Guid orderId, Guid variantId, string sku, string productName, string variantDescription,
        decimal unitPrice, int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad debe ser mayor que cero.");
        if (unitPrice <= 0)
            throw new DomainException("El precio unitario debe ser mayor que cero.");

        OrderId = orderId;
        VariantId = variantId;
        Sku = sku;
        ProductName = productName;
        VariantDescription = variantDescription;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Guid TenantId { get; set; }
    public Guid OrderId { get; private set; }
    public Guid VariantId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public string VariantDescription { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
