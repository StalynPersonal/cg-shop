using CgShop.Application.Tenancy;

namespace CgShop.Web.Tenancy;

public enum HostKind
{
    /// <summary>Dominio raíz (landing de la plataforma).</summary>
    Root,

    /// <summary>Subdominio de una tienda.</summary>
    Tenant,

    /// <summary>admin.* — panel del Super Admin.</summary>
    Admin
}

/// <summary>Contexto del host, cascadeado a todos los componentes del circuito.</summary>
public sealed record HostContext(HostKind Kind, TenantInfo? Tenant)
{
    public bool IsTenant => Kind == HostKind.Tenant && Tenant is not null;
    public bool IsAdmin => Kind == HostKind.Admin;
    public string Currency => Tenant?.Currency ?? "DOP";
}

/// <summary>Página de la tienda (solo en subdominio de tenant).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TenantHostAttribute : Attribute;

/// <summary>Página del panel Super Admin (solo en admin.*).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AdminHostAttribute : Attribute;
