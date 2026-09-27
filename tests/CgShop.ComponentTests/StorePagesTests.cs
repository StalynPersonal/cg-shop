using Bunit;
using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Store;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace CgShop.ComponentTests;

/// <summary>Tienda renderizada con 100 productos reales del tenant.</summary>
public sealed class StorePagesTests : StoreTestContext
{
    [Fact]
    public async Task Catalog_shows_first_page_of_100_products()
    {
        await SeedProductsAsync();

        var cut = Render<CatalogPage>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='product-card']").Should().HaveCount(12));
        cut.Markup.Should().Contain("100 productos");
        cut.FindComponents<MudPagination>().Should().ContainSingle(); // 100 / 12 => 9 páginas
    }

    [Theory]
    [InlineData("ropa", ProductCategory.Clothing)]
    [InlineData("gorras", ProductCategory.Caps)]
    [InlineData("relojes", ProductCategory.Watches)]
    [InlineData("perfumes", ProductCategory.Perfumes)]
    [InlineData("calzados", ProductCategory.Footwear)]
    public async Task Category_route_filters_products(string slug, ProductCategory category)
    {
        await SeedProductsAsync();

        var cut = Render<CatalogPage>(p => p.AddCascadingValue(HostContext).Add(x => x.CategorySlug, slug));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("20 productos"));
        cut.Markup.Should().Contain(category.DisplayName());
    }

    [Fact]
    public async Task Product_page_shows_variants_and_attributes_of_a_perfume()
    {
        await SeedProductsAsync();
        var perfume = TestData.Products().First(p => p.Category == ProductCategory.Perfumes);

        var cut = Render<ProductPage>(p => p.AddCascadingValue(HostContext).Add(x => x.Slug, perfume.Slug));

        cut.WaitForAssertion(() => cut.Find("[data-testid='product-name']").TextContent.Should().Be(perfume.Name));
        cut.Markup.Should().Contain("Presentación").And.Contain("ml").And.Contain("Notas Salida");
        cut.Find("[data-testid='add-to-cart']").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Order_page_shows_bank_transfer_instructions_and_upload_for_pending_order()
    {
        await SeedProductsAsync(5);
        var order = (await SeedOrdersAsync(1))[0];

        Services.GetRequiredService<NavigationManager>().NavigateTo($"/pedido/{order.Number}?t={order.AccessToken}");
        var cut = Render<OrderStatusPage>(p => p.AddCascadingValue(HostContext)
            .Add(x => x.Number, order.Number));

        cut.WaitForAssertion(() => cut.Find("[data-testid='payment-instructions']"));
        cut.Markup.Should().Contain("Banco Popular").And.Contain("800-1").And.Contain(order.Number);
        cut.Find("[data-testid='upload-receipt']");
        cut.Find("[data-testid='whatsapp-button']").GetAttribute("href").Should().StartWith("https://wa.me/18095551234");
        cut.Markup.Should().Contain(OrderStatus.PendingPaymentValidation.DisplayName());
    }

    [Fact]
    public async Task Order_page_with_wrong_token_does_not_reveal_order()
    {
        await SeedProductsAsync(5);
        var order = (await SeedOrdersAsync(1))[0];

        Services.GetRequiredService<NavigationManager>().NavigateTo($"/pedido/{order.Number}?t=incorrecto");
        var cut = Render<OrderStatusPage>(p => p.AddCascadingValue(HostContext)
            .Add(x => x.Number, order.Number));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pedido no encontrado"));
        cut.Markup.Should().NotContain("Banco Popular");
    }
}
