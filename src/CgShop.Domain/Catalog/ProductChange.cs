using CgShop.Domain.Common;

namespace CgShop.Domain.Catalog;

public enum ProductChangeType
{
    Created = 1,
    Updated = 2,
    CategoryChanged = 3,
    Activated = 4,
    Deactivated = 5,
    VariantAdded = 6,
    VariantRemoved = 7,
    PriceChanged = 8,
    Deleted = 9
}

public static class ProductChangeTypeExtensions
{
    public static string DisplayName(this ProductChangeType type) => type switch
    {
        ProductChangeType.Created => "Creado",
        ProductChangeType.Updated => "Datos modificados",
        ProductChangeType.CategoryChanged => "Cambio de categoría",
        ProductChangeType.Activated => "Activado",
        ProductChangeType.Deactivated => "Desactivado",
        ProductChangeType.VariantAdded => "Variante agregada",
        ProductChangeType.VariantRemoved => "Variante eliminada",
        ProductChangeType.PriceChanged => "Cambio de precio",
        ProductChangeType.Deleted => "Eliminado",
        _ => type.ToString()
    };
}

/// <summary>
/// Historial inmutable de cambios de un producto: qué se cambió, quién y cuándo.
/// Sin FK al producto: se conserva aunque el producto se elimine.
/// </summary>
public sealed class ProductChange : Entity, ITenantEntity
{
    public const int DetailsMax = 2000;

    private ProductChange() { }

    public ProductChange(Product product, ProductChangeType type, string? details, ActorInfo actor, DateTime atUtc)
    {
        TenantId = product.TenantId;
        ProductId = product.Id;
        ProductName = product.Name;
        Type = type;
        Details = string.IsNullOrWhiteSpace(details)
            ? null
            : details.Length > DetailsMax ? details[..(DetailsMax - 1)] + "…" : details;
        UserId = actor.UserId;
        UserName = actor.DisplayName;
        UserRole = actor.IsTenantAdmin ? Roles.TenantAdmin : actor.IsInRole(Roles.TenantStaff) ? Roles.TenantStaff : null;
        CreatedAtUtc = atUtc;
    }

    public Guid TenantId { get; set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = "";
    public ProductChangeType Type { get; private set; }
    public string? Details { get; private set; }
    public string UserId { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string? UserRole { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
