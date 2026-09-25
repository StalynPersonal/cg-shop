namespace CgShop.Domain.Common;

/// <summary>
/// Marca una entidad que pertenece a un tenant. El DbContext aplica automáticamente
/// el filtro global por <see cref="TenantId"/> y lo asigna al guardar.
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}
