using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;

namespace CgShop.UnitTests.Application;

/// <summary>Categorías Celulares (capacidad + color) y Electrónica (color) con 100 productos.</summary>
public class PhonesAndElectronicsTests : IAsyncLifetime
{
    private static readonly string[] Capacities = ["1 TB", "64 GB", "512 GB", "128 GB", "256 GB"];
    private readonly InMemoryDb _db = new();
    private InMemoryDb.Factory _factory = null!;

    public async Task InitializeAsync()
    {
        var tenant = await _db.AddTenantAsync();
        _factory = _db.For(tenant);
        await using var ctx = _factory.CreateAppDbContext();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var phone = i % 2 == 0;
            var product = Product.Create(phone ? $"Teléfono Modelo {i:000}" : $"Audífonos Modelo {i:000}",
                phone ? ProductCategory.Phones : ProductCategory.Electronics, i % 3 == 0 ? "Apple" : "Samsung");
            product.TenantId = tenant.Id;
            if (phone)
                foreach (var capacity in Capacities.Take(1 + i % 5))
                    product.AddVariant($"TEL-{i:000}-{capacity.Replace(" ", "")}", 20000 + i, 5, capacity, "Negro");
            else
                product.AddVariant($"AUD-{i:000}", 3000 + i, 5, color: i % 4 == 0 ? "Blanco" : "Negro");
            ctx.Products.Add(product);
        }

        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Filters_and_search_find_phones_and_electronics_with_or_without_accents()
    {
        var service = new CatalogService(_factory);
        async Task<int> Count(CatalogQuery q) => (await service.SearchAsync(q with { PageSize = 200 })).TotalCount;

        (await Count(new CatalogQuery(ProductCategory.Phones))).Should().Be(50);
        (await Count(new CatalogQuery(ProductCategory.Electronics))).Should().Be(50);
        (await Count(new CatalogQuery(Search: "celular"))).Should().Be(50);
        (await Count(new CatalogQuery(Search: "celulares"))).Should().Be(50);
        (await Count(new CatalogQuery(Search: "electronica"))).Should().Be(50);
        (await Count(new CatalogQuery(Search: "Electrónica"))).Should().Be(50);
        (await Count(new CatalogQuery(Search: "celular apple"))).Should().Be(Enumerable.Range(0, 100).Count(i => i % 2 == 0 && i % 3 == 0));
        (await Count(new CatalogQuery(Size: "256 GB"))).Should().Be(Enumerable.Range(0, 100).Count(i => i % 2 == 0 && 1 + i % 5 >= 5));
    }

    [Fact]
    public async Task Capacities_are_sorted_by_size_not_alphabetically()
    {
        var facets = await new CatalogService(_factory).GetFacetsAsync(ProductCategory.Phones);
        facets.Sizes.Should().Equal("64 GB", "128 GB", "256 GB", "512 GB", "1 TB");

        CatalogService.SortSizes(["1 TB", "128 GB", "40", "M", "64 GB", "S"])
            .Should().Equal("40", "64 GB", "128 GB", "1 TB", "S", "M");
    }

    [Fact]
    public void Phones_use_capacity_and_electronics_only_color()
    {
        ProductCategory.Phones.SizeLabel().Should().Be("Capacidad");
        ProductCategory.Footwear.SizeLabel().Should().Be("Talla");
        ProductCategory.Phones.VariantDimensions().Should().Be((true, true, false));
        ProductCategory.Electronics.VariantDimensions().Should().Be((false, true, false));
        ProductCategory.Phones.DisplayName().Should().Be("Celulares");
        ProductCategory.Electronics.DisplayName().Should().Be("Electrónica");
    }
}
