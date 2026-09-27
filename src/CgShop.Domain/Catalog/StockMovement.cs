using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

public enum StockMovementType
{
    /// <summary>Stock con el que se creó la variante.</summary>
    InitialStock = 1,

    /// <summary>Ajuste manual (compra a proveedor, merma, conteo físico...). Requiere motivo.</summary>
    ManualAdjustment = 2,

    /// <summary>Salida por venta al validarse el pago del pedido.</summary>
    Sale = 3,

    /// <summary>Reingreso por cancelación de un pedido ya pagado.</summary>
    CancellationReturn = 4
}

public static class StockMovementTypeExtensions
{
    public static string DisplayName(this StockMovementType type) => type switch
    {
        StockMovementType.InitialStock => "Stock inicial",
        StockMovementType.ManualAdjustment => "Ajuste manual",
        StockMovementType.Sale => "Venta",
        StockMovementType.CancellationReturn => "Devolución por cancelación",
        _ => type.ToString()
    };
}

/// <summary>
/// Movimiento de inventario: registro inmutable de cada cambio en el stock físico de una variante:
/// quién, cuándo, cuánto, stock antes/después, motivo y pedido relacionado.
/// </summary>
public sealed class StockMovement : Entity, ITenantEntity
{
    private StockMovement() { }

    public StockMovement(ProductVariant variant, string productName, StockMovementType type, int quantity,
        int stockBefore, ActorInfo actor, DateTime atUtc, string? reason = null, string? orderNumber = null)
    {
        if (quantity == 0)
            throw new DomainException("Un movimiento de inventario no puede ser de cantidad cero.");
        if (type == StockMovementType.ManualAdjustment && string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Los ajustes manuales requieren un motivo.");

        TenantId = variant.TenantId;
        VariantId = variant.Id;
        ProductId = variant.ProductId;
        Sku = variant.Sku;
        ProductName = productName;
        Type = type;
        Quantity = quantity;
        StockBefore = stockBefore;
        StockAfter = stockBefore + quantity;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        OrderNumber = orderNumber;
        UserId = actor.UserId;
        UserName = actor.DisplayName;
        UserRole = actor.IsTenantAdmin ? Roles.TenantAdmin : actor.IsInRole(Roles.TenantStaff) ? Roles.TenantStaff : null;
        CreatedAtUtc = atUtc;
    }

    public Guid TenantId { get; set; }
    public Guid VariantId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; } = "";
    public string ProductName { get; private set; } = "";
    public StockMovementType Type { get; private set; }

    /// <summary>Positivo = entrada; negativo = salida.</summary>
    public int Quantity { get; private set; }

    public int StockBefore { get; private set; }
    public int StockAfter { get; private set; }
    public string? Reason { get; private set; }
    public string? OrderNumber { get; private set; }
    public string UserId { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string? UserRole { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
