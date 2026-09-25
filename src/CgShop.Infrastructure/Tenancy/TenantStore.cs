using CgShop.Application.Tenancy;
using CgShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CgShop.Infrastructure.Tenancy;

/// <summary>
/// Resuelve tenants por slug/ID con caché en memoria (5 min). La tabla Tenants no tiene
/// filtro global, por lo que puede consultarse sin tenant activo.
/// </summary>
public sealed class TenantStore(AppDbContext db, IMemoryCache cache) : ITenantStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static string SlugKey(string slug) => $"tenant:slug:{slug}";
    private static string IdKey(Guid id) => $"tenant:id:{id}";

    public async Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        if (cache.TryGetValue(SlugKey(normalized), out TenantInfo? cached))
            return cached;

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == normalized, ct);
        var info = tenant is null ? null : TenantInfo.From(tenant);
        if (info is not null)
            Store(info);
        return info;
    }

    public async Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (cache.TryGetValue(IdKey(id), out TenantInfo? cached))
            return cached;

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        var info = tenant is null ? null : TenantInfo.From(tenant);
        if (info is not null)
            Store(info);
        return info;
    }

    public void Invalidate(string slug)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        if (cache.TryGetValue(SlugKey(normalized), out TenantInfo? cached) && cached is not null)
            cache.Remove(IdKey(cached.Id));
        cache.Remove(SlugKey(normalized));
    }

    private void Store(TenantInfo info)
    {
        cache.Set(SlugKey(info.Slug), info, Ttl);
        cache.Set(IdKey(info.Id), info, Ttl);
    }
}
