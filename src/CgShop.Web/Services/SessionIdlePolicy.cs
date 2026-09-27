namespace CgShop.Web.Services;

/// <summary>
/// Regla de servidor del cierre por inactividad: la cookie se emite/renueva con cada actividad
/// (<c>/cuenta/mantener-sesion</c>); si pasó más tiempo que el configurado desde su emisión, se rechaza.
/// </summary>
public static class SessionIdlePolicy
{
    /// <summary>Vida máxima de la cookie; la inactividad real se controla con <see cref="IsExpired"/>.</summary>
    public static readonly TimeSpan MaxCookieLifetime = TimeSpan.FromHours(24);

    public static bool IsExpired(DateTimeOffset? issuedUtc, DateTimeOffset nowUtc, TimeSpan idleTimeout) =>
        issuedUtc is { } issued && nowUtc - issued > idleTimeout;
}
