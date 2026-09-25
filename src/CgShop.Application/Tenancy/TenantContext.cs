using CgShop.Domain.Tenants;

namespace CgShop.Application.Tenancy;

/// <summary>Instantánea inmutable del tenant resuelto para la petición/circuito actual.</summary>
public sealed record TenantInfo(
    Guid Id,
    string Slug,
    string Name,
    string PrimaryColor,
    string? SecondaryColor,
    string? LogoUrl,
    TenantStatus Status,
    string Currency,
    decimal TaxRate)
{
    public bool IsActive => Status == TenantStatus.Active;

    public static TenantInfo From(Tenant t) =>
        new(t.Id, t.Slug, t.Name, t.PrimaryColor, t.SecondaryColor, t.LogoUrl, t.Status, t.Currency, t.TaxRate);
}

/// <summary>Tenant activo en el scope actual (petición HTTP o circuito Blazor).</summary>
public interface ITenantContext
{
    TenantInfo? Tenant { get; }

    /// <summary>Zona de Super Admin (admin.*): sin tenant, filtros aplican con Guid.Empty.</summary>
    bool IsAdminHost { get; }

    /// <summary>Scope de sistema (jobs, seed): permite escrituras multi-tenant con IgnoreQueryFilters.</summary>
    bool IsSystemScope { get; }

    Guid? TenantId => Tenant?.Id;
    bool HasTenant => Tenant is not null;
}

public sealed class TenantContext : ITenantContext
{
    public TenantInfo? Tenant { get; private set; }
    public bool IsAdminHost { get; private set; }
    public bool IsSystemScope { get; private set; }

    public void SetTenant(TenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Tenant is not null && Tenant.Id != tenant.Id)
            throw new InvalidOperationException("El tenant del scope actual ya fue establecido y no puede cambiarse.");
        Tenant = tenant;
    }

    public void SetAdminHost() => IsAdminHost = true;

    public void EnterSystemScope() => IsSystemScope = true;
}
