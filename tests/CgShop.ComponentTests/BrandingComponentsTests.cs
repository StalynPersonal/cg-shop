using Bunit;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CgShop.ComponentTests;

public sealed class BrandingComponentsTests : MudTestContext
{
    [Fact]
    public void Brand_preview_shows_selected_primary_color_for_100_tenants()
    {
        foreach (var tenant in TestData.Tenants())
        {
            var cut = Render<BrandPreview>(p => p
                .Add(x => x.Name, tenant.Name)
                .Add(x => x.Slug, tenant.Slug)
                .Add(x => x.PrimaryColor, tenant.PrimaryColor));

            cut.Markup.Should().Contain($"background:{tenant.PrimaryColor}");
            cut.Markup.Should().Contain(tenant.Name).And.Contain($"{tenant.Slug}.tudominio.com");
        }
    }

    [Fact]
    public void Brand_preview_uses_readable_text_color()
    {
        var dark = Render<BrandPreview>(p => p.Add(x => x.PrimaryColor, "#1B5E20"));
        dark.Markup.Should().Contain("color:#FFFFFF");

        var light = Render<BrandPreview>(p => p.Add(x => x.PrimaryColor, "#FFEB3B"));
        light.Markup.Should().Contain("color:#000000");
    }

    [Fact]
    public void Color_picker_presets_emit_the_selected_hex()
    {
        string? selected = null;
        var cut = Render<BrandColorPicker>(p => p
            .Add(x => x.Required, true)
            .Add(x => x.Value, null)
            .Add(x => x.ValueChanged, v => selected = v));

        foreach (var (_, hex) in BrandColorPicker.Presets)
        {
            cut.Find($"[data-testid='preset-{hex}']").Click();
            selected.Should().Be(hex);
        }
    }

    [Theory]
    [InlineData(OrderStatus.PendingPaymentValidation, "Pendiente de validación de pago")]
    [InlineData(OrderStatus.PaymentValidated, "Pago validado")]
    [InlineData(OrderStatus.PaymentRejected, "Pago rechazado")]
    [InlineData(OrderStatus.Delivered, "Entregado")]
    public void Order_status_chip_renders_spanish_label(OrderStatus status, string label)
    {
        var cut = Render<OrderStatusChip>(p => p.Add(x => x.Status, status));
        cut.Markup.Should().Contain(label);
    }
}
