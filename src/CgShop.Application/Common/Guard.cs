using CgShop.Application.Tenancy;
using CgShop.Domain.Common;

namespace CgShop.Application.Common;

internal static class Guard
{
    public static TenantInfo RequireTenant(ITenantContext context) =>
        context.Tenant ?? throw new ForbiddenException("No hay una tienda activa para esta operación.");

    public static void RequireStaff(ActorInfo actor)
    {
        if (!actor.IsTenantStaff)
            throw new ForbiddenException("Se requiere personal de la tienda.");
    }

    public static void RequireTenantAdmin(ActorInfo actor)
    {
        if (!actor.IsTenantAdmin)
            throw new ForbiddenException("Se requiere el propietario/administrador de la tienda.");
    }

    public static void RequireSuperAdmin(ActorInfo actor)
    {
        if (!actor.IsInRole(Roles.SuperAdmin))
            throw new ForbiddenException("Se requiere Super Admin.");
    }

    public static (int Page, int PageSize) Paging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
}
