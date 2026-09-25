namespace CgShop.Application.Tenancy;

/// <summary>Búsqueda (cacheada) de tenants por subdominio.</summary>
public interface ITenantStore
{
    Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default);
    void Invalidate(string slug);
}
