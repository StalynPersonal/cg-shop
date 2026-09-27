using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Application.Catalog;

public sealed record ProductImageDto(Guid Id, string Url, int SortOrder, int Width, int Height, long SizeBytes,
    string FileName)
{
    public bool IsMain => SortOrder == 0;
}

/// <summary>Galería de fotos de productos: subida validada, orden, foto principal y eliminación.</summary>
public sealed class ProductImageService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    IFileStorage storage,
    TimeProvider clock,
    IOptions<CatalogOptions> options,
    ILogger<ProductImageService> logger)
{
    public const string Folder = "products";

    /// <summary>URL pública (por tienda) para mostrar una foto.</summary>
    public static string UrlFor(Guid imageId) => $"/imagenes/{imageId:N}";

    public async Task<IReadOnlyList<ProductImageDto>> ListAsync(Guid productId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.AsNoTracking().Include(p => p.Images)
                          .FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw new NotFoundException("Producto no encontrado.");
        return product.Images.Select(ToDto).ToList();
    }

    /// <summary>Sube una foto: valida formato real, tamaño y dimensiones antes de guardarla.</summary>
    public async Task<ProductImageDto> UploadAsync(Guid productId, string fileName, Stream content, long declaredSize,
        ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        var tenant = Guard.RequireTenant(tenantContext);
        var opts = options.Value;

        if (declaredSize > opts.MaxImageBytes)
            throw new ValidationException([$"'{Path.GetFileName(fileName)}' supera el máximo de {opts.MaxImageBytes / 1024d / 1024d:0.#} MB."]);

        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(content, buffer, opts.MaxImageBytes, fileName, ct);
        var info = ProductImageValidator.Validate(fileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length), opts);

        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw new NotFoundException("Producto no encontrado.");
        if (product.Images.Count >= opts.MaxImagesPerProduct)
            throw new DomainException($"El producto ya tiene el máximo de {opts.MaxImagesPerProduct} fotos.");

        buffer.Position = 0;
        var path = await storage.SaveAsync(tenant.Id, Folder, info.Extension, buffer, ct);
        try
        {
            var safeName = Path.GetFileName(fileName);
            var image = product.AddImage(path, info.ContentType, buffer.Length, info.Width, info.Height,
                safeName.Length > 200 ? safeName[^200..] : safeName, opts.MaxImagesPerProduct,
                clock.GetUtcNow().UtcDateTime);
            db.ProductImages.Add(image);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Foto {Image} agregada al producto {Product} por {User}", image.Id, productId, actor.UserId);
            return ToDto(image);
        }
        catch
        {
            await storage.DeleteAsync(tenant.Id, path, CancellationToken.None);
            throw;
        }
    }

    public Task SetMainAsync(Guid productId, Guid imageId, ActorInfo actor, CancellationToken ct = default) =>
        MutateAsync(productId, actor, p => p.SetMainImage(imageId), ct);

    public Task MoveAsync(Guid productId, Guid imageId, int offset, ActorInfo actor, CancellationToken ct = default) =>
        MutateAsync(productId, actor, p => p.MoveImage(imageId, offset), ct);

    public async Task DeleteAsync(Guid productId, Guid imageId, ActorInfo actor, CancellationToken ct = default)
    {
        Guard.RequireStaff(actor);
        var tenant = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw new NotFoundException("Producto no encontrado.");
        var removed = product.RemoveImage(imageId);
        db.ProductImages.Remove(removed);
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(tenant.Id, removed.StoragePath, CancellationToken.None);
    }

    /// <summary>Abre una foto de la tienda activa (para servirla por HTTP).</summary>
    public async Task<(Stream Content, string ContentType)?> OpenAsync(Guid imageId, CancellationToken ct = default)
    {
        var tenant = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var image = await db.ProductImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null)
            return null;
        var stream = await storage.OpenAsync(tenant.Id, image.StoragePath, ct);
        return stream is null ? null : (stream, image.ContentType);
    }

    private async Task MutateAsync(Guid productId, ActorInfo actor, Action<Domain.Catalog.Product> action,
        CancellationToken ct)
    {
        Guard.RequireStaff(actor);
        await using var db = dbFactory.CreateDbContext();
        var product = await db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw new NotFoundException("Producto no encontrado.");
        action(product);
        await db.SaveChangesAsync(ct);
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long max, string fileName,
        CancellationToken ct)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > max)
                throw new ValidationException([$"'{Path.GetFileName(fileName)}' supera el máximo de {max / 1024d / 1024d:0.#} MB."]);
            await target.WriteAsync(chunk.AsMemory(0, read), ct);
        }
    }

    internal static ProductImageDto ToDto(Domain.Catalog.ProductImage i) =>
        new(i.Id, UrlFor(i.Id), i.SortOrder, i.Width, i.Height, i.SizeBytes, i.OriginalFileName);
}
