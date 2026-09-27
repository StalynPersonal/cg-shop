using CgShop.Domain.Common;

namespace CgShop.Domain.Platform;

/// <summary>
/// Parámetros de negocio de toda la plataforma, editables por el Super Admin (fila única).
/// Mientras no se guarde, rigen los valores predeterminados de fábrica.
/// </summary>
public sealed class PlatformSettings
{
    /// <summary>Identificador fijo de la única fila de configuración.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-00000000c0f1");

    private PlatformSettings() { }

    public Guid Id { get; private set; } = SingletonId;

    // Pedidos
    public int ReservationHours { get; private set; }
    public int ExpiryCheckMinutes { get; private set; }
    public int MaxReceiptMb { get; private set; }

    // Fotos de productos
    public int MaxImageMb { get; private set; }
    public int MaxImagesPerProduct { get; private set; }
    public int MinImageDimension { get; private set; }
    public int MaxImageDimension { get; private set; }

    // Sesión
    public int IdleTimeoutMinutes { get; private set; }
    public int SessionWarningSeconds { get; private set; }

    public DateTime? UpdatedAtUtc { get; private set; }
    public string? UpdatedBy { get; private set; }

    public static PlatformSettings Create(PlatformSettingsValues values)
    {
        var settings = new PlatformSettings();
        settings.Apply(values);
        return settings;
    }

    public void Update(PlatformSettingsValues values, ActorInfo actor, DateTime nowUtc)
    {
        if (!actor.IsInRole(Roles.SuperAdmin))
            throw new DomainException("Solo el Super Admin puede cambiar la configuración de la plataforma.");
        Apply(values);
        UpdatedAtUtc = nowUtc;
        UpdatedBy = actor.DisplayName;
    }

    public PlatformSettingsValues ToValues() => new(ReservationHours, ExpiryCheckMinutes, MaxReceiptMb, MaxImageMb,
        MaxImagesPerProduct, MinImageDimension, MaxImageDimension, IdleTimeoutMinutes, SessionWarningSeconds);

    private void Apply(PlatformSettingsValues v)
    {
        var errors = v.Validate();
        if (errors.Count > 0)
            throw new DomainException(string.Join(" ", errors));

        ReservationHours = v.ReservationHours;
        ExpiryCheckMinutes = v.ExpiryCheckMinutes;
        MaxReceiptMb = v.MaxReceiptMb;
        MaxImageMb = v.MaxImageMb;
        MaxImagesPerProduct = v.MaxImagesPerProduct;
        MinImageDimension = v.MinImageDimension;
        MaxImageDimension = v.MaxImageDimension;
        IdleTimeoutMinutes = v.IdleTimeoutMinutes;
        SessionWarningSeconds = v.SessionWarningSeconds;
    }
}

/// <summary>Valores de configuración con sus rangos permitidos.</summary>
public sealed record PlatformSettingsValues(
    int ReservationHours,
    int ExpiryCheckMinutes,
    int MaxReceiptMb,
    int MaxImageMb,
    int MaxImagesPerProduct,
    int MinImageDimension,
    int MaxImageDimension,
    int IdleTimeoutMinutes,
    int SessionWarningSeconds)
{
    public static class Limits
    {
        public const int ReservationHoursMin = 1, ReservationHoursMax = 720;       // hasta 30 días
        public const int ExpiryCheckMin = 1, ExpiryCheckMax = 1440;
        public const int FileMbMin = 1, FileMbMax = 50;
        public const int ImagesMin = 1, ImagesMax = 50;
        public const int DimensionMin = 50, DimensionMax = 12000;
        public const int IdleMin = 1, IdleMax = 1440;
        public const int WarningMin = 10, WarningMax = 600;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        void Range(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                errors.Add($"{name} debe estar entre {min} y {max}.");
        }

        Range(ReservationHours, Limits.ReservationHoursMin, Limits.ReservationHoursMax, "Las horas de reserva");
        Range(ExpiryCheckMinutes, Limits.ExpiryCheckMin, Limits.ExpiryCheckMax, "La frecuencia de revisión de reservas");
        Range(MaxReceiptMb, Limits.FileMbMin, Limits.FileMbMax, "El tamaño máximo del comprobante (MB)");
        Range(MaxImageMb, Limits.FileMbMin, Limits.FileMbMax, "El tamaño máximo de foto (MB)");
        Range(MaxImagesPerProduct, Limits.ImagesMin, Limits.ImagesMax, "Las fotos por producto");
        Range(MinImageDimension, Limits.DimensionMin, Limits.DimensionMax, "La dimensión mínima de foto");
        Range(MaxImageDimension, Limits.DimensionMin, Limits.DimensionMax, "La dimensión máxima de foto");
        if (MinImageDimension > MaxImageDimension)
            errors.Add("La dimensión mínima de foto no puede superar la máxima.");
        Range(IdleTimeoutMinutes, Limits.IdleMin, Limits.IdleMax, "Los minutos de inactividad");
        Range(SessionWarningSeconds, Limits.WarningMin, Limits.WarningMax, "Los segundos de aviso");
        if (SessionWarningSeconds >= IdleTimeoutMinutes * 60)
            errors.Add("El aviso de cierre debe ser menor que el tiempo de inactividad.");
        return errors;
    }
}
