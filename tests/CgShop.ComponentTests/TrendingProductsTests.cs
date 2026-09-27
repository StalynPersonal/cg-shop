using Bunit;
using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;

namespace CgShop.ComponentTests;

public sealed class TrendingProductsTests : MudTestContext
{
    [Fact]
    public void Renders_100_cards_with_brand_name_price_and_link()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var products = Enumerable.Range(0, TestData.BatchSize).Select(i => new ProductCardDto(Guid.NewGuid(),
            $"producto-{i}", $"Producto {i}", i % 2 == 0 ? "Apple" : null, ProductCategory.Electronics,
            i % 3 == 0 ? $"/imagenes/{i}" : null, 1000 + i, i % 10 == 0 ? 0 : 5)).ToList();

        var cut = Render<TrendingProducts>(p => p.Add(x => x.Products, products).Add(x => x.Currency, "DOP"));

        var cards = cut.FindAll("[data-testid='trending-card']");
        cards.Should().HaveCount(TestData.BatchSize);
        cards[0].GetAttribute("href").Should().Be("/producto/producto-0");
        cards[0].TextContent.Should().Contain("Apple").And.Contain("Producto 0");
        cut.FindAll(".cg-trending-brand").Should().HaveCount(50);
        cut.FindAll(".cg-trending-image img").Should().HaveCount(34);
        cut.FindAll(".cg-trending-soldout").Should().HaveCount(10);

        cut.Find(".cg-trending-arrow.right").Click();
        JSInterop.VerifyInvoke("cgScroller.page");
    }

    [Fact]
    public void Nothing_is_rendered_without_products()
    {
        var cut = Render<TrendingProducts>(p => p.Add(x => x.Products, []));
        cut.Markup.Trim().Should().BeEmpty();
    }
}
