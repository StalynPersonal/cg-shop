using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace CgShop.UnitTests.Application;

public class CatalogServiceTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private InMemoryDb.Factory _factory = null!;

    public async Task InitializeAsync()
    {
        var tenant = await _db.AddTenantAsync();
        _factory = _db.For(tenant);
        await using var ctx = _factory.CreateAppDbContext();
        ctx.Products.AddRange(TestData.Products(TestData.BatchSize, tenant.Id));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Paginates_100_products()
    {
        var service = new CatalogService(_factory);

        var first = await service.SearchAsync(new CatalogQuery(PageSize: 24));
        var last = await service.SearchAsync(new CatalogQuery(Page: 5, PageSize: 24));

        first.TotalCount.Should().Be(TestData.BatchSize);
        first.TotalPages.Should().Be(5);
        first.Items.Should().HaveCount(24);
        last.Items.Should().HaveCount(4);
        first.Items.Select(i => i.Id).Should().NotIntersectWith(last.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData(ProductCategory.Clothing)]
    [InlineData(ProductCategory.Caps)]
    [InlineData(ProductCategory.Watches)]
    [InlineData(ProductCategory.Perfumes)]
    [InlineData(ProductCategory.Footwear)]
    public async Task Filters_by_category(ProductCategory category)
    {
        var result = await new CatalogService(_factory).SearchAsync(new CatalogQuery(category, PageSize: 200));

        result.TotalCount.Should().Be(20); // 100 productos repartidos en 5 categorías
        result.Items.Should().OnlyContain(p => p.Category == category);
    }

    [Fact]
    public async Task Filters_by_size_color_and_price_and_sorts()
    {
        var service = new CatalogService(_factory);

        var bySize = await service.SearchAsync(new CatalogQuery(Size: "42", PageSize: 200));
        bySize.Items.Should().NotBeEmpty().And.OnlyContain(p => p.Category == ProductCategory.Footwear);

        var byColor = await service.SearchAsync(new CatalogQuery(Color: "Rojo", PageSize: 200));
        byColor.Items.Should().NotBeEmpty();
        byColor.TotalCount.Should().BeLessThan(TestData.BatchSize);

        var byPrice = await service.SearchAsync(new CatalogQuery(MinPrice: 1000, MaxPrice: 2000, Sort: CatalogSort.PriceAsc, PageSize: 200));
        byPrice.Items.Should().OnlyContain(p => p.MinPrice >= 1000 && p.MinPrice <= 2000);
        byPrice.Items.Select(p => p.MinPrice).Should().BeInAscendingOrder();

        var desc = await service.SearchAsync(new CatalogQuery(Sort: CatalogSort.PriceDesc, PageSize: 200));
        desc.Items.Select(p => p.MinPrice).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task Search_matches_name_or_brand_and_inactive_products_are_hidden()
    {
        var service = new CatalogService(_factory);
        var nike = await service.SearchAsync(new CatalogQuery(Search: "Nike", PageSize: 200));
        nike.Items.Should().OnlyContain(p => p.Brand == "Nike");
        nike.TotalCount.Should().Be(10);

        var admin = new ProductAdminService(_factory, _factory.Context, NSubstitute.Substitute.For<IFileStorage>(), TimeProvider.System, NullLogger<ProductAdminService>.Instance);
        foreach (var p in nike.Items)
            await admin.SetActiveAsync(p.Id, false, TestData.TenantStaff);

        (await service.SearchAsync(new CatalogQuery(Search: "Nike"))).TotalCount.Should().Be(0);
        (await service.SearchAsync(new CatalogQuery(PageSize: 200))).TotalCount.Should().Be(TestData.BatchSize - 10);
        (await service.GetBySlugAsync(nike.Items[0].Slug)).Should().BeNull();
    }

    [Fact]
    public async Task Detail_and_facets()
    {
        var service = new CatalogService(_factory);
        var perfumes = await service.SearchAsync(new CatalogQuery(ProductCategory.Perfumes));
        var detail = await service.GetBySlugAsync(perfumes.Items[0].Slug);

        detail!.Attributes.Should().ContainKey("NotasCorazon");
        detail.Variants.Should().OnlyContain(v => v.VolumeMl > 0);

        var facets = await service.GetFacetsAsync();
        facets.Categories.Should().HaveCount(5);
        facets.Sizes.Should().StartWith(["36", "37"]); // numéricos primero, ordenados
        facets.Colors.Should().Contain(["Negro", "Blanco"]);
    }

    [Fact]
    public void Sizes_are_sorted_numerically_then_by_clothing_order()
    {
        CatalogService.SortSizes(["XL", "42", "S", "38", "M", "Ajustable", "40.5"])
            .Should().Equal("38", "40.5", "42", "S", "M", "XL", "Ajustable");
    }
}

public class ProductAdminServiceTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private InMemoryDb.Factory _factory = null!;
    private ProductAdminService _service = null!;

    public async Task InitializeAsync()
    {
        _factory = _db.For(await _db.AddTenantAsync());
        _service = new ProductAdminService(_factory, _factory.Context, NSubstitute.Substitute.For<IFileStorage>(), TimeProvider.System, NullLogger<ProductAdminService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (ProductUpsertDto, List<VariantUpsertDto>) Dto(int i)
    {
        var source = TestData.Product(i);
        return (new ProductUpsertDto
            {
                Name = source.Name, Category = source.Category, Brand = source.Brand,
                Attributes = new Dictionary<string, string>(source.Attributes)
            },
            source.Variants.Select(v => new VariantUpsertDto
            {
                Sku = v.Sku, Size = v.Size, Color = v.Color, VolumeMl = v.VolumeMl, Price = v.Price,
                InitialStock = v.StockOnHand
            }).ToList());
    }

    [Fact]
    public async Task Creates_100_products_and_lists_them()
    {
        for (var i = 1; i <= TestData.BatchSize; i++)
        {
            var (p, v) = Dto(i);
            await _service.CreateAsync(p, v, TestData.TenantStaff);
        }

        var page = await _service.ListAsync(null, null, 1, 200);
        page.TotalCount.Should().Be(TestData.BatchSize);
        page.Items.Should().OnlyContain(r => r.VariantCount > 0 && r.StockOnHand > 0 && r.IsActive);

        var inventory = await _service.GetInventoryAsync();
        inventory.Should().HaveCount(page.Items.Sum(r => r.VariantCount));
    }

    [Fact]
    public async Task Duplicate_sku_is_rejected_and_duplicate_names_get_unique_slugs()
    {
        var (p, v) = Dto(1);
        await _service.CreateAsync(p, v, TestData.TenantAdmin);

        await _service.Invoking(s => s.CreateAsync(p, v, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*SKU*");

        var other = v.Select(x => new VariantUpsertDto
        {
            Sku = x.Sku + "-B", Size = x.Size, Color = x.Color, VolumeMl = x.VolumeMl, Price = x.Price, InitialStock = 1
        }).ToList();
        var id = await _service.CreateAsync(p, other, TestData.TenantAdmin);

        await using var ctx = _factory.CreateAppDbContext();
        (await ctx.Products.FindAsync(id))!.Slug.Should().EndWith("-2");
    }

    [Fact]
    public async Task Customer_cannot_manage_catalog_and_staff_cannot_delete()
    {
        var (p, v) = Dto(1);
        await _service.Invoking(s => s.CreateAsync(p, v, TestData.Customer)).Should().ThrowAsync<ForbiddenException>();

        var id = await _service.CreateAsync(p, v, TestData.TenantStaff);
        await _service.Invoking(s => s.DeleteAsync(id, TestData.TenantStaff)).Should().ThrowAsync<ForbiddenException>();
        (await _service.DeleteAsync(id, TestData.TenantAdmin)).Should().BeTrue(); // sin ventas => borrado físico
    }

    [Fact]
    public async Task Stock_adjustments_on_100_variants()
    {
        for (var i = 1; i <= 50; i++)
        {
            var (p, v) = Dto(i);
            await _service.CreateAsync(p, v, TestData.TenantStaff);
        }

        var variants = (await _service.GetInventoryAsync()).Take(TestData.BatchSize).ToList();
        variants.Should().HaveCount(TestData.BatchSize);
        foreach (var row in variants)
            (await _service.AdjustStockAsync(row.VariantId, 5, "Compra a proveedor", TestData.TenantStaff))
                .Should().Be(row.StockOnHand + 5);

        await _service.Invoking(s => s.AdjustStockAsync(variants[0].VariantId, -1000, "Merma", TestData.TenantStaff))
            .Should().ThrowAsync<DomainException>();
        await _service.Invoking(s => s.AdjustStockAsync(variants[0].VariantId, 1, " ", TestData.TenantStaff))
            .Should().ThrowAsync<DomainException>();

        var low = await _service.GetInventoryAsync(maxAvailable: 0);
        low.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_manual_adjustment_by_staff_is_recorded_and_visible_only_to_owner()
    {
        for (var i = 1; i <= 50; i++)
        {
            var (p, v) = Dto(i);
            await _service.CreateAsync(p, v, TestData.TenantAdmin);
        }

        var variants = (await _service.GetInventoryAsync()).Take(TestData.BatchSize).ToList();
        variants.Should().HaveCount(TestData.BatchSize);
        foreach (var (row, i) in variants.Select((r, i) => (r, i)))
        {
            var delta = i % 2 == 0 ? 5 : -3;
            await _service.AdjustStockAsync(row.VariantId, delta, $"Conteo físico {i}", TestData.TenantStaff);
        }

        var manual = await _service.GetMovementsAsync(
            new StockMovementQuery(StockMovementType.ManualAdjustment, PageSize: 200), TestData.TenantAdmin);
        manual.TotalCount.Should().Be(TestData.BatchSize);
        manual.Items.Should().AllSatisfy(m =>
        {
            m.UserName.Should().Be(TestData.TenantStaff.DisplayName);
            m.UserRole.Should().Be(Roles.TenantStaff);
            m.Reason.Should().StartWith("Conteo físico");
            m.StockAfter.Should().Be(m.StockBefore + m.Quantity);
        });

        var byVariant = await _service.GetMovementsAsync(
            new StockMovementQuery(VariantId: variants[0].VariantId), TestData.TenantAdmin);
        byVariant.Items.Select(m => m.Type).Should().BeEquivalentTo([StockMovementType.ManualAdjustment, StockMovementType.InitialStock]);

        await _service.Invoking(s => s.GetMovementsAsync(new StockMovementQuery(), TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Creating_products_records_initial_stock_and_deleting_records_the_exit()
    {
        var ids = new List<Guid>();
        for (var i = 1; i <= TestData.BatchSize; i++)
        {
            var (p, v) = Dto(i);
            ids.Add(await _service.CreateAsync(p, v, TestData.TenantStaff));
        }

        var initial = await _service.GetMovementsAsync(
            new StockMovementQuery(StockMovementType.InitialStock, PageSize: 500), TestData.TenantAdmin);
        initial.TotalCount.Should().Be(TestData.Products().Sum(p => p.Variants.Count));

        (await _service.DeleteAsync(ids[0], TestData.TenantAdmin)).Should().BeTrue();
        var exits = await _service.GetMovementsAsync(new StockMovementQuery(Search: "Producto eliminado"), TestData.TenantAdmin);
        exits.Items.Should().NotBeEmpty().And.OnlyContain(m => m.Quantity < 0 && m.StockAfter == 0);
    }

    [Fact]
    public async Task Update_add_and_remove_variants()
    {
        var (p, v) = Dto(3); // Perfumes? categoría de i=3 => Perfumes
        var id = await _service.CreateAsync(p, v, TestData.TenantStaff);

        p.Name = "Nombre Editado";
        p.Attributes["Nota"] = "Cítrica";
        await _service.UpdateAsync(id, p, TestData.TenantStaff);

        var variantId = await _service.AddVariantAsync(id, new VariantUpsertDto
        {
            Sku = "NUEVO-1", VolumeMl = 200, Size = "U", Color = "Negro", Price = 999, InitialStock = 3
        }, TestData.TenantStaff);
        await _service.ChangePriceAsync(variantId, 1200, TestData.TenantStaff);

        var (edited, variants) = await _service.GetForEditAsync(id);
        edited.Name.Should().Be("Nombre Editado");
        edited.Attributes.Should().ContainKey("Nota");
        variants.Should().ContainSingle(x => x.Sku == "NUEVO-1").Which.Price.Should().Be(1200);

        await _service.RemoveVariantAsync(id, variantId, TestData.TenantStaff);
        (await _service.GetForEditAsync(id)).Variants.Should().NotContain(x => x.Sku == "NUEVO-1");
    }
}
