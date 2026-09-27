using Bunit;
using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CgShop.ComponentTests;

/// <summary>El editor de producto muestra al propietario quién cambió qué.</summary>
public sealed class ProductHistoryTests : StoreTestContext
{
    private async Task<Guid> SeedProductWithChangesAsync()
    {
        var service = new ProductAdminService(new DirectFactory(this), TenantContext,
            NSubstitute.Substitute.For<CgShop.Application.Common.IFileStorage>(), Clock, NullLogger<ProductAdminService>.Instance);
        var id = await service.CreateAsync(new ProductUpsertDto { Name = "Reloj Clásico", Category = ProductCategory.Watches },
            [new VariantUpsertDto { Sku = "REL-1", Color = "Negro", Price = 5000, InitialStock = 3 }], TestData.TenantAdmin);
        var (_, variants) = await service.GetForEditAsync(id);
        for (var i = 1; i <= TestData.BatchSize; i++)
            await service.ChangePriceAsync(variants[0].Id, 5000 + i, TestData.TenantStaff);
        return id;
    }

    [Fact]
    public async Task Owner_sees_the_100_price_changes_made_by_staff()
    {
        var id = await SeedProductWithChangesAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, id));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='history-entry']").Should().HaveCount(TestData.BatchSize + 1));
        cut.Find("[data-testid='history-entry']").TextContent
            .Should().Contain("Cambio de precio").And.Contain(TestData.TenantStaff.DisplayName).And.Contain("(Empleado)")
            .And.Contain("REL-1: 5,099.00 → 5,100.00");
    }

    [Fact]
    public async Task Staff_does_not_see_the_history()
    {
        var id = await SeedProductWithChangesAsync();
        AuthorizeAs(TestData.TenantStaff);

        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, id));

        cut.WaitForAssertion(() => cut.Find("[data-testid='gallery-editor']"));
        cut.FindAll("[data-testid='product-history']").Should().BeEmpty();
    }
}
