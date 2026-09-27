using AngleSharp.Dom;
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
    public async Task Clicking_a_variant_marks_it_and_updates_the_sku_for_100_products()
    {
        await SeedProductsAsync();
        var checkedChips = 0;

        foreach (var product in TestData.Products(TestData.BatchSize, Tenant.Id))
        {
            var cut = Render<ProductPage>(p => p.AddCascadingValue(HostContext).Add(x => x.Slug, product.Slug));
            cut.WaitForAssertion(() => cut.Find("[data-testid='product-name']").TextContent.Should().Be(product.Name));

            foreach (var group in new[] { "size", "color", "volume" })
            {
                var labels = cut.FindAll($"[data-testid='{group}-chip']").Select(c => c.TextContent.Trim()).ToList();
                foreach (var label in labels)
                {
                    cut.FindAll($"[data-testid='{group}-chip']").Single(c => c.TextContent.Trim() == label).Click();

                    var selected = cut.FindAll($"[data-testid='{group}-chip'].selected");
                    selected.Should().ContainSingle($"{product.Name}: solo un chip de {group} marcado");
                    selected[0].TextContent.Trim().Should().Be(label);

                    // La variante resuelta corresponde al chip pulsado (y el color se ajusta a la talla).
                    var variant = product.Variants.Single(v => cut.Markup.Contains($"SKU {v.Sku}"));
                    var value = group switch
                    {
                        "size" => variant.Size,
                        "color" => variant.Color,
                        _ => $"{variant.VolumeMl} ml"
                    };
                    value.Should().Be(label);
                    checkedChips++;
                }
            }
            cut.Dispose();
        }

        checkedChips.Should().BeGreaterThan(TestData.BatchSize);
    }

    [Fact]
    public async Task Size_without_stock_is_read_only()
    {
        await SeedProductsAsync();
        var product = Product.Create("Tenis Agotados", ProductCategory.Footwear, "Nike", null, null);
        product.AddVariant("AG-40-NG", 5000m, 0, "40", "Negro");
        product.AddVariant("AG-40-BL", 5000m, 0, "40", "Blanco");
        product.AddVariant("AG-41-NG", 5000m, 5, "41", "Negro");
        await using (var db = CreateDb())
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
        }

        var cut = Render<ProductPage>(p => p.AddCascadingValue(HostContext).Add(x => x.Slug, product.Slug));
        cut.WaitForAssertion(() => cut.Find("[data-testid='product-name']").TextContent.Should().Be(product.Name));

        IElement Chip(string group, string text) =>
            cut.FindAll($"[data-testid='{group}-chip']").Single(c => c.TextContent.Trim() == text);

        Chip("size", "40").ClassList.Should().Contain("mud-disabled");
        Chip("size", "41").ClassList.Should().NotContain("mud-disabled").And.Contain("selected");
        Chip("color", "Blanco").ClassList.Should().Contain("mud-disabled"); // sin stock en ninguna talla
        cut.Markup.Should().Contain("SKU AG-41-NG");
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
