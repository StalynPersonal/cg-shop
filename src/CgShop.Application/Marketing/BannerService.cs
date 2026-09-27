using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Marketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Application.Marketing;

public sealed record BannerDto(Guid Id, string Url, int SortOrder, int Width, int Height, long SizeBytes,
    string? Title, string? Subtitle, string? ButtonText, string? LinkUrl, bool IsActive);

public sealed class BannerContentDto
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? ButtonText { get; set; }
    public string? LinkUrl { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Carrusel de portada: el propietario sube, ordena, edita y retira banners; la tienda muestra los activos.</summary>
public sealed class BannerService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    IFileStorage storage,
    TimeProvider clock,
    IOptions<CatalogOptions> options,
    ILogger<BannerService> logger)
{
    public const string Folder = "banners";

    /// <summary>Ancho mínimo para que el banner se vea nítido en pantallas grandes.</summary>
    public const int MinWidth = 1000;

    /// <summary>Proporción mínima ancho/alto (3:2): el banner debe ser horizontal.</summary>
    public const double MinAspectRatio = 1.5;

    public static string UrlFor(Guid id) => $"/banners/{id:N}";

    /// <summary>Banners activos en orden, para la portada.</summary>
    public async Task<IReadOnlyList<BannerDto>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var banners = await db.StoreBanners.AsNoTracking().Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder).ToListAsync(ct);
        return banners.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<BannerDto>> ListAsync(ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var banners = await db.StoreBanners.AsNoTracking().OrderBy(b => b.SortOrder).ToListAsync(ct);
        return banners.Select(ToDto).ToList();
    }

    /// <summary>Sube un banner: mismo control de formato real y peso que las fotos, y además debe ser horizontal.</summary>
    public async Task<BannerDto> UploadAsync(string fileName, Stream content, long declaredSize, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        var tenant = Guard.RequireTenant(tenantContext);
        var opts = options.Value;
        var name = Path.GetFileName(fileName);
        if (declaredSize > opts.MaxImageBytes)
            throw new ValidationException([$"'{name}' supera el máximo de {opts.MaxImageBytes / 1024d / 1024d:0.#} MB."]);

        using var buffer = new MemoryStream();
        await ProductImageService.CopyWithLimitAsync(content, buffer, opts.MaxImageBytes, fileName, ct);
        var info = ProductImageValidator.Validate(fileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length), opts);
        if (info.Width < MinWidth)
            throw new ValidationException([$"'{name}' mide {info.Width} px de ancho; el banner necesita al menos {MinWidth} px."]);
        if (info.Width < info.Height * MinAspectRatio)
            throw new ValidationException([$"'{name}' debe ser horizontal (por ejemplo 1920×640 o 1500×1000); mide {info.Width}×{info.Height} px."]);

        await using var db = dbFactory.CreateDbContext();
        var existing = await db.StoreBanners.Select(b => b.SortOrder).ToListAsync(ct);
        if (existing.Count >= StoreBanner.MaxPerStore)
            throw new DomainException($"La portada admite hasta {StoreBanner.MaxPerStore} banners. Elimine uno para subir otro.");

        buffer.Position = 0;
        var path = await storage.SaveAsync(tenant.Id, Folder, info.Extension, buffer, ct);
        try
        {
            var banner = StoreBanner.Create(path, info.ContentType, buffer.Length, info.Width, info.Height,
                existing.Count == 0 ? 0 : existing.Max() + 1, clock.GetUtcNow().UtcDateTime);
            db.StoreBanners.Add(banner);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Banner {Banner} subido por {User}", banner.Id, actor.UserId);
            return ToDto(banner);
        }
        catch
        {
            await storage.DeleteAsync(tenant.Id, path, CancellationToken.None);
            throw;
        }
    }

    public async Task<BannerDto> UpdateAsync(Guid id, BannerContentDto content, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var banner = await FindAsync(db, id, ct);
        banner.UpdateContent(content.Title, content.Subtitle, content.ButtonText, content.LinkUrl, content.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(banner);
    }

    /// <summary>Mueve el banner una posición antes (-1) o después (+1) en el carrusel.</summary>
    public async Task MoveAsync(Guid id, int offset, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        await using var db = dbFactory.CreateDbContext();
        var ordered = await db.StoreBanners.OrderBy(b => b.SortOrder).ToListAsync(ct);
        var index = ordered.FindIndex(b => b.Id == id);
        if (index < 0)
            throw new NotFoundException("Banner no encontrado.");
        var target = Math.Clamp(index + Math.Sign(offset), 0, ordered.Count - 1);
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        var tenant = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var banner = await FindAsync(db, id, ct);
        db.StoreBanners.Remove(banner);
        var rest = await db.StoreBanners.Where(b => b.Id != id).OrderBy(b => b.SortOrder).ToListAsync(ct);
        for (var i = 0; i < rest.Count; i++)
            rest[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(tenant.Id, banner.StoragePath, CancellationToken.None);
    }

    /// <summary>Abre la imagen de un banner de la tienda activa (para servirla por HTTP).</summary>
    public async Task<(Stream Content, string ContentType)?> OpenAsync(Guid id, CancellationToken ct = default)
    {
        var tenant = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var banner = await db.StoreBanners.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (banner is null)
            return null;
        var stream = await storage.OpenAsync(tenant.Id, banner.StoragePath, ct);
        return stream is null ? null : (stream, banner.ContentType);
    }

    private static async Task<StoreBanner> FindAsync(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.StoreBanners.FirstOrDefaultAsync(b => b.Id == id, ct)
        ?? throw new NotFoundException("Banner no encontrado.");

    private static BannerDto ToDto(StoreBanner b) =>
        new(b.Id, UrlFor(b.Id), b.SortOrder, b.Width, b.Height, b.SizeBytes, b.Title, b.Subtitle, b.ButtonText,
            b.LinkUrl, b.IsActive);
}
