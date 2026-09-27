namespace CgShop.Web.Services;

/// <summary>Cierre de sesión por inactividad (sección "Session" de appsettings).</summary>
public sealed class SessionTimeoutOptions
{
    public const string Section = "Session";

    /// <summary>Minutos sin actividad tras los cuales se cierra la sesión. Por defecto 15.</summary>
    public int IdleTimeoutMinutes { get; set; } = 15;

    /// <summary>Segundos de aviso antes del cierre. Por defecto 60.</summary>
    public int WarningSeconds { get; set; } = 60;

    /// <summary>Cada cuántos segundos, como máximo, se renueva la cookie mientras hay actividad.</summary>
    public int KeepAliveSeconds { get; set; } = 60;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(Math.Clamp(IdleTimeoutMinutes, 1, 24 * 60));

    public int EffectiveWarningSeconds =>
        Math.Clamp(WarningSeconds, 10, Math.Max(10, (int)IdleTimeout.TotalSeconds / 2));

    public int EffectiveKeepAliveSeconds =>
        Math.Clamp(KeepAliveSeconds, 15, Math.Max(15, (int)IdleTimeout.TotalSeconds / 3));
}
