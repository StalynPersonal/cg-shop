using System.Collections.Concurrent;
using CgShop.Application.Tenancy;
using CgShop.Domain.Tenants;
using MudBlazor;

namespace CgShop.Web.Theming;

/// <summary>Colores de marca que alimentan el tema de MudBlazor.</summary>
public sealed record TenantBranding(string PrimaryColor, string? SecondaryColor = null)
{
    /// <summary>Tema neutro de la plataforma (panel Super Admin y dominio raíz).</summary>
    public static TenantBranding Platform { get; } = new("#37474F", "#FF6F00");

    public static TenantBranding From(TenantInfo tenant) => new(tenant.PrimaryColor, tenant.SecondaryColor);
}

public interface ITenantThemeService
{
    MudTheme BuildTheme(TenantBranding branding);
}

/// <summary>
/// Construye un <see cref="MudTheme"/> a partir del color primario definido por el Super Admin al crear el tenant.
/// Cada tenant obtiene una instancia de tema propia (cacheada por combinación de colores):
/// nunca se muta un tema compartido, por lo que un tenant no puede afectar los colores de otro.
/// </summary>
public sealed class TenantThemeService : ITenantThemeService
{
    private readonly ConcurrentDictionary<TenantBranding, MudTheme> _cache = new();

    public MudTheme BuildTheme(TenantBranding branding) => _cache.GetOrAdd(Normalize(branding), Create);

    private static TenantBranding Normalize(TenantBranding b)
    {
        var primary = HexColor.IsValid(b.PrimaryColor)
            ? HexColor.Normalize(b.PrimaryColor)
            : TenantBranding.Platform.PrimaryColor;
        var secondary = HexColor.IsValid(b.SecondaryColor) ? HexColor.Normalize(b.SecondaryColor!) : null;
        return new TenantBranding(primary, secondary);
    }

    private static MudTheme Create(TenantBranding b)
    {
        var primary = b.PrimaryColor;
        var secondary = b.SecondaryColor ?? TenantBranding.Platform.SecondaryColor!;
        var primaryText = HexColor.ContrastText(primary);
        var secondaryText = HexColor.ContrastText(secondary);

        return new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = primary,
                PrimaryContrastText = primaryText,
                Secondary = secondary,
                SecondaryContrastText = secondaryText,
                AppbarBackground = primary,
                AppbarText = primaryText,
                Background = "#FAFAFA",
                DrawerBackground = "#FFFFFF"
            },
            PaletteDark = new PaletteDark
            {
                Primary = primary,
                PrimaryContrastText = primaryText,
                Secondary = secondary,
                SecondaryContrastText = secondaryText,
                AppbarBackground = primary,
                AppbarText = primaryText
            },
            LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" }
        };
    }
}
