using CgShop.Domain.Common;

namespace CgShop.Domain.Orders;

public sealed class OrderItem : Entity, ITenantEntity
{
    private OrderItem() { }

    internal OrderItem(Guid orderId, Guid variantId, string sku, string productName, string variantDescription,
        decimal unitPrice, int quantity, Guid productId = default)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad debe ser mayor que cero.");
        if (unitPrice <= 0)
            throw new DomainException("El precio unitario debe ser mayor que cero.");

        OrderId = orderId;
        ProductId = productId;
        VariantId = variantId;
        Sku = sku;
        ProductName = productName;
        VariantDescription = variantDescription;
        UnitPrice = unitPrice;
        Quantity = quantity;
    }

    public Guid TenantId { get; set; }
    public Guid OrderId { get; private set; }
    /// <summary>Producto al que pertenecía la variante (copia: el pedido no depende de que la variante siga existiendo).</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Variante vendida. Puede ya no existir (p. ej. tras cambiar la categoría del producto).</summary>
    public Guid VariantId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public string VariantDescription { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
