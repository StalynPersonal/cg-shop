using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

/// <summary>
/// SKU vendible (talla/color/volumen). Controla stock físico (<see cref="StockOnHand"/>)
/// y stock apartado por órdenes pendientes de validación (<see cref="StockReserved"/>).
/// </summary>
public sealed class ProductVariant : Entity, ITenantEntity
{
    private ProductVariant() { }

    internal ProductVariant(Guid productId, string sku, decimal price, int initialStock, string? size,
        string? color, int? volumeMl)
    {
        if (string.IsNullOrWhiteSpace(sku))
            throw new DomainException("El SKU es obligatorio.");
        if (price <= 0)
            throw new DomainException("El precio debe ser mayor que cero.");
        if (initialStock < 0)
            throw new DomainException("El stock inicial no puede ser negativo.");

        ProductId = productId;
        Sku = sku;
        Price = price;
        StockOnHand = initialStock;
        Size = string.IsNullOrWhiteSpace(size) ? null : size.Trim();
        Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        VolumeMl = volumeMl;
    }

    public Guid TenantId { get; set; }
    public Guid ProductId { get; private set; }
    public Product? Product { get; private set; }
    public string Sku { get; private set; } = "";
    public string? Size { get; private set; }
    public string? Color { get; private set; }
    public int? VolumeMl { get; private set; }
    public decimal Price { get; private set; }
    public int StockOnHand { get; private set; }
    public int StockReserved { get; private set; }

    /// <summary>Token de concurrencia optimista (SQL Server rowversion).</summary>
    public byte[] RowVersion { get; private set; } = [];

    public int Available => StockOnHand - StockReserved;

    public string Description => string.Join(" / ",
        new[] { Size is null ? null : $"Talla {Size}", Color, VolumeMl is null ? null : $"{VolumeMl} ml" }
            .Where(s => s is not null));

    public void ChangePrice(decimal price)
    {
        if (price <= 0)
            throw new DomainException("El precio debe ser mayor que cero.");
        Price = price;
    }

    /// <summary>Aparta stock para una orden pendiente de validación.</summary>
    public void Reserve(int quantity)
    {
        EnsurePositive(quantity);
        if (quantity > Available)
            throw new DomainException(
                $"Stock insuficiente para {Sku}: disponible {Available}, solicitado {quantity}.");
        StockReserved += quantity;
    }

    /// <summary>Libera stock apartado (pago rechazado, orden cancelada o expirada).</summary>
    public void Release(int quantity)
    {
        EnsurePositive(quantity);
        if (quantity > StockReserved)
            throw new DomainException($"No se puede liberar más stock del reservado en {Sku}.");
        StockReserved -= quantity;
    }

    /// <summary>Convierte una reserva en salida definitiva de inventario (pago validado).</summary>
    public void CommitReservation(int quantity)
    {
        EnsurePositive(quantity);
        if (quantity > StockReserved)
            throw new DomainException($"No existe reserva suficiente en {Sku} para confirmar {quantity} unidades.");
        StockReserved -= quantity;
        StockOnHand -= quantity;
    }

    /// <summary>Ajuste manual de inventario (entrada positiva o salida negativa).</summary>
    public void AdjustStock(int delta)
    {
        if (StockOnHand + delta < StockReserved)
            throw new DomainException(
                $"El ajuste dejaría el stock de {Sku} por debajo de lo reservado ({StockReserved}).");
        StockOnHand += delta;
    }

    private static void EnsurePositive(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("La cantidad debe ser mayor que cero.");
    }
}
