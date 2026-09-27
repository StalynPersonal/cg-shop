using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Platform;
using CgShop.Domain.Common;
using CgShop.Domain.Platform;
using CgShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CgShop.Infrastructure.Platform;

/// <summary>
/// Lee y guarda la configuración de la plataforma. Los valores se cachean (1 min) y la caché
/// se invalida al guardar, de modo que los cambios aplican sin reiniciar la aplicación.
/// </summary>
public sealed class PlatformSettingsStore(
    IDbContextFactoryAdapter dbFactory,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<PlatformSettingsStore> logger) : IPlatformSettingsProvider
{
    private const string CacheKey = "platform:settings";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    /// <summary>Valores predeterminados de fábrica (se usan mientras el Super Admin no guarde otros).</summary>
    public PlatformSettingsValues Defaults { get; } = PlatformSettingsMapping.FromOptions(
        new OrderOptions(), new CatalogOptions(), idleMinutes: 15, warningSeconds: 60);

    public PlatformSettingsValues Current =>
        cache.GetOrCreate(CacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            try
            {
                using var db = dbFactory.Create();
                return db.PlatformSettings.AsNoTracking().FirstOrDefault()?.ToValues() ?? Defaults;
            }
            catch (Exception ex)
            {
                // Base no disponible (p. ej. al arrancar): usar predeterminados y reintentar pronto.
                logger.LogWarning(ex, "No se pudo leer la configuración de la plataforma; se usan valores predeterminados");
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
                return Defaults;
            }
        })!;

    public async Task<PlatformSettingsView> GetAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.Create();
        var row = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return ToView(row);
    }

    public async Task<PlatformSettingsView> UpdateAsync(PlatformSettingsValues values, ActorInfo actor,
        CancellationToken ct = default)
    {
        if (!actor.IsInRole(Roles.SuperAdmin))
            throw new ForbiddenException("Solo el Super Admin puede cambiar la configuración de la plataforma.");

        await using var db = dbFactory.Create();
        var row = await db.PlatformSettings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = PlatformSettings.Create(Defaults);
            db.PlatformSettings.Add(row);
        }

        row.Update(values, actor, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
        logger.LogInformation("Configuración de la plataforma actualizada por {User}: {@Values}", actor.UserId, values);
        return ToView(row);
    }

    public async Task<PlatformSettingsView> ResetAsync(ActorInfo actor, CancellationToken ct = default)
    {
        if (!actor.IsInRole(Roles.SuperAdmin))
            throw new ForbiddenException("Solo el Super Admin puede cambiar la configuración de la plataforma.");
        await using var db = dbFactory.Create();
        await db.PlatformSettings.ExecuteDeleteAsync(ct);
        cache.Remove(CacheKey);
        return ToView(null);
    }

    private PlatformSettingsView ToView(PlatformSettings? row) =>
        new(row?.ToValues() ?? Defaults, Defaults, row is not null, row?.UpdatedAtUtc, row?.UpdatedBy);

}

/// <summary>Crea contextos sin tenant para datos globales de la plataforma.</summary>
public interface IDbContextFactoryAdapter
{
    AppDbContext Create();
}

internal sealed class PlatformDbContextFactory(string connectionString) : IDbContextFactoryAdapter
{
    private readonly DbContextOptions<AppDbContext> _options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3))
        .Options;

    public AppDbContext Create() => new(_options, new CgShop.Application.Tenancy.TenantContext());
}
