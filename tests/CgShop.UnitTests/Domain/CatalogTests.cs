using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Tests.Shared;

namespace CgShop.UnitTests.Domain;

public class CatalogTests
{
    [Fact]
    public void Creates_100_products_across_all_five_categories()
    {
        var products = TestData.Products().ToList();

        products.Should().HaveCount(TestData.BatchSize);
        products.Select(p => p.Category).Distinct().Should().HaveCount(5);
        products.SelectMany(p => p.Variants).Select(v => v.Sku).Should().OnlyHaveUniqueItems();
        products.Where(p => p.Category == ProductCategory.Perfumes)
            .Should().AllSatisfy(p =>
            {
                p.Attributes.Should().ContainKey("NotasSalida");
                p.Variants.Should().AllSatisfy(v => v.VolumeMl.Should().BePositive());
            });
        products.Where(p => p.Category is ProductCategory.Footwear or ProductCategory.Clothing)
            .SelectMany(p => p.Variants).Should().AllSatisfy(v => v.Size.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public void Reserve_release_and_commit_keep_stock_consistent_for_100_variants()
    {
        var variants = TestData.Products(stockPerVariant: 10).SelectMany(p => p.Variants).Take(TestData.BatchSize).ToList();
        variants.Should().HaveCount(TestData.BatchSize);

        foreach (var v in variants)
        {
            v.Reserve(4);
            v.Available.Should().Be(6);

            v.Release(1);
            v.StockReserved.Should().Be(3);

            v.CommitReservation(3);
            v.StockOnHand.Should().Be(7);
            v.StockReserved.Should().Be(0);
            v.Available.Should().Be(7);
        }
    }

    [Fact]
    public void Cannot_reserve_more_than_available()
    {
        foreach (var v in TestData.Products(stockPerVariant: 5).SelectMany(p => p.Variants).Take(TestData.BatchSize))
        {
            v.Reserve(5);
            v.Invoking(x => x.Reserve(1)).Should().Throw<DomainException>().WithMessage("*Stock insuficiente*");
            v.Invoking(x => x.CommitReservation(6)).Should().Throw<DomainException>();
            v.Invoking(x => x.Release(6)).Should().Throw<DomainException>();
            v.Invoking(x => x.Reserve(0)).Should().Throw<DomainException>();
        }
    }

    [Fact]
    public void Stock_adjustment_cannot_go_below_reserved()
    {
        var v = TestData.Product(1, stockPerVariant: 10).Variants[0];
        v.Reserve(8);

        v.Invoking(x => x.AdjustStock(-3)).Should().Throw<DomainException>();
        v.AdjustStock(-2);
        v.StockOnHand.Should().Be(8);
        v.AdjustStock(20);
        v.Available.Should().Be(20);
    }

    [Theory]
    [InlineData(ProductCategory.Footwear)]
    [InlineData(ProductCategory.Clothing)]
    [InlineData(ProductCategory.Caps)]
    public void Sized_categories_require_size(ProductCategory category)
    {
        var product = Product.Create("Prod", category);
        product.Invoking(p => p.AddVariant("X-1", 100, 1, size: null, color: "Rojo"))
            .Should().Throw<DomainException>().WithMessage("*talla*");
    }

    [Fact]
    public void Perfume_requires_volume_and_duplicate_sku_is_rejected()
    {
        var perfume = Product.Create("Eau", ProductCategory.Perfumes);
        perfume.Invoking(p => p.AddVariant("P-1", 100, 1)).Should().Throw<DomainException>().WithMessage("*volumen*");

        perfume.AddVariant("p-1", 100, 1, volumeMl: 50);
        perfume.Invoking(p => p.AddVariant("P-1 ", 100, 1, volumeMl: 100))
            .Should().Throw<DomainException>().WithMessage("*ya existe*");
    }

    [Fact]
    public void Variant_with_reserved_stock_cannot_be_removed()
    {
        var product = TestData.Product(3);
        var variant = product.Variants[0];
        variant.Reserve(1);

        product.Invoking(p => p.RemoveVariant(variant.Id)).Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("Tenis Nike Air Max 90", "tenis-nike-air-max-90")]
    [InlineData("Perfume Ámbar & Jazmín", "perfume-ambar-jazmin")]
    [InlineData("  Gorra  New-Era  ", "gorra-new-era")]
    public void Slugify_normalizes_names(string input, string expected) =>
        Product.Slugify(input).Should().Be(expected);
}
