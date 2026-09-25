using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CgShop.Application.Tenants;

/// <summary>
/// Panel global del Super Admin: alta de empresas (con color primario obligatorio), branding,
/// suspensión y métricas. Es el ÚNICO servicio de negocio que usa <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class SuperAdminTenantService(
    IAppDbContextFactory dbFactory,
    ITenantStore tenantStore,
    ITenantAdminProvisioner provisioner,
    TimeProvider clock,
    ILogger<SuperAdminTenantService> logger)
{
    public async Task<IReadOnlyList<TenantSummaryDto>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var tenants = db.Tenants.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            tenants = tenants.Where(t => t.Name.Contains(term) || t.Slug.Contains(term));
        }

        var list = await tenants.OrderBy(t => t.Name).ToListAsync(ct);
        var products = await db.Products.IgnoreQueryFilters().AsNoTracking().GroupBy(p => p.TenantId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var orders = await db.Orders.IgnoreQueryFilters().AsNoTracking().GroupBy(o => o.TenantId)
            .Select(g => new
            {
                g.Key, Count = g.Count(),
                Pending = g.Count(o => o.Status == OrderStatus.PendingPaymentValidation)
            })
            .ToDictionaryAsync(x => x.Key, ct);

        return list.Select(t => new TenantSummaryDto(t.Id, t.Name, t.Slug, t.PrimaryColor, t.SecondaryColor,
            t.LogoUrl, t.Status, t.ContactEmail, t.TaxRate, t.CreatedAtUtc, products.GetValueOrDefault(t.Id),
            orders.TryGetValue(t.Id, out var o) ? o.Count : 0, o?.Pending ?? 0)).ToList();
    }

    public async Task<PlatformStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return new PlatformStatsDto(
            await db.Tenants.CountAsync(ct),
            await db.Tenants.CountAsync(t => t.Status == TenantStatus.Active, ct),
            await db.Products.IgnoreQueryFilters().CountAsync(ct),
            await db.Orders.IgnoreQueryFilters().CountAsync(ct),
            await db.Orders.IgnoreQueryFilters().CountAsync(o => o.Status == OrderStatus.PendingPaymentValidation, ct));
    }

    public async Task<TenantSummaryDto?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        (await ListAsync(null, ct)).FirstOrDefault(t => t.Id == tenantId);

    /// <summary>Alta de empresa + usuario administrador. El color primario es obligatorio.</summary>
    public async Task<Guid> CreateAsync(CreateTenantRequest request, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireSuperAdmin(actor);
        var errors = new List<string>();
        if (!HexColor.IsValid(request.PrimaryColor))
            errors.Add("Debe seleccionar el color primario de la tienda (#RRGGBB).");
        if (string.IsNullOrWhiteSpace(request.AdminEmail) || !request.AdminEmail.Contains('@'))
            errors.Add("El correo del administrador es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 8)
            errors.Add("La contraseña del administrador debe tener al menos 8 caracteres.");
        if (string.IsNullOrWhiteSpace(request.AdminFullName))
            errors.Add("El nombre del administrador es obligatorio.");
        if (errors.Count > 0)
            throw new ValidationException(errors);

        var tenant = Tenant.Create(request.Name, request.Slug, request.PrimaryColor, request.ContactEmail,
            clock.GetUtcNow().UtcDateTime);
        tenant.SetBranding(request.PrimaryColor, request.SecondaryColor, request.LogoUrl);
        tenant.SetTaxRate(request.TaxRate);

        await using (var db = dbFactory.CreateDbContext())
        {
            if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, ct))
                throw new ValidationException([$"El subdominio '{tenant.Slug}' ya está en uso."]);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await provisioner.ProvisionAsync(tenant.Id, request.AdminFullName, request.AdminEmail,
                request.AdminPassword, ct);
        }
        catch
        {
            // Compensación: no dejar tenants sin administrador.
            await using var db = dbFactory.CreateDbContext();
            await db.Tenants.Where(t => t.Id == tenant.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }

        logger.LogInformation("Tenant {Slug} creado por {User} con color {Color}", tenant.Slug, actor.UserId,
            tenant.PrimaryColor);
        return tenant.Id;
    }

    public async Task UpdateBrandingAsync(Guid tenantId, UpdateBrandingRequest request, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireSuperAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new NotFoundException("Empresa no encontrada.");
        tenant.Rename(request.Name);
        tenant.SetBranding(request.PrimaryColor, request.SecondaryColor, request.LogoUrl);
        tenant.SetTaxRate(request.TaxRate);
        await db.SaveChangesAsync(ct);
        tenantStore.Invalidate(tenant.Slug);
    }

    public async Task SetStatusAsync(Guid tenantId, TenantStatus status, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireSuperAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new NotFoundException("Empresa no encontrada.");
        if (status == TenantStatus.Active) tenant.Activate();
        else tenant.Suspend();
        await db.SaveChangesAsync(ct);
        tenantStore.Invalidate(tenant.Slug);
    }
}
