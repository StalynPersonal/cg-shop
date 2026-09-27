using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Domain.Common;
using CgShop.Domain.Platform;

namespace CgShop.Application.Platform;

public sealed record PlatformSettingsView(
    PlatformSettingsValues Values,
    PlatformSettingsValues Defaults,
    bool IsCustomized,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// Configuración de negocio vigente (editable por el Super Admin, con caché).
/// Si nunca se guardó, usa los valores de appsettings como predeterminados.
/// </summary>
public interface IPlatformSettingsProvider
{
    /// <summary>Valores vigentes (desde caché).</summary>
    PlatformSettingsValues Current { get; }

    Task<PlatformSettingsView> GetAsync(CancellationToken ct = default);

    Task<PlatformSettingsView> UpdateAsync(PlatformSettingsValues values, ActorInfo actor, CancellationToken ct = default);

    /// <summary>Vuelve a los valores predeterminados de appsettings.</summary>
    Task<PlatformSettingsView> ResetAsync(ActorInfo actor, CancellationToken ct = default);
}

public static class PlatformSettingsMapping
{
    private const long Mb = 1024 * 1024;

    public static OrderOptions ToOrderOptions(this PlatformSettingsValues v) => new()
    {
        ReservationHours = v.ReservationHours,
        ExpiryCheckMinutes = v.ExpiryCheckMinutes,
        MaxReceiptBytes = v.MaxReceiptMb * Mb
    };

    public static CatalogOptions ToCatalogOptions(this PlatformSettingsValues v) => new()
    {
        MaxImageBytes = v.MaxImageMb * Mb,
        MaxImagesPerProduct = v.MaxImagesPerProduct,
        MinImageDimension = v.MinImageDimension,
        MaxImageDimension = v.MaxImageDimension
    };

    /// <summary>Predeterminados a partir de las secciones de appsettings.</summary>
    public static PlatformSettingsValues FromOptions(OrderOptions orders, CatalogOptions catalog, int idleMinutes,
        int warningSeconds) =>
        new((int)orders.ReservationHours, orders.ExpiryCheckMinutes, (int)Math.Max(1, orders.MaxReceiptBytes / Mb),
            (int)Math.Max(1, catalog.MaxImageBytes / Mb), catalog.MaxImagesPerProduct, catalog.MinImageDimension,
            catalog.MaxImageDimension, idleMinutes, warningSeconds);
}
