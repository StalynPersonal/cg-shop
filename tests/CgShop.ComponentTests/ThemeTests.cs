using Bunit;
using CgShop.Application.Tenancy;
using CgShop.Domain.Tenants;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;
using CgShop.Web.Theming;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Utilities;

namespace CgShop.ComponentTests;

/// <summary>Verifica que el color primario definido por el Super Admin se inyecta en el tema de MudBlazor.</summary>
public sealed class ThemeTests : MudTestContext
{

    private static string Hex(MudColor color) => color.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant();

    [Fact]
    public void Theme_service_builds_primary_palette_from_tenant_color_for_100_tenants()
    {
        var service = new TenantThemeService();

        foreach (var tenant in TestData.Tenants())
        {
            var theme = service.BuildTheme(TenantBranding.From(TenantInfo.From(tenant)));

            Hex(theme.PaletteLight.Primary).Should().Be(tenant.PrimaryColor);
            Hex(theme.PaletteDark.Primary).Should().Be(tenant.PrimaryColor);
            Hex(theme.PaletteLight.AppbarBackground).Should().Be(tenant.PrimaryColor);
            Hex(theme.PaletteLight.PrimaryContrastText).Should().Be(HexColor.ContrastText(tenant.PrimaryColor));
        }
    }

    [Fact]
    public void Each_tenant_gets_its_own_theme_instance_and_colors_do_not_leak()
    {
        var service = new TenantThemeService();
        var green = service.BuildTheme(new TenantBranding("#2E7D32"));
        var red = service.BuildTheme(new TenantBranding("#C62828"));
        var greenAgain = service.BuildTheme(new TenantBranding("#2e7d32"));

        green.Should().NotBeSameAs(red);
        greenAgain.Should().BeSameAs(green); // caché por color normalizado
        Hex(green.PaletteLight.Primary).Should().Be("#2E7D32");
        Hex(red.PaletteLight.Primary).Should().Be("#C62828");
    }

    [Fact]
    public void Invalid_color_falls_back_to_platform_theme()
    {
        var theme = new TenantThemeService().BuildTheme(new TenantBranding("no-es-color"));
        Hex(theme.PaletteLight.Primary).Should().Be(TenantBranding.Platform.PrimaryColor);
    }

    [Fact]
    public void Provider_renders_mud_theme_with_tenant_primary_color_for_100_tenants()
    {
        foreach (var tenant in TestData.Tenants())
        {
            var info = TenantInfo.From(tenant);

            var cut = Render<TenantThemeProvider>(p => p.Add(x => x.Tenant, info));

            var mudProvider = cut.FindComponent<MudThemeProvider>();
            Hex(mudProvider.Instance.Theme!.PaletteLight.Primary).Should().Be(tenant.PrimaryColor);
            cut.Markup.Should().Contain("--mud-palette-primary:");
            cut.Markup.ToLowerInvariant().Should().Contain(CssRgb(tenant.PrimaryColor));
        }
    }

    [Fact]
    public void Two_stores_render_two_different_primary_colors()
    {
        var green = TenantInfo.From(Tenant.Create("Verde", "verde", "#2E7D32"));
        var red = TenantInfo.From(Tenant.Create("Rojo", "rojo", "#C62828"));

        var greenCut = Render<TenantThemeProvider>(p => p.Add(x => x.Tenant, green));
        var redCut = Render<TenantThemeProvider>(p => p.Add(x => x.Tenant, red));

        greenCut.Markup.ToLowerInvariant().Should().Contain(CssRgb("#2E7D32")).And.NotContain(CssRgb("#C62828"));
        redCut.Markup.ToLowerInvariant().Should().Contain(CssRgb("#C62828")).And.NotContain(CssRgb("#2E7D32"));
    }

    [Fact]
    public void Provider_without_tenant_uses_platform_theme_and_updates_when_tenant_changes()
    {
        var cut = Render<TenantThemeProvider>();
        Hex(cut.Instance.Theme.PaletteLight.Primary).Should().Be(TenantBranding.Platform.PrimaryColor);

        var blue = TenantInfo.From(Tenant.Create("Azul", "azul", "#1565C0"));
        cut.Render(p => p.Add(x => x.Tenant, blue));

        Hex(cut.Instance.Theme.PaletteLight.Primary).Should().Be("#1565C0");
    }

    /// <summary>MudBlazor emite las variables CSS como rgba(r,g,b,a); comparamos el prefijo rgb.</summary>
    private static string CssRgb(string hex)
    {
        var c = new MudColor(hex);
        return $"rgba({c.R},{c.G},{c.B}";
    }
}
